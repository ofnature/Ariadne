using Ariadne.Config;
using Ariadne.Ipc;
using Ariadne.Mnemosyne;
using Ariadne.Movement;
using Ariadne.Seeding;
using Ariadne.Windows;
using Ariadne.Zone;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
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

    public static string PluginVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly WindowSystem _windowSystem = new("Ariadne");
    private readonly AriadneConfig _config;
    private readonly ZoneWatcher _zoneWatcher;
    private readonly MeshBroker _broker;
    private readonly ReadyTracker _tracker;
    private readonly GameStatePusher _pusher;
    private readonly PathFollower _follower;
    private readonly MoveRequest _move;
    private readonly AriadneIpc _ipc;
    private readonly MainWindow _mainWindow;

    public AriadnePlugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();

        _config = PluginInterface.GetPluginConfig() as AriadneConfig ?? new AriadneConfig();

        // vnavmesh's meshcache is a sibling of our own config directory
        var vnavCacheDir = Path.Combine(
            PluginInterface.ConfigDirectory.Parent!.FullName, "vnavmesh", "meshcache");

        var client = new MnemosyneClient(m => Log.Information(m), m => Log.Warning(m));
        var vnav = new VnavIpc(PluginInterface, m => Log.Information(m));
        _broker = new MeshBroker(client, new CacheSeeder(vnavCacheDir), vnav,
            () => _config.AutoSeed, m => Log.Information(m));

        _tracker = new ReadyTracker(
            () => vnav.IsAvailable, () => vnav.IsReady, () => vnav.BuildProgress,
            () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);

        _zoneWatcher = new ZoneWatcher(Framework);
        _zoneWatcher.KeyChanged += _ =>
        {
            _broker.OnZoneChanged(_zoneWatcher.CurrentCacheKey);
            _tracker.OnZoneChanged(_zoneWatcher.CurrentCacheKey);
        };
        Framework.Update += OnFrameworkTick;

        _pusher = new GameStatePusher(SampleGameState,
            s => client.UpdateGameStateAsync(s.CacheKey, s.TerritoryId, [s.Pos.X, s.Pos.Y, s.Pos.Z], s.Rotation, s.Flying));

        _follower = new PathFollower(_config);
        _move = new MoveRequest(_broker, _follower, _config, () => ObjectTable.LocalPlayer?.Position, m => Log.Information(m));

        _ipc = new AriadneIpc(PluginInterface, _broker, () => _zoneWatcher.CurrentCacheKey, _follower, _move);

        _mainWindow = new MainWindow(
            _config, SaveConfig, _broker, vnav, _zoneWatcher, _tracker, _pusher, _follower, _move,
            () => ObjectTable.LocalPlayer?.Position);
        _windowSystem.AddWindow(_mainWindow);

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMain;

        CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Ariadne status window.",
        });

        Log.Information($"Ariadne v{PluginVersion} loaded (vnav cache: {vnavCacheDir}).");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandMain);
        Framework.Update -= OnFrameworkTick;
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;
        _windowSystem.RemoveAllWindows();
        _ipc.Dispose();
        _move.Dispose();
        _follower.Dispose(); // unhooks movement/camera
        _zoneWatcher.Dispose();
        _broker.Dispose(); // disposes the pipe client
    }

    private void OnFrameworkTick(Dalamud.Plugin.Services.IFramework fwk)
    {
        _tracker.Tick();
        _pusher.Tick();
        _follower.Update(fwk);
        _move.Update();
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
            Condition[ConditionFlag.InFlight] || Condition[ConditionFlag.Diving]);
    }

    private void OnCommand(string command, string args) => OpenMain();

    private void OpenMain() => _mainWindow.IsOpen = true;

    private void SaveConfig() => PluginInterface.SavePluginConfig(_config);
}
