using Ariadne.Config;
using Ariadne.Ipc;
using Ariadne.Movement;
using Ariadne.Zone;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Ariadne.Windows;

/// <summary>
/// The whole bridge on one screen: Mnemosyne connection, current zone and its mesh status,
/// vnavmesh's view of the world, manual overrides for everything the broker does
/// automatically, and the recent-activity trail that explains what just happened.
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly Vector4 Green = new(0.3f, 0.9f, 0.3f, 1);
    private static readonly Vector4 Red = new(0.9f, 0.35f, 0.35f, 1);
    private static readonly Vector4 Yellow = new(0.95f, 0.85f, 0.35f, 1);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1);

    private readonly AriadneConfig _config;
    private readonly Action _saveConfig;
    private readonly MeshBroker _broker;
    private readonly VnavIpc _vnav;
    private readonly ZoneWatcher _zoneWatcher;
    private readonly ReadyTracker _tracker;
    private readonly GameStatePusher _pusher;
    private readonly PathFollower _follower;
    private readonly MoveRequest _move;
    private readonly Func<Vector3?> _playerPosition;

    private Vector3 _pathDest;
    private bool _pathFly;
    private float _pathRange;
    private string _lastPathResult = "";

    public MainWindow(
        AriadneConfig config,
        Action saveConfig,
        MeshBroker broker,
        VnavIpc vnav,
        ZoneWatcher zoneWatcher,
        ReadyTracker tracker,
        GameStatePusher pusher,
        PathFollower follower,
        MoveRequest move,
        Func<Vector3?> playerPosition)
        : base("Ariadne##AriadneMain") // no NoCollapse — the title-bar arrow minimizes it
    {
        _config = config;
        _saveConfig = saveConfig;
        _broker = broker;
        _vnav = vnav;
        _zoneWatcher = zoneWatcher;
        _tracker = tracker;
        _pusher = pusher;
        _follower = follower;
        _move = move;
        _playerPosition = playerPosition;

        Size = new Vector2(560, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(1200, 1000),
        };
    }

    public override void Draw()
    {
        DrawMnemosyne();
        DrawZone();
        DrawVnavmesh();
        DrawActions();
        DrawMovement();
        DrawTimings();
        DrawActivity();
    }

    private void DrawMnemosyne()
    {
        ImGui.TextUnformatted("Mnemosyne");
        ImGui.SameLine();
        if (_broker.MnemosyneConnected)
            ImGui.TextColored(Green, $"connected — {_broker.MnemosyneApp}");
        else
            ImGui.TextColored(Red, "disconnected (start Mnemosyne or the stub; reconnects automatically)");

        ImGui.TextColored(Grey, "game link");
        ImGui.SameLine(90);
        if (_pusher.IsActive && _broker.MnemosyneConnected)
            ImGui.TextColored(Green, "pushing player position (~10 Hz)");
        else
            ImGui.TextColored(Grey, "idle (no player, zone loading, or Mnemosyne away)");
        ImGui.Separator();
    }

    private void DrawZone()
    {
        var snapshot = _broker.Current;
        ImGui.TextUnformatted("Zone");
        ImGui.SameLine();
        var (color, label) = snapshot.Status switch
        {
            ZoneMeshStatus.NotReady => (Grey, "not ready (loading)"),
            ZoneMeshStatus.LocalCurrent => (Green, "cached locally — vnavmesh will skip its build"),
            ZoneMeshStatus.MnemosyneCached => (Yellow, "cached in Mnemosyne — seed to hand it to vnavmesh"),
            ZoneMeshStatus.MnemosyneUnavailable => (Grey, "unknown — Mnemosyne unreachable"),
            ZoneMeshStatus.Missing => (Red, "missing everywhere — vnavmesh will build from scratch"),
            _ => (Grey, snapshot.Status.ToString()),
        };
        ImGui.TextColored(color, label);

        DrawKeyValue("cache key", _zoneWatcher.CurrentCacheKey);
        DrawKeyValue("layout key", _zoneWatcher.CurrentKey);
        if (snapshot.MeshPath is { } path)
            DrawKeyValue("mesh file", path);
        ImGui.Separator();
    }

    private void DrawVnavmesh()
    {
        ImGui.TextUnformatted("vnavmesh");
        ImGui.SameLine();
        if (!_vnav.IsAvailable)
        {
            ImGui.TextColored(Grey, "not loaded");
        }
        else if (_vnav.IsReady)
        {
            ImGui.TextColored(Green, "mesh ready");
        }
        else
        {
            var progress = _vnav.BuildProgress;
            if (progress >= 0)
            {
                ImGui.TextColored(Yellow, "building");
                ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{progress * 100:0}%");
            }
            else
            {
                ImGui.TextColored(Grey, "no mesh loaded");
            }
        }
        ImGui.Separator();
    }

    private void DrawActions()
    {
        var autoSeed = _config.AutoSeed;
        if (ImGui.Checkbox("Auto-seed vnavmesh cache on zone load", ref autoSeed))
        {
            _config.AutoSeed = autoSeed;
            _saveConfig();
        }
        ImGui.SameLine();
        var buildOnMiss = _config.BuildOnMiss;
        if (ImGui.Checkbox("Build missing meshes (capture → Mnemosyne)", ref buildOnMiss))
        {
            _config.BuildOnMiss = buildOnMiss;
            _saveConfig();
        }

        if (ImGui.Button("Refresh"))
            _ = _broker.RefreshAsync();
        ImGui.SameLine();
        if (ImGui.Button("Seed vnavmesh cache"))
            _ = _broker.SeedVnavCacheAsync();
        ImGui.SameLine();
        if (ImGui.Button("Reload vnavmesh"))
            _vnav.Reload();

        ImGui.Separator();
    }

    private void DrawMovement()
    {
        ImGui.TextUnformatted("Movement");
        ImGui.SameLine();
        if (_follower.IsRunning)
            ImGui.TextColored(Green, $"following — {_follower.Waypoints.Count} waypoints left" + (_move.RetriesUsed > 0 ? $" (re-pathed ×{_move.RetriesUsed})" : ""));
        else if (_move.TaskInProgress)
            ImGui.TextColored(Yellow, "pathfinding…");
        else
            ImGui.TextColored(Grey, _move.LastResult.Length > 0 ? $"idle — last: {_move.LastResult}" : "idle");

        ImGui.SetNextItemWidth(240);
        ImGui.InputFloat3("dest", ref _pathDest);
        ImGui.SameLine();
        ImGui.Checkbox("fly", ref _pathFly);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60);
        ImGui.InputFloat("range", ref _pathRange);

        if (ImGui.Button("Move to dest"))
            _move.MoveTo(_pathDest, _pathFly, _pathRange);
        ImGui.SameLine();
        if (ImGui.Button("Stop"))
            _move.Stop();
        ImGui.SameLine();
        if (ImGui.Button("Set dest = here") && _playerPosition() is { } here)
            _pathDest = here;
        ImGui.SameLine();
        if (ImGui.Button("FindPath only"))
        {
            if (_playerPosition() is { } from)
            {
                var dest = _pathDest;
                var fly = _pathFly;
                _ = Task.Run(async () =>
                {
                    var path = await _broker.FindPathAsync(from, dest, fly);
                    _lastPathResult = path.Count > 0 ? $"{path.Count} waypoints" : "no path (see activity)";
                });
            }
        }
        if (_lastPathResult.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Grey, _lastPathResult);
        }

        var align = _config.AlignCameraToMovement;
        if (ImGui.Checkbox("Align camera", ref align)) { _config.AlignCameraToMovement = align; _saveConfig(); }
        ImGui.SameLine();
        var cancel = _config.CancelMoveOnUserInput;
        if (ImGui.Checkbox("Cancel on input", ref cancel)) { _config.CancelMoveOnUserInput = cancel; _saveConfig(); }
        ImGui.SameLine();
        var stalls = _config.DetectStalls;
        if (ImGui.Checkbox("Recover from stalls", ref stalls)) { _config.DetectStalls = stalls; _saveConfig(); }
        if (_config.DetectStalls)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            var retries = _config.StallRetries;
            if (ImGui.InputInt("retries", ref retries)) { _config.StallRetries = Math.Clamp(retries, 0, 20); _saveConfig(); }
        }
        var mirror = _config.MirrorVnavPathIsRunning;
        if (ImGui.Checkbox("Publish vnav.PathIsRunning too (BossMod yields to Ariadne movement)", ref mirror))
        {
            _config.MirrorVnavPathIsRunning = mirror;
            _saveConfig();
        }
        ImGui.Separator();
    }

    private void DrawTimings()
    {
        ImGui.TextUnformatted("Mesh ready times");
        if (_tracker.InProgress is { } live)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, live.MaxProgress > 0.02f
                ? $"building… {live.Elapsed:0.0}s ({live.MaxProgress * 100:0}%)"
                : $"loading… {live.Elapsed:0.0}s");
        }

        var history = _tracker.History;
        if (history.Length == 0)
        {
            ImGui.TextColored(Grey, "no zones measured yet");
            ImGui.Separator();
            return;
        }

        if (ImGui.BeginTable("##timings", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("when", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("zone");
            ImGui.TableSetupColumn("outcome", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("time", ImGuiTableColumnFlags.WidthFixed, 60);
            foreach (var t in history)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Grey, $"{t.When:HH:mm:ss}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(t.CacheKey);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(t.CacheKey);
                ImGui.TableNextColumn();
                if (t.Built)
                    ImGui.TextColored(Red, "built");
                else
                    ImGui.TextColored(Green, "cache load");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatDuration(t.Seconds));
            }
            ImGui.EndTable();
        }
        ImGui.Separator();
    }

    private static string FormatDuration(double seconds) => seconds switch
    {
        < 10 => $"{seconds:0.00}s",
        < 120 => $"{seconds:0.0}s",
        _ => $"{(int)seconds / 60}m{(int)seconds % 60:00}s",
    };

    private void DrawActivity()
    {
        ImGui.TextUnformatted("Activity");
        if (ImGui.BeginChild("##activity", new Vector2(-1, -1), true))
        {
            var entries = _broker.RecentActivity;
            for (var i = entries.Length - 1; i >= 0; i--)
                ImGui.TextUnformatted(entries[i]);
        }
        ImGui.EndChild();
    }

    private static void DrawKeyValue(string label, string value)
    {
        ImGui.TextColored(Grey, label);
        ImGui.SameLine(90);
        ImGui.TextUnformatted(value.Length > 0 ? value : "—");
        if (value.Length > 0 && ImGui.IsItemClicked())
            ImGui.SetClipboardText(value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("click to copy");
    }
}
