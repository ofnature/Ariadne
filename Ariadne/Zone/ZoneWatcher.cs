using Dalamud.Plugin.Services;
using System;

namespace Ariadne.Zone;

// Polls the layout state every framework tick, exactly like vnavmesh's NavmeshManager.Update,
// so Ariadne notices a zone becoming ready on the same frame vnavmesh would start its build.
public sealed class ZoneWatcher : IDisposable
{
    public string CurrentKey { get; private set; } = "";
    public string CurrentCacheKey { get; private set; } = "";

    /// <summary>Fires on the framework thread when the layout key changes. Empty key = zone unloading/not ready.</summary>
    public event Action<string>? KeyChanged;

    private readonly IFramework _framework;
    private bool _dead; // set after an unexpected poll failure so one bad sig scan can't spam every tick

    public ZoneWatcher(IFramework framework)
    {
        _framework = framework;
        _framework.Update += OnUpdate;
    }

    public void Dispose() => _framework.Update -= OnUpdate;

    private void OnUpdate(IFramework framework)
    {
        if (_dead)
            return;

        string key;
        try
        {
            key = ZoneKey.CurrentKey();
        }
        catch (Exception ex)
        {
            _dead = true;
            Service.Log.Error($"[ZoneWatcher] Layout poll failed, watcher disabled: {ex}");
            return;
        }

        if (key == CurrentKey)
            return;

        Service.Log.Info($"[ZoneWatcher] Transition from '{CurrentKey}' to '{key}'");
        CurrentKey = key;
        CurrentCacheKey = key.Length > 0 ? ZoneKey.CurrentCacheKey() : "";
        if (CurrentCacheKey.Length > 0)
            Service.Log.Info($"[ZoneWatcher] Cache key: '{CurrentCacheKey}' (expect meshcache file '{CurrentCacheKey}.navmesh')");

        KeyChanged?.Invoke(key);
    }
}
