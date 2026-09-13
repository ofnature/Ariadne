using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System;

namespace Ariadne.Travel;

/// <summary>
/// Lifestream, wrapped: the teleport executor. Ariadne never reimplements aetheryte travel
/// (the confirmation dialog, the queue, the loading screen) — it asks Lifestream and waits.
/// Gate names are <c>Lifestream.{Method}</c> from its EzIPC provider, the same three
/// Odysseus has used in the field (<c>Teleport</c>, <c>IsBusy</c>, <c>Abort</c>). Every
/// call fails open: without Lifestream there are simply no teleport legs.
/// </summary>
internal sealed class LifestreamIpc
{
    private readonly IDalamudPluginInterface _pi;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<uint, byte, bool>? _teleport;
    private ICallGateSubscriber<bool>? _isBusy;
    private ICallGateSubscriber<object>? _abort;
    private bool _warned;

    public LifestreamIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pi = pluginInterface;
        _log = log;
    }

    /// <summary>Lifestream is loaded and answering.</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                (_isBusy ??= _pi.GetIpcSubscriber<bool>("Lifestream.IsBusy")).InvokeFunc();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Queue a teleport to an aetheryte by id. True = accepted (not yet arrived).</summary>
    public bool Teleport(uint aetheryteId) => Try(() =>
        (_teleport ??= _pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport")).InvokeFunc(aetheryteId, 0));

    /// <summary>Mid-task: casting, waiting on the confirmation, or in the loading screen.</summary>
    public bool IsBusy => Try(() =>
        (_isBusy ??= _pi.GetIpcSubscriber<bool>("Lifestream.IsBusy")).InvokeFunc());

    public void Abort() => Try(() =>
    {
        (_abort ??= _pi.GetIpcSubscriber<object>("Lifestream.Abort")).InvokeAction();
        return true;
    });

    private bool Try(Func<bool> call)
    {
        try
        {
            var result = call();
            _warned = false;
            return result;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"Lifestream unavailable ({ex.GetType().Name}) — aetheryte legs are off until it loads.");
            }
            return false;
        }
    }
}
