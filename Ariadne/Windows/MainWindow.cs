using Ariadne.Config;
using Ariadne.Ipc;
using Ariadne.Mnemosyne;
using Ariadne.Movement;
using Ariadne.Zone;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;

namespace Ariadne.Windows;

/// <summary>
/// The whole bridge on one screen, two tabs: Status (connection, zone, vnavmesh,
/// movement, timings, activity) and Config (every persisted knob).
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly Vector4 Green = new(0.3f, 0.9f, 0.3f, 1);
    private static readonly Vector4 Red = new(0.9f, 0.35f, 0.35f, 1);
    private static readonly Vector4 Yellow = new(0.95f, 0.85f, 0.35f, 1);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1);

    // Resolve-for-display state. Recomputed at most once a second: the draw path runs every
    // frame and this touches the disk.
    private DateTime _serviceProbedAt = DateTime.MinValue;
    private string? _serviceExe;
    private string _serviceWhyNot = "";
    private bool _servicePipeUp;

    private readonly AriadneConfig _config;
    private readonly Action _saveConfig;
    private readonly MeshBroker _broker;
    private readonly VnavIpc _vnav;
    private readonly VnavCompatIpc _compat;
    private readonly ZoneWatcher _zoneWatcher;
    private readonly ReadyTracker _tracker;
    private readonly GameStatePusher _pusher;
    private readonly PathFollower _follower;
    private readonly MoveRequest _move;
    private readonly WaypointOverlay _overlay;
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
        VnavCompatIpc compat,
        ZoneWatcher zoneWatcher,
        ReadyTracker tracker,
        GameStatePusher pusher,
        PathFollower follower,
        MoveRequest move,
        WaypointOverlay overlay,
        Func<Vector3?> playerPosition)
        : base("Ariadne##AriadneMain") // no NoCollapse — the title-bar arrow minimizes it
    {
        _overlay = overlay;
        _config = config;
        _saveConfig = saveConfig;
        _broker = broker;
        _vnav = vnav;
        _compat = compat;
        _zoneWatcher = zoneWatcher;
        _tracker = tracker;
        _pusher = pusher;
        _follower = follower;
        _move = move;
        _playerPosition = playerPosition;

        Size = new Vector2(560, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(1200, 1000),
        };
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##tabs"))
            return;
        if (ImGui.BeginTabItem("Status"))
        {
            DrawMnemosyne();
            DrawZone();
            DrawVnavmesh();
            DrawActions();
            DrawMovement();
            DrawTimings();
            DrawActivity();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Config"))
        {
            DrawConfig();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    // ---- Status tab ----

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
        if (_compat.Owned)
        {
            // its own status is unreadable now: the vnavmesh.* gates answer with Ariadne's state
            if (_compat.VnavmeshLoaded)
                ImGui.TextColored(Yellow, "loaded — vnavmesh.* gates taken over, consumers move through Ariadne");
            else
                ImGui.TextColored(Green, "not loaded — vnavmesh.* gates served by Ariadne");
        }
        else if (!_vnav.IsAvailable)
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
            ImGui.TextColored(Yellow, _move.TeleportStatus.Length > 0 ? _move.TeleportStatus : "pathfinding…");
        else
            ImGui.TextColored(Grey, _move.LastResult.Length > 0 ? $"idle — last: {_move.LastResult}" : "idle");

        if (Service.TargetManager.Target is { } tgt && _playerPosition() is { } me)
        {
            var dist = Vector3.Distance(me, tgt.Position);
            ImGui.TextColored(Grey, "target");
            ImGui.SameLine(90);
            ImGui.TextUnformatted($"{tgt.Name.TextValue} — {dist:0.0}y");
            ImGui.SameLine();
            if (dist <= 3.5f)
                ImGui.TextColored(Green, "can interact");
            else if (dist <= 7f)
                ImGui.TextColored(Yellow, "interact range (edge)");
            else
                ImGui.TextColored(Grey, "out of reach");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Interact range is ~7y; ≤3.5y is safely inside it (the margins Odysseus\nuses in the field). Some objects differ — gathering nodes are shorter.");
        }

        ImGui.SetNextItemWidth(240);
        ImGui.InputFloat3("dest", ref _pathDest);
        ImGui.SameLine();
        ImGui.Checkbox("fly", ref _pathFly);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60);
        ImGui.InputFloat("range", ref _pathRange);

        if (ImGui.Button("Move to dest"))
            _move.MoveTo(_pathDest, _pathFly, _pathRange);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pathfind on the ground navmesh (or volume when 'fly' is ticked) and walk it.");
        ImGui.SameLine();
        if (ImGui.Button("Fly to dest"))
            _move.MoveTo(_pathDest, fly: true, _pathRange);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Volume pathfind — flying navigation through the 3D voxel volume instead of the\nground mesh. Needs a flyable zone and a mount (or diving); ignores the 'fly' tick.");
        ImGui.SameLine();
        if (ImGui.Button("Stop"))
            _move.Stop();
        ImGui.SameLine();
        if (ImGui.Button("Set dest = here") && _playerPosition() is { } here)
            _pathDest = here;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Your position.");
        ImGui.SameLine();
        if (ImGui.Button("Set dest = target"))
        if (ImGui.Button("Move to target"))
        {
            if (Service.TargetManager.Target is { } t)
                _move.MoveToInteract(t.GameObjectId, _pathFly);
            else
                _lastPathResult = "no target selected";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Interact goal: path to your current target and stop inside interact range\n(config range + its hitbox). Follows it if it wanders.");
        ImGui.SameLine();
        {
            if (Service.TargetManager.Target is { } target)
                _pathDest = target.Position;
            else
                _lastPathResult = "no target selected";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Your current game target's position.");
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
                    _overlay.PreviewPath = path.Count > 0 ? path : null;
                    _lastPathResult = path.Count > 0 ? $"{path.Count} waypoints" : "no path (see activity)";
                });
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Query the path and draw it in the world (blue) without walking it.");
        if (_overlay.PreviewPath is { Count: > 0 })
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear waypoints"))
            {
                _overlay.PreviewPath = null;
                _lastPathResult = "";
            }
        }
        if (_lastPathResult.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Grey, _lastPathResult);
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

    // ---- Config tab ----

    private void DrawConfig()
    {
        ImGui.TextColored(Grey, "Meshes");
        Toggle("Auto-seed vnavmesh cache on zone load", () => _config.AutoSeed, v => _config.AutoSeed = v);
        Toggle("Build missing meshes (capture → Mnemosyne)", () => _config.BuildOnMiss, v => _config.BuildOnMiss = v);
        ImGui.Separator();

        ImGui.TextColored(Grey, "Mnemosyne service");
        Toggle("Start the service when nothing is listening", () => _config.AutoStartMnemosyne, v => _config.AutoStartMnemosyne = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only ever fires when the pipe is absent. A service that is already running is\nleft alone - and if two clients race, the loser exits on its own.");

        ImGui.SetNextItemWidth(430);
        var exePath = _config.MnemosyneServicePath ?? "";
        if (ImGui.InputTextWithHint("Service exe", "blank = %APPDATA%\\Mnemosyne\\service.path", ref exePath, 512))
        {
            _config.MnemosyneServicePath = exePath.Trim();
            _saveConfig();
            _serviceProbedAt = DateTime.MinValue; // re-resolve on the next frame
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Set this when the marker file cannot be read - a second client root, or a\nprofile the game process cannot see. Editing it starts nothing.");
        DrawServiceResolution();
        ImGui.Separator();

        ImGui.TextColored(Grey, "Movement");
        Toggle("Align camera to movement direction", () => _config.AlignCameraToMovement, v => _config.AlignCameraToMovement = v);
        if (_config.AlignCameraToMovement)
        {
            ImGui.SetNextItemWidth(200);
            var height = _config.AlignCameraHeight;
            if (ImGui.SliderFloat("Camera height (degrees)", ref height, -75, 75))
            {
                _config.AlignCameraHeight = height;
                _saveConfig();
            }
        }
        Toggle("Cancel current path on player movement input", () => _config.CancelMoveOnUserInput, v => _config.CancelMoveOnUserInput = v);
        Toggle("Recover from movement stalls", () => _config.DetectStalls, v => _config.DetectStalls = v);
        if (_config.DetectStalls)
        {
            ImGui.SetNextItemWidth(100);
            var retries = _config.StallRetries;
            if (ImGui.InputInt("Futile re-paths before giving up", ref retries))
            {
                _config.StallRetries = Math.Clamp(retries, 0, 20);
                _saveConfig();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Re-paths that gain ≥10y toward the destination don't count — only consecutive futile ones.");
        }
        ImGui.Separator();

        ImGui.TextColored(Grey, "Overlay & info bar");
        Toggle("Show active waypoints", () => _config.ShowWaypoints, v => _config.ShowWaypoints = v);
        Toggle("Enable server info bar entry (DTR)", () => _config.EnableDtrBar, v => _config.EnableDtrBar = v);
        if (_config.EnableDtrBar)
        {
            ImGui.Indent();
            Toggle("Show detailed query status in DTR", () => _config.DtrShowDetail, v => _config.DtrShowDetail = v);
            ImGui.Unindent();
        }
        ImGui.Separator();

        Toggle("Use aetherytes when they save time (Ariadne.* moves only)", () => _config.UseAetherytes, v => _config.UseAetherytes = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Before pathing, compare travelling directly against teleporting to an attuned aetheryte in\nthis zone (Lifestream executes). Never in duties, combat, on a quest vehicle, or for\nvnavmesh.* compat calls.");
        if (_config.UseAetherytes)
        {
            ImGui.Indent();
            ImGui.SetNextItemWidth(120);
            var cost = _config.TeleportCostSeconds;
            if (ImGui.InputFloat("Teleport cost (s)", ref cost, 1, 5, "%.0f"))
            {
                _config.TeleportCostSeconds = Math.Max(0, cost);
                _saveConfig();
            }
            ImGui.SetNextItemWidth(120);
            var saving = _config.TeleportMinSavingSeconds;
            if (ImGui.InputFloat("Minimum saving (s)", ref saving, 1, 5, "%.0f"))
            {
                _config.TeleportMinSavingSeconds = Math.Max(0, saving);
                _saveConfig();
            }
            ImGui.Unindent();
        }
        ImGui.TextColored(Grey, "Integration");
        Toggle("Publish vnav.PathIsRunning too (BossMod yields to Ariadne movement)", () => _config.MirrorVnavPathIsRunning, v => _config.MirrorVnavPathIsRunning = v);
        Toggle("Claim vnavmesh.* IPC gates when vnavmesh is absent", () => _config.EnableVnavCompat, v => _config.EnableVnavCompat = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The cutover switch: with vnavmesh uninstalled, Ariadne registers its IPC names so\nconsumers (Olympus, Theseus, …) work unmodified. Re-checked every couple of seconds:\nif vnavmesh loads later it takes its names back, if it unloads Ariadne reclaims them.");
        if (_config.EnableVnavCompat)
        {
            ImGui.Indent();
            Toggle("Take over the gates even while vnavmesh is loaded (Ariadne does the moving)", () => _config.VnavCompatTakeover, v => _config.VnavCompatTakeover = v);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Ariadne registers over vnavmesh's IPC names, so consumers path and move through Ariadne\nwhile vnavmesh stays installed for its viewer, in-game builds and the seeded cache.\nvnavmesh's own status is unreadable meanwhile. Turning this off (or unloading Ariadne)\nempties the names — reload vnavmesh to give it its own back.");
            ImGui.Unindent();
        }
    }

    /// <summary>Show what autostart would use, and whether it is even needed. Resolving is a
    /// pair of file reads and starts nothing — the only code that launches is TryLaunch, and
    /// it runs from the connect path, only when the pipe is absent.</summary>
    private void DrawServiceResolution()
    {
        if (DateTime.UtcNow - _serviceProbedAt > TimeSpan.FromSeconds(1))
        {
            _serviceProbedAt = DateTime.UtcNow;
            _serviceExe = ServiceLauncher.ResolveExe(_config.MnemosyneServicePath, out _serviceWhyNot);
            _servicePipeUp = File.Exists($@"\\.\pipe\{Protocol.PipeName}");
        }

        if (_servicePipeUp)
            ImGui.TextColored(Green, "running — autostart idle");
        else if (_serviceExe != null)
            ImGui.TextColored(Yellow, "not running — would start it");
        else
            ImGui.TextColored(Red, "not running — cannot start it");

        ImGui.SameLine();
        ImGui.TextColored(Grey, _serviceExe ?? _serviceWhyNot);
    }

    private void Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            _saveConfig();
        }
    }

    private static string FormatDuration(double seconds) => seconds switch
    {
        < 10 => $"{seconds:0.00}s",
        < 120 => $"{seconds:0.0}s",
        _ => $"{(int)seconds / 60}m{(int)seconds % 60:00}s",
    };

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
