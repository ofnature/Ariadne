using Ariadne.Config;
using Ariadne.Movement;
using Ariadne.Zone;
using Dalamud.Game.Gui.Dtr;
using System;

namespace Ariadne;

// Server-info-bar (DTR) entry, vnavmesh's DTRProvider pattern: one glanceable line,
// optional live detail, click opens the window.
internal sealed class DtrProvider : IDisposable
{
    private readonly AriadneConfig _config;
    private readonly MeshBroker _broker;
    private readonly PathFollower _follower;
    private readonly MoveRequest _move;
    private readonly ZoneWatcher _zoneWatcher;
    private readonly IDtrBarEntry _entry;

    public DtrProvider(AriadneConfig config, MeshBroker broker, PathFollower follower,
        MoveRequest move, ZoneWatcher zoneWatcher, Action openWindow)
    {
        _config = config;
        _broker = broker;
        _follower = follower;
        _move = move;
        _zoneWatcher = zoneWatcher;
        _entry = Service.DtrBar.Get("Ariadne");
        _entry.OnClick = _ => openWindow();
    }

    public void Dispose() => _entry.Remove();

    public void Update()
    {
        _entry.Shown = _config.EnableDtrBar;
        if (!_entry.Shown)
            return;

        var mesh = !_broker.MnemosyneConnected && _broker.Current.Status is ZoneMeshStatus.MnemosyneUnavailable or ZoneMeshStatus.NotReady
            ? "Offline"
            : _broker.Current.Status switch
            {
                ZoneMeshStatus.NotReady => "…",
                ZoneMeshStatus.LocalCurrent or ZoneMeshStatus.MnemosyneCached => "Ready",
                ZoneMeshStatus.MnemosyneUnavailable => "Offline",
                ZoneMeshStatus.Missing => "No mesh",
                _ => "?",
            };

        var text = $"Ariadne: {mesh}";
        if (_config.DtrShowDetail)
        {
            if (_move.TaskInProgress)
                text += " | Pathfinding";
            if (_follower.IsRunning)
                text += $" | Moving ({_follower.Waypoints.Count}wp)";
        }

        _entry.Text = text;
        _entry.Tooltip = _zoneWatcher.CurrentCacheKey.Length > 0 ? _zoneWatcher.CurrentCacheKey : "zone loading";
    }
}
