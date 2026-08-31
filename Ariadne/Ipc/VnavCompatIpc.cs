using Ariadne.Config;
using Ariadne.Movement;
using Ariadne.Zone;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Ariadne.Ipc;

// The flip: registers the complete vnavmesh.* IPC contract (the 17-gate consumer survey in
// Mnemosyne's roadmap) so Olympus/Theseus/Charon/SealBreaker/rsr work unmodified with
// vnavmesh uninstalled. Registered ONLY when the real vnavmesh is absent — Dalamud IPC gate
// names are global and two providers would fight — probed via vnavmesh.Nav.IsReady at load.
// If vnavmesh is installed later mid-session its registration overwrites ours; reload
// Ariadne after uninstalling it to reclaim the gates.
//
// Shape notes vs the Ariadne.* surface: Query.Mesh.* here are SYNC (bounded blocking wait,
// SyncGate) because consumers don't await them; PointOnFloor uses vnavmesh's argument
// order (point, allowUnlandable, halfExtentXZ); BuildBitmap* return (min,max) bounds.
internal sealed class VnavCompatIpc : IDisposable
{
    private const int BitmapBudgetMs = 5000; // bitmaps rasterize server-side; vnavmesh blocked comparably in-process

    public bool Registered { get; }

    private readonly List<Action> _disposeActions = new();
    private readonly IDalamudPluginInterface _pi;
    private readonly AriadneConfig _config;

    public VnavCompatIpc(IDalamudPluginInterface pluginInterface, AriadneConfig config, Action saveConfig,
        MeshBroker broker, PathFollower follower, MoveRequest move,
        Func<bool> windowIsOpen, Action<bool> windowSetOpen, Action<string> log)
    {
        _pi = pluginInterface;
        _config = config;

        if (!config.EnableVnavCompat)
        {
            log("[VnavCompat] disabled by config — vnavmesh.* gates not claimed");
            return;
        }
        if (VnavmeshIsLoaded())
        {
            log("[VnavCompat] real vnavmesh is loaded — its gates stay untouched (uninstall it and reload Ariadne to flip)");
            return;
        }

        int Budget() => _config.SyncGateBudgetMs;

        // ---- Nav status / lifecycle ----
        Func("Nav.IsReady", () => broker.NavIsReady);
        Func("Nav.BuildProgress", () => broker.NavBuildProgress);
        Func("Nav.Reload", () => { _ = broker.RefreshAsync(); return true; });
        Func("Nav.Rebuild", () => { _ = broker.RefreshAsync(); return true; }); // rebuild-on-demand is Mnemosyne's call now
        Func("Nav.IsAutoLoad", () => true);         // Ariadne always auto-resolves meshes
        Action<bool>("Nav.SetAutoLoad", _ => { }); // no-op: nothing to disable

        // ---- Nav pathfinding (async-shaped in vnavmesh too — consumers await these) ----
        Func("Nav.Pathfind", (Vector3 from, Vector3 to, bool fly) => broker.FindPathAsync(from, to, fly));
        Func("Nav.PathfindWithTolerance", (Vector3 from, Vector3 to, bool fly, float tolerance) => broker.FindPathAsync(from, to, fly, tolerance));
        Func("Nav.PathfindAvoid", (Vector3 from, Vector3 to, bool fly, Vector3 avoidCenter, float avoidRadius) => broker.FindPathAsync(from, to, fly, null, avoidCenter, avoidRadius));
        Func("Nav.PathfindCancelable", (Vector3 from, Vector3 to, bool fly, CancellationToken _) => broker.FindPathAsync(from, to, fly)); // token unused: single-flight client, ms-scale queries
        Action("Nav.PathfindCancelAll", () => { }); // nothing queued client-side to cancel
        Func("Nav.PathfindInProgress", () => broker.PathfindInProgress);
        Func("Nav.PathfindNumQueued", () => broker.PathfindNumQueued);

        // ---- Query.Mesh: the sync-shaped gates (the reason this class exists) ----
        Func("Query.Mesh.NearestPoint", (Vector3 p, float halfExtentXZ, float halfExtentY)
            => SyncGate.Wait(broker.NearestPointAsync(p, halfExtentXZ, halfExtentY, false), Budget(), (Vector3?)null));
        Func("Query.Mesh.NearestPointReachable", (Vector3 p, float halfExtentXZ, float halfExtentY)
            => SyncGate.Wait(broker.NearestPointAsync(p, halfExtentXZ, halfExtentY, true), Budget(), (Vector3?)null));
        Func("Query.Mesh.IsPointOnMesh", (Vector3 p, float halfExtentY, bool allowUnreachable)
            => SyncGate.Wait(broker.IsPointOnMeshAsync(p, halfExtentY, allowUnreachable), Budget(), false));
        Func("Query.Mesh.PointOnFloor", (Vector3 p, bool allowUnlandable, float halfExtentXZ) // vnavmesh's arg order
            => SyncGate.Wait(broker.PointOnFloorAsync(p, halfExtentXZ, allowUnlandable), Budget(), (Vector3?)null));
        Func("Query.Mesh.FlagToPoint", ()
            => MapFlag.GetFlagPosition() is { } flag
                ? SyncGate.Wait(broker.PointOnFloorAsync(new(flag.X, 1024, flag.Y), 5, false), Budget(), (Vector3?)null)
                : null);

        // ---- Bitmaps (sync (min,max) in vnavmesh; bounded wait, generous budget) ----
        Func("Nav.BuildBitmap", (Vector3 start, string filename, float pixelSize)
            => SyncGate.Wait(broker.BuildBitmapBoundsAsync([start], filename, pixelSize), BitmapBudgetMs, default((Vector3, Vector3))));
        Func("Nav.BuildBitmapBounded", (Vector3 start, string filename, float pixelSize, Vector3 lo, Vector3 hi)
            => SyncGate.Wait(broker.BuildBitmapBoundsAsync([start], filename, pixelSize, lo, hi), BitmapBudgetMs, (lo, hi)));
        Func("Nav.BuildBitmapMulti", (List<Vector3> starts, string filename, float pixelSize)
            => SyncGate.Wait(broker.BuildBitmapBoundsAsync(starts, filename, pixelSize), BitmapBudgetMs, default((Vector3, Vector3))));
        Func("Nav.BuildBitmapMultiBounded", (List<Vector3> starts, string filename, float pixelSize, Vector3 lo, Vector3 hi)
            => SyncGate.Wait(broker.BuildBitmapBoundsAsync(starts, filename, pixelSize, lo, hi), BitmapBudgetMs, (lo, hi)));

        // ---- Path / SimpleMove (Ariadne's follower, vnavmesh's shapes) ----
        Action("Path.MoveTo", (List<Vector3> waypoints, bool fly) => follower.Move(waypoints, fly));
        Action("Path.Stop", move.Stop);
        Func("Path.IsRunning", () => follower.IsRunning);
        Func("Path.NumWaypoints", () => follower.Waypoints.Count);
        Func("Path.ListWaypoints", () => new List<Vector3>(follower.Waypoints));
        Func("Path.GetMovementAllowed", () => follower.MovementAllowed);
        Action<bool>("Path.SetMovementAllowed", v => follower.MovementAllowed = v);
        Func("Path.GetAlignCamera", () => _config.AlignCameraToMovement);
        Action<bool>("Path.SetAlignCamera", v => { _config.AlignCameraToMovement = v; saveConfig(); });
        Func("Path.GetTolerance", () => follower.Tolerance);
        Action<float>("Path.SetTolerance", v => follower.Tolerance = v);
        Func("SimpleMove.PathfindAndMoveTo", (Vector3 dest, bool fly) => move.MoveTo(dest, fly));
        Func("SimpleMove.PathfindAndMoveCloseTo", (Vector3 dest, bool fly, float range) => move.MoveTo(dest, fly, range));
        Func("SimpleMove.PathfindInProgress", () => move.TaskInProgress);

        // ---- Window / DTR ----
        Func("Window.IsOpen", windowIsOpen);
        Action<bool>("Window.SetOpen", windowSetOpen);
        Func("DTR.IsShown", () => _config.EnableDtrBar);
        Action<bool>("DTR.SetShown", v => { _config.EnableDtrBar = v; saveConfig(); });

        Registered = true;
        log("[VnavCompat] vnavmesh.* gates claimed — consumers run on Ariadne unmodified");
    }

    public void Dispose()
    {
        foreach (var a in _disposeActions)
            a();
    }

    private bool VnavmeshIsLoaded()
    {
        try
        {
            _pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // registration helpers, vnavmesh.* prefix
    private void Func<TRet>(string name, Func<TRet> f)
    { var p = _pi.GetIpcProvider<TRet>("vnavmesh." + name); p.RegisterFunc(f); _disposeActions.Add(p.UnregisterFunc); }
    private void Func<T1, TRet>(string name, Func<T1, TRet> f)
    { var p = _pi.GetIpcProvider<T1, TRet>("vnavmesh." + name); p.RegisterFunc(f); _disposeActions.Add(p.UnregisterFunc); }
    private void Func<T1, T2, TRet>(string name, Func<T1, T2, TRet> f)
    { var p = _pi.GetIpcProvider<T1, T2, TRet>("vnavmesh." + name); p.RegisterFunc(f); _disposeActions.Add(p.UnregisterFunc); }
    private void Func<T1, T2, T3, TRet>(string name, Func<T1, T2, T3, TRet> f)
    { var p = _pi.GetIpcProvider<T1, T2, T3, TRet>("vnavmesh." + name); p.RegisterFunc(f); _disposeActions.Add(p.UnregisterFunc); }
    private void Func<T1, T2, T3, T4, TRet>(string name, Func<T1, T2, T3, T4, TRet> f)
    { var p = _pi.GetIpcProvider<T1, T2, T3, T4, TRet>("vnavmesh." + name); p.RegisterFunc(f); _disposeActions.Add(p.UnregisterFunc); }
    private void Func<T1, T2, T3, T4, T5, TRet>(string name, Func<T1, T2, T3, T4, T5, TRet> f)
    { var p = _pi.GetIpcProvider<T1, T2, T3, T4, T5, TRet>("vnavmesh." + name); p.RegisterFunc(f); _disposeActions.Add(p.UnregisterFunc); }
    private void Action(string name, Action f)
    { var p = _pi.GetIpcProvider<object>("vnavmesh." + name); p.RegisterAction(f); _disposeActions.Add(p.UnregisterAction); }
    private void Action<T1>(string name, Action<T1> f)
    { var p = _pi.GetIpcProvider<T1, object>("vnavmesh." + name); p.RegisterAction(f); _disposeActions.Add(p.UnregisterAction); }
    private void Action<T1, T2>(string name, Action<T1, T2> f)
    { var p = _pi.GetIpcProvider<T1, T2, object>("vnavmesh." + name); p.RegisterAction(f); _disposeActions.Add(p.UnregisterAction); }
}
