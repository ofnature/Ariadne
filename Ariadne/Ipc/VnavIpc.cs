using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System;

namespace Ariadne.Ipc;

/// <summary>
/// vnavmesh, wrapped. Every call fails open — a missing vnavmesh degrades Ariadne to a pure
/// Mnemosyne front-end (its own IPC consumers still work), reported once rather than
/// throwing on every zone change. While Ariadne's compat layer holds the vnavmesh.* names
/// these gates would answer with Ariadne's own state, so vnavmesh is reported absent
/// instead of talked to through ourselves.
/// </summary>
internal sealed class VnavIpc
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Func<bool> _compatOwnsGates;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<bool>? _isReady;
    private ICallGateSubscriber<float>? _buildProgress;
    private ICallGateSubscriber<bool>? _reload;

    private bool _warned;

    public VnavIpc(IDalamudPluginInterface pluginInterface, Func<bool> compatOwnsGates, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _compatOwnsGates = compatOwnsGates;
        _log = log;
    }

    /// <summary>vnavmesh is loaded and answering IPC.</summary>
    public bool IsAvailable
    {
        get
        {
            if (_compatOwnsGates())
                return false;
            try
            {
                (_isReady ??= _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady")).InvokeFunc();
                _warned = false;
                return true;
            }
            catch
            {
                WarnOnce();
                return false;
            }
        }
    }

    /// <summary>The navmesh for this zone is built and usable.</summary>
    public bool IsReady => Try(() =>
        (_isReady ??= _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady")).InvokeFunc());

    /// <summary>Build progress in [0,1], or a negative value when no build is running (also
    /// when vnavmesh is absent).</summary>
    public float BuildProgress
    {
        get
        {
            if (_compatOwnsGates())
                return -1;
            try
            {
                return (_buildProgress ??= _pluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress")).InvokeFunc();
            }
            catch
            {
                WarnOnce();
                return -1;
            }
        }
    }

    /// <summary>Cancels any in-flight build and reloads from cache — the recovery lever when
    /// a seed lands after vnavmesh already started building.</summary>
    public bool Reload() => Try(() =>
        (_reload ??= _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.Reload")).InvokeFunc());

    private bool Try(Func<bool> call)
    {
        if (_compatOwnsGates())
            return false;
        try
        {
            var result = call();
            _warned = false;
            return result;
        }
        catch
        {
            WarnOnce();
            return false;
        }
    }

    private void WarnOnce()
    {
        if (!_warned)
        {
            _warned = true;
            _log?.Invoke("vnavmesh unavailable — seeding still writes its cache, but reload nudges are disabled until it loads.");
        }
    }
}
