using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace Ariadne.Ipc;

// Consumer-facing IPC surface (same provider pattern as vnavmesh's IPCProvider). Names and
// semantics are documented in the README; ZoneMeshStatus enum values are part of the
// contract, so reorder there only with a version bump.
internal sealed class AriadneIpc : IDisposable
{
    private readonly List<Action> _disposeActions = new();
    private readonly IDalamudPluginInterface _pluginInterface;

    public AriadneIpc(IDalamudPluginInterface pluginInterface, MeshBroker broker, Func<string> currentCacheKey,
        Movement.PathFollower follower, Movement.MoveRequest move, Func<int> syncBudgetMs)
    {
        _pluginInterface = pluginInterface;

        RegisterFunc("IsConnected", () => broker.MnemosyneConnected);
        RegisterFunc("CurrentCacheKey", currentCacheKey);
        RegisterFunc("ZoneStatus", () => (int)broker.Current.Status);
        RegisterFunc("RequestMesh", broker.RequestMeshAsync);
        RegisterFunc("SeedVnavCache", broker.SeedVnavCacheAsync);
        RegisterFunc("FindPath", (Vector3 from, Vector3 to, bool fly) => broker.FindPathAsync(from, to, fly));
        // feedback channel (Odysseus §2): report a traversal that succeeded where the mesh
        // said no ("direct"), or a planned leg that failed — becomes override evidence
        RegisterFunc("ReportTraversal", (Vector3 from, Vector3 to, string mode, bool success) => broker.ReportTraversalAsync(from, to, mode, success));

        // ---- vnavmesh gate parity, under the Ariadne. prefix (added 2026-08-24) ----------
        // Deliberately NOT registered as vnavmesh.* yet: with both prefixes live a consumer
        // can call Ariadne.Query.Mesh.NearestPoint and vnavmesh.Query.Mesh.NearestPoint on
        // the same input and diff the answers. Once those agree across a zone sweep, the
        // cutover is just re-registering these names.
        // Shape note: vnavmesh serves these synchronously from an in-process mesh; ours
        // cross a pipe, so they return Task<T>. The sync-shaped aliases come with the
        // cutover (Phase 2) — either a bounded blocking wait or a locally cached query.
        RegisterFunc("Nav.IsReady", () => broker.NavIsReady);
        RegisterFunc("Nav.BuildProgress", () => broker.NavBuildProgress);
        RegisterFunc("Nav.Pathfind", (Vector3 from, Vector3 to, bool fly) => broker.FindPathAsync(from, to, fly));
        RegisterFunc("Nav.PathfindWithTolerance", (Vector3 from, Vector3 to, bool fly, float tolerance)
            => broker.FindPathAsync(from, to, fly, tolerance));
        RegisterFunc("Nav.PathfindAvoid", (Vector3 from, Vector3 to, bool fly, Vector3 avoidCenter, float avoidRadius)
            => broker.FindPathAsync(from, to, fly, null, avoidCenter, avoidRadius));
        // The classified answer, which the vnavmesh-shaped gates structurally cannot carry:
        // they return a bare list, so "no path" and "your goal is 2 y off the mesh, stand
        // here instead" look identical. Returns (result, waypoints, nearest, partial).
        RegisterFunc("Nav.PathfindDetailed", (Vector3 from, Vector3 to, bool fly) =>
            broker.FindPathDetailedAsync(from, to, fly)
                .ContinueWith(t => (t.Result.Result, t.Result.Waypoints, t.Result.Nearest, t.Result.Partial)));
        RegisterFunc("Nav.PathfindInProgress", () => broker.PathfindInProgress);
        RegisterFunc("Nav.PathfindNumQueued", () => broker.PathfindNumQueued);

        RegisterFunc("Query.Mesh.NearestPoint", (Vector3 p, float halfExtentXZ, float halfExtentY)
            => broker.NearestPointAsync(p, halfExtentXZ, halfExtentY, false));
        RegisterFunc("Query.Mesh.NearestPointReachable", (Vector3 p, float halfExtentXZ, float halfExtentY)
            => broker.NearestPointAsync(p, halfExtentXZ, halfExtentY, true));
        RegisterFunc("Query.Mesh.IsPointOnMesh", (Vector3 p, float halfExtentY, bool allowUnreachable)
            => broker.IsPointOnMeshAsync(p, halfExtentY, allowUnreachable));
        RegisterFunc("Query.Mesh.PointOnFloor", (Vector3 p, float halfExtentXZ, bool allowUnreachable)
            => broker.PointOnFloorAsync(p, halfExtentXZ, allowUnreachable));
        // sync-shaped like vnavmesh's (bounded wait): the flag lives in AgentMap, which only
        // the game process can read — Ariadne resolves it and asks Mnemosyne for the floor
        RegisterFunc("Query.Mesh.FlagToPoint", ()
            => Zone.MapFlag.GetFlagPosition() is { } flag
                ? SyncGate.Wait(broker.PointOnFloorAsync(new(flag.X, 1024, flag.Y), 5, false), syncBudgetMs(), (Vector3?)null)
                : null);

        RegisterFunc("Nav.BuildBitmap", (List<Vector3> starts, string filename, float pixelSize)
            => broker.BuildBitmapAsync(starts, filename, pixelSize));
        RegisterFunc("Nav.BuildBitmapBounded", (List<Vector3> starts, string filename, float pixelSize, Vector3[] bounds)
            => broker.BuildBitmapAsync(starts, filename, pixelSize,
                bounds.Length > 0 ? bounds[0] : null, bounds.Length > 1 ? bounds[1] : null));

        // movement — same shapes as vnavmesh's Path.* / SimpleMove.* so a compat alias layer is trivial later
        RegisterAction("Path.MoveTo", (List<Vector3> waypoints, bool fly) => follower.Move(waypoints, fly));
        RegisterAction("Path.Stop", move.Stop);
        RegisterFunc("Path.IsRunning", () => follower.IsRunning);
        RegisterFunc("Path.NumWaypoints", () => follower.Waypoints.Count);
        RegisterFunc("Path.GetTolerance", () => follower.Tolerance);
        RegisterAction("Path.SetTolerance", (float v) => follower.Tolerance = v);
        RegisterFunc("Path.GetMovementAllowed", () => follower.MovementAllowed);
        RegisterAction("Path.SetMovementAllowed", (bool v) => follower.MovementAllowed = v);
        RegisterFunc("SimpleMove.PathfindAndMoveTo", (Vector3 dest, bool fly) => move.MoveTo(dest, fly));
        RegisterFunc("SimpleMove.PathfindAndMoveCloseTo", (Vector3 dest, bool fly, float range) => move.MoveTo(dest, fly, range));
        RegisterFunc("SimpleMove.PathfindInProgress", () => move.TaskInProgress);
    }

    public void Dispose()
    {
        foreach (var a in _disposeActions)
            a();
    }

    private void RegisterFunc<TRet>(string name, Func<TRet> func)
    {
        var p = _pluginInterface.GetIpcProvider<TRet>("Ariadne." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2>(string name, Func<T1, T2, TRet> func)
    {
        var p = _pluginInterface.GetIpcProvider<T1, T2, TRet>("Ariadne." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2, T3>(string name, Func<T1, T2, T3, TRet> func)
    {
        var p = _pluginInterface.GetIpcProvider<T1, T2, T3, TRet>("Ariadne." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2, T3, T4>(string name, Func<T1, T2, T3, T4, TRet> func)
    {
        var p = _pluginInterface.GetIpcProvider<T1, T2, T3, T4, TRet>("Ariadne." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2, T3, T4, T5>(string name, Func<T1, T2, T3, T4, T5, TRet> func)
    {
        var p = _pluginInterface.GetIpcProvider<T1, T2, T3, T4, T5, TRet>("Ariadne." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterAction(string name, Action func)
    {
        var p = _pluginInterface.GetIpcProvider<object>("Ariadne." + name);
        p.RegisterAction(func);
        _disposeActions.Add(p.UnregisterAction);
    }

    private void RegisterAction<T1>(string name, Action<T1> func)
    {
        var p = _pluginInterface.GetIpcProvider<T1, object>("Ariadne." + name);
        p.RegisterAction(func);
        _disposeActions.Add(p.UnregisterAction);
    }

    private void RegisterAction<T1, T2>(string name, Action<T1, T2> func)
    {
        var p = _pluginInterface.GetIpcProvider<T1, T2, object>("Ariadne." + name);
        p.RegisterAction(func);
        _disposeActions.Add(p.UnregisterAction);
    }
}
