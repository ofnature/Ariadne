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
        Movement.PathFollower follower, Movement.MoveRequest move)
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
