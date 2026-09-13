using Ariadne.Config;
using Ariadne.Ipc;
using Ariadne.Mnemosyne;
using Ariadne.Movement;
using Ariadne.Seeding;
using Ariadne.Travel;
using Ariadne.Windows;
using Ariadne.Zone;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using static Ariadne.Service;

namespace Ariadne;

/// <summary>
/// Ariadne — bridge between the game session and Mnemosyne's out-of-process navmesh cache.
///
/// <para>Zone changes flow: ZoneWatcher → MeshBroker (Mnemosyne query → auto-seed →
/// vnavmesh reload nudge). Consumers use the <c>Ariadne.*</c> IPC surface. Design and
/// phased scope live in <c>PLAN.md</c> at the repo root (local-only).</para>
/// </summary>
public sealed class AriadnePlugin : IDalamudPlugin
{
    private const string CommandMain = "/ariadne";
    private const string CommandShort = "/aria";

    public static string PluginVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly WindowSystem _windowSystem = new("Ariadne");
    private readonly AriadneConfig _config;
    private readonly ZoneWatcher _zoneWatcher;
    private readonly MeshBroker _broker;
    private readonly ReadyTracker _tracker;
    private readonly GameStatePusher _pusher;
    private readonly PathIsRunningSignal _signal;
    private readonly PathFollower _follower;
    private readonly MoveRequest _move;
    private readonly TeleportService _teleports;
    private readonly DtrProvider _dtr;
    private readonly WaypointOverlay _overlay;
    private readonly AriadneIpc _ipc;
    private readonly VnavCompatIpc _vnavCompat;
    private readonly MainWindow _mainWindow;
    private readonly bool[] _navReadyShared;

    public AriadnePlugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();

        _config = PluginInterface.GetPluginConfig() as AriadneConfig ?? new AriadneConfig();

        // vnavmesh's meshcache is a sibling of our own config directory
        var vnavCacheDir = Path.Combine(
            PluginInterface.ConfigDirectory.Parent!.FullName, "vnavmesh", "meshcache");

        var client = new MnemosyneClient(m => Log.Information(m), m => Log.Warning(m),
            serviceExePath: () => _config.AutoStartMnemosyne ? _config.MnemosyneServicePath : null);
        // _vnavCompat is assigned below; the lambda only runs on ticks, after the constructor
        var vnav = new VnavIpc(PluginInterface, () => _vnavCompat?.Owned == true, m => Log.Information(m));
        _broker = new MeshBroker(client, new CacheSeeder(vnavCacheDir), vnav,
            () => _config.AutoSeed, () => _config.BuildOnMiss,
            cacheKey => Framework.RunOnFrameworkThread(() =>
            {
                try { return SceneCapture.CaptureActive(cacheKey); }
                catch (Exception ex) { Log.Warning($"Scene capture failed: {ex.Message}"); return null; }
            }),
            m => Log.Information(m));

        _tracker = new ReadyTracker(
            () => vnav.IsAvailable, () => vnav.IsReady, () => vnav.BuildProgress,
            () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);

        _zoneWatcher = new ZoneWatcher(Framework);

        _pusher = new GameStatePusher(SampleGameState,
            s => client.UpdateGameStateAsync(s.CacheKey, s.TerritoryId, [s.Pos.X, s.Pos.Y, s.Pos.Z], s.Rotation, s.Flying,
                s.Character));

        _signal = new PathIsRunningSignal(PluginInterface, () => _config.MirrorVnavPathIsRunning);
        _follower = new PathFollower(_config, _signal);
        // Teleport legs: Lifestream executes, the aetheryte sheet places, the character's own
        // attunements gate. Blocked wherever a teleport is impossible or unwanted.
        _teleports = new TeleportService(
            new LifestreamIpc(PluginInterface, m => Log.Information(m)),
            AetheryteCatalog.FromSheet(DataManager, m => Log.Warning(m)),
            _config,
            () => ClientState.TerritoryType,
            () => Condition[ConditionFlag.BoundByDuty] || Condition[ConditionFlag.InCombat]
                || Condition[ConditionFlag.RidingPillion] || Condition[ConditionFlag.BetweenAreas],
            () => Condition[ConditionFlag.Casting] || Condition[ConditionFlag.BetweenAreas]
                || Condition[ConditionFlag.BetweenAreas51],
            () =>
            {
                var ids = new List<uint>();
                foreach (var e in AetheryteList)
                    ids.Add(e.AetheryteId);
                return ids;
            });
        _move = new MoveRequest(_broker, _follower, _config, () => ObjectTable.LocalPlayer?.Position,
            id => ObjectTable.SearchById(id) is { } o ? (o.Position, o.HitboxRadius) : null,
            _teleports, m => Log.Information(m));

        _ipc = new AriadneIpc(PluginInterface, _broker, () => _zoneWatcher.CurrentCacheKey, _follower, _move,
            () => _config.SyncGateBudgetMs);

        // Registers nothing until its first Tick, so the window lambdas never run before
        // _mainWindow is assigned.
        _vnavCompat = new VnavCompatIpc(PluginInterface, _config, SaveConfig, _broker, _follower, _move,
            () => _mainWindow!.IsOpen, v => _mainWindow!.IsOpen = v, m => Log.Information(m));

        _overlay = new WaypointOverlay(_config, _follower, () => ObjectTable.LocalPlayer?.Position);
        _mainWindow = new MainWindow(
            _config, SaveConfig, _broker, vnav, _vnavCompat, _zoneWatcher, _tracker, _pusher, _follower, _move, _overlay,
            () => ObjectTable.LocalPlayer?.Position);
        _windowSystem.AddWindow(_mainWindow);

        _dtr = new DtrProvider(_config, _broker, _follower, _move, _zoneWatcher, OpenMain);

        // exception-free readiness for per-frame consumers (Minerva probes once a second and
        // eats a try/catch because gate calls throw when a plugin is absent; shared data doesn't)
        _navReadyShared = PluginInterface.GetOrCreateData<bool[]>("ariadne.NavReady", () => [false]);

        PluginInterface.UiBuilder.Draw += _overlay.Draw;
        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMain;

        CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Ariadne status window. '/ariadne capture' rebuilds this zone from the live layout; '/ariadne aetherytes' lists the zone's crystals as the teleport planner sees them.",
        });
        CommandManager.AddHandler(CommandShort, new CommandInfo(OnCommand)
        {
            HelpMessage = "Short alias for /ariadne.",
        });

        // Framework subscriptions go last, deliberately. Dalamud constructs plugins off the
        // framework thread, so a tick can land in the middle of this constructor - and both
        // handlers read fields assigned further down. Subscribing early threw a
        // NullReferenceException out of IFramework::Update on load, every load, because
        // ZoneWatcher hooks Update in its own constructor and fires KeyChanged on the first
        // tick while _overlay was still null.
        _zoneWatcher.KeyChanged += _ =>
        {
            _broker.OnZoneChanged(_zoneWatcher.CurrentCacheKey);
            _tracker.OnZoneChanged(_zoneWatcher.CurrentCacheKey);
            _overlay.PreviewPath = null; // world coordinates from the old zone are meaningless
        };
        Framework.Update += OnFrameworkTick;

        Log.Information($"Ariadne v{PluginVersion} loaded (vnav cache: {vnavCacheDir}).");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandMain);
        CommandManager.RemoveHandler(CommandShort);
        Framework.Update -= OnFrameworkTick;
        PluginInterface.UiBuilder.Draw -= _overlay.Draw;
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _dtr.Dispose();
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;
        _windowSystem.RemoveAllWindows();
        _vnavCompat.Dispose();
        _ipc.Dispose();
        _move.Dispose();
        _follower.Dispose(); // unhooks movement/camera
        _signal.Dispose();   // clears + relinquishes shared-data flags
        _navReadyShared[0] = false;
        PluginInterface.RelinquishData("ariadne.NavReady");
        _zoneWatcher.Dispose();
        _broker.Dispose(); // disposes the pipe client
    }

    private void OnFrameworkTick(Dalamud.Plugin.Services.IFramework fwk)
    {
        _tracker.Tick();
        _vnavCompat.Tick();
        _pusher.Tick();
        _follower.Update(fwk);
        _move.Update();
        _dtr.Update();
        _navReadyShared[0] = _broker.MnemosyneConnected && _broker.NavIsReady;
    }

    // Runs on the framework thread (safe to touch game state); null while loading or logged out.
    private GameStateSample? SampleGameState()
    {
        var cacheKey = _zoneWatcher.CurrentCacheKey;
        if (cacheKey.Length == 0)
            return null;
        var player = ObjectTable.LocalPlayer;
        if (player == null)
            return null;
        return new GameStateSample(cacheKey, ClientState.TerritoryType, player.Position, player.Rotation,
            Condition[ConditionFlag.InFlight] || Condition[ConditionFlag.Diving],
            $"{player.Name.TextValue}@{player.HomeWorld.Value.Name}");
    }

    private void OnCommand(string command, string args)
    {
        // "/ariadne capture" ships the live layout to Mnemosyne and rebuilds this zone even
        // when a mesh already exists - the only way to capture a festival or shared-group
        // variant, since the automatic path only fires on a cache miss.
        if (args.Trim().Equals("capture", StringComparison.OrdinalIgnoreCase))
        {
            _ = _broker.CaptureCurrentZoneAsync();
            Log.Information("[Ariadne] capture requested for the current zone");
            return;
        }
        // "/ariadne aetherytes": the zone's crystals as the teleport planner sees them
        if (args.Trim().Equals("aetherytes", StringComparison.OrdinalIgnoreCase))
        {
            var here = ObjectTable.LocalPlayer?.Position ?? default;
            foreach (var line in _teleports.Describe(here))
            {
                Log.Information("[Aetherytes] " + line);
                ChatGui.Print("[Ariadne] " + line);
            }
            // the live objects in range, to check the catalog's placement against reality
            foreach (var o in ObjectTable)
            {
                if (o.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte)
                    continue;
                var line = $"  live object '{o.Name.TextValue}' (base id {o.BaseId}) at {o.Position:f1}";
                Log.Information("[Aetherytes] " + line);
                ChatGui.Print("[Ariadne] " + line);
            }
            return;
        }
        OpenMain();
    }

    private void OpenMain() => _mainWindow.IsOpen = true;

    private void SaveConfig() => PluginInterface.SavePluginConfig(_config);
}
