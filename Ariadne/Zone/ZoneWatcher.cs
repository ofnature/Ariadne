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

    /// <summary>Consecutive failed polls. Non-zero means zone detection is not reading the
    /// layout right now, and the keys below are the last read that worked — not the current
    /// zone. The window says so rather than showing a stale pair as if it were live.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>The last failure in the form the window shows it ("NullReferenceException: …").</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Zone detection is currently failing to read the layout (it is retrying).</summary>
    public bool Interrupted => ConsecutiveFailures > 0;

    private readonly IFramework _framework;

    // Poll failures are retried, never fatal. Disabling the watcher after the first one meant a
    // single transient throw during a layout transition cost every later zone for the session,
    // with one log line as the only trace. Backing off instead keeps a blip free (the first two
    // failures retry on the next tick) and a permanently broken read quiet.
    private const int FailuresBeforeBackoff = 3;
    private const int FailuresBeforeLongBackoff = 10;
    private static readonly TimeSpan ShortBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongBackoff = TimeSpan.FromMinutes(2);

    private DateTime _retryAt = DateTime.MinValue;

    public ZoneWatcher(IFramework framework)
    {
        _framework = framework;
        _framework.Update += OnUpdate;
    }

    public void Dispose() => _framework.Update -= OnUpdate;

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < _retryAt)
            return;

        string key;
        try
        {
            key = ZoneKey.CurrentKey();
        }
        catch (Exception ex)
        {
            ++ConsecutiveFailures;
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            if (ConsecutiveFailures == 1)
                Service.Log.Error($"[ZoneWatcher] Layout poll failed ({LastError}) — retrying, zone detection stays live: {ex}");
            else if (ConsecutiveFailures % 100 == 0)
                Service.Log.Warning($"[ZoneWatcher] Layout poll still failing ({ConsecutiveFailures} in a row): {LastError}");
            if (ConsecutiveFailures >= FailuresBeforeBackoff)
                _retryAt = DateTime.UtcNow + (ConsecutiveFailures >= FailuresBeforeLongBackoff ? LongBackoff : ShortBackoff);
            return;
        }

        if (ConsecutiveFailures > 0)
        {
            Service.Log.Information($"[ZoneWatcher] Layout poll recovered after {ConsecutiveFailures} failure(s)");
            ConsecutiveFailures = 0;
            LastError = "";
            _retryAt = DateTime.MinValue;
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
