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

    public AriadneIpc(IDalamudPluginInterface pluginInterface, MeshBroker broker, Func<string> currentCacheKey)
    {
        _pluginInterface = pluginInterface;

        RegisterFunc("IsConnected", () => broker.MnemosyneConnected);
        RegisterFunc("CurrentCacheKey", currentCacheKey);
        RegisterFunc("ZoneStatus", () => (int)broker.Current.Status);
        RegisterFunc("RequestMesh", broker.RequestMeshAsync);
        RegisterFunc("SeedVnavCache", broker.SeedVnavCacheAsync);
        RegisterFunc("FindPath", (Vector3 from, Vector3 to, bool fly) => broker.FindPathAsync(from, to, fly));
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

    private void RegisterFunc<TRet, T1, T2, T3>(string name, Func<T1, T2, T3, TRet> func)
    {
        var p = _pluginInterface.GetIpcProvider<T1, T2, T3, TRet>("Ariadne." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }
}
