using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ariadne.Mnemosyne;

// Phase 0 of the vnavmesh replacement: the mesh service has to be there without the user
// remembering to start it. When the pipe is absent we start Mnemosyne.Service ourselves.
//
// Three places a service can come from, in the order that keeps every machine honest:
//
//   1. the configured path  - an explicit choice; always wins
//   2. the marker           - a service that has run here before stamped its own path into
//                             %APPDATA%\Mnemosyne\service.path; a development machine keeps
//                             running its own build and never pays for the bundled copy.
//                             A marker that names an earlier *staged* bundle does not count:
//                             the bundle carried now supersedes it (2026-09-29)
//   3. the bundled payload  - `service/` inside the plugin package, staged under %APPDATA% and
//                             launched from there (see BundledService), so a machine that has
//                             never built Mnemosyne still gets a working service
//
// Four game clients on one PC means four plugin instances racing to do this. That race is
// resolved by the service itself, which holds a Global\MnemosyneService mutex and exits
// immediately if it loses — so a duplicate launch is harmless, never two servers on one
// pipe. The Global\MnemosyneServiceLaunch mutex here is only politeness: it stops four
// processes spawning at once when one would do.
internal static class ServiceLauncher
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
    private static DateTime _nextAttempt = DateTime.MinValue;
    private static int _staging; // guards the one-time payload copy across clients and retries

    internal static string AppDataRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Path Mnemosyne stamps for us on every service/CLI run, so the plugin does
    /// not have to hardcode a build directory.</summary>
    internal static string MarkerPath => Path.Combine(AppDataRoot, "Mnemosyne", "service.path");

    /// <summary>The plugin's own folder — where a package's `service/` payload sits.</summary>
    internal static string PluginDir =>
        Path.GetDirectoryName(typeof(ServiceLauncher).Assembly.Location) ?? "";

    /// <summary>What autostart would use, why, and whether it has to be staged first.</summary>
    internal readonly record struct Resolution(string? Exe, string Reason, bool NeedsStaging,
        ServiceSource Source = ServiceSource.None);

    /// <summary>The service that answered is not the one that should be running, and nothing
    /// chose it on purpose - so it should make way. Null when it may stay.
    ///
    /// <para>Found 2026-09-29 on a machine with two clients: every exploration query failed with
    /// "unknown op", because a service from before 2026-09-14 was answering the pipe. Three plugin
    /// updates had shipped a newer one and none of them replaced it: the launcher starts a
    /// service only when the pipe is absent, and the old one never let go of it.</para></summary>
    /// <param name="target">What autostart would run now.</param>
    /// <param name="runningExe">The answering service's exe from `hello`; null when it is too
    /// old to say (before 2026-09-20).</param>
    /// <param name="bundleCarried">This package ships a service at all.</param>
    public static string? WhyReplace(Resolution target, string? runningExe, bool bundleCarried)
    {
        if (!bundleCarried || target.Exe == null || target.Source == ServiceSource.Configured)
            return null; // nothing to offer instead, or an explicit choice: theirs to keep
        if (runningExe == null)
            return "it does not say which build it is, so it predates 2026-09-20, and this package ships a newer one";
        if (target.Source != ServiceSource.Bundled)
            return null; // a hand-built service the marker names: a development machine's own
        if (string.Equals(runningExe.TrimEnd('\\'), target.Exe.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return null;
        return $"it is {runningExe}, not the service this package ships ({target.Exe})";
    }

    private static DateTime _nextReplace = DateTime.MinValue;
    private static readonly TimeSpan ReplaceCooldown = TimeSpan.FromMinutes(10);

    /// <summary>Stop the running service and forget its marker, so the next launch resolves to
    /// the bundle. Once per ten minutes: a service that cannot be killed (another user's, or
    /// one that respawns from elsewhere) must not be fought every reconnect. Returns whether it
    /// acted.</summary>
    public static bool Replace(string why, Action<string> log)
    {
        if (DateTime.UtcNow < _nextReplace)
            return false;
        _nextReplace = DateTime.UtcNow + ReplaceCooldown;

        var killed = 0;
        foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(BundledService.ExeName)))
        {
            using (proc)
            {
                try
                {
                    proc.Kill();
                    killed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    log($"[Mnemosyne] could not stop the service (pid {proc.Id}): {ex.Message}");
                }
            }
        }
        try
        {
            File.Delete(MarkerPath); // it names the build that just left; the bundle stamps its own
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"[Mnemosyne] could not remove the marker at {MarkerPath}: {ex.Message}");
        }
        _nextAttempt = DateTime.MinValue; // the pipe is about to be absent: launch on the next connect
        log($"[Mnemosyne] replacing the running service - {why}; stopped {killed} process(es), the bundled one starts next");
        return true;
    }

    public static string? ResolveExe(string? configured) => ResolveExe(configured, out _);

    /// <summary>Resolve the service exe, and say what failed when it cannot. The reason
    /// matters: "no exe known" covered a configured path that does not exist, a missing
    /// marker file, and a marker pointing at a deleted build, and those need three
    /// different responses from whoever reads the log. One of them cost an evening.</summary>
    public static string? ResolveExe(string? configured, out string reason)
    {
        var resolved = Resolve(configured);
        reason = resolved.Reason;
        return resolved.Exe;
    }

    /// <summary>The marker's answer alone — what the client compares the answering build
    /// against. Deliberately excludes the bundled payload: that comparison is about a
    /// half-finished restart of a hand-built service, and the bundled one is not that.</summary>
    public static string? ResolveMarkerExe(out string reason, string? appDataRoot = null) =>
        ReadMarker(out reason, appDataRoot ?? AppDataRoot);

    /// <summary>configured → marker → bundled. Pure but for the disk reads, so the tests can
    /// point it at temp directories.</summary>
    internal static Resolution Resolve(string? configured, string? appDataRoot = null, string? pluginDir = null)
    {
        var hasConfigured = !string.IsNullOrWhiteSpace(configured);
        if (hasConfigured && File.Exists(configured))
            return new Resolution(configured, "configured path", false, ServiceSource.Configured);

        var roots = appDataRoot ?? AppDataRoot;
        var marked = ReadMarker(out var markerWhyNot, roots);
        var payload = BundledService.PayloadDir(pluginDir ?? PluginDir);

        // A marker inside the staging folder was written by a bundle this plugin staged before.
        // It does not outrank the bundle the plugin carries now: that is how three updates
        // shipped a newer service and the old one kept answering (2026-09-29).
        var superseded = marked != null && payload != null && IsUnderStageRoot(marked, roots);
        if (marked != null && !superseded)
            return new Resolution(marked, "the marker", false, ServiceSource.Marker);

        if (payload != null)
        {
            var version = BundledService.Describe(payload);
            var stage = BundledService.StageDir(roots, version);
            var staged = BundledService.IsStaged(stage);
            var exe = Path.Combine(stage, BundledService.ExeName);
            var note = superseded && !string.Equals(marked, exe, StringComparison.OrdinalIgnoreCase)
                ? $"; the marker names an earlier staged bundle, {marked}, which this one supersedes"
                : "";
            return new Resolution(
                exe,
                $"the bundled service {version} at {payload}"
                    + (staged ? " (already staged)" : " (not staged yet)") + note,
                NeedsStaging: !staged, ServiceSource.Bundled);
        }

        var configuredNote = hasConfigured ? $"configured path '{configured}' does not exist; " : "";
        return new Resolution(null,
            $"{configuredNote}{markerWhyNot}; and this package carries no service at "
            + $"'{Path.Combine(pluginDir ?? PluginDir, BundledService.PayloadFolderName)}' "
            + "(reinstall the plugin, or set the path in the config)",
            false);
    }

    private static bool IsUnderStageRoot(string exe, string appDataRoot)
    {
        var root = Path.Combine(appDataRoot, "Mnemosyne", BundledService.StageFolderName)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return exe.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The existing marker logic, on its own so the two callers can differ: autostart
    /// follows it anywhere, the window's build comparison wants only what it says.</summary>
    internal static string? ReadMarker(out string reason, string? appDataRoot = null)
    {
        string marked;
        try
        {
            marked = File.ReadAllText(Path.Combine(appDataRoot ?? AppDataRoot, "Mnemosyne", "service.path")).Trim();
        }
        catch (FileNotFoundException)
        {
            reason = "no service.path marker - no hand-built service has ever run for this user";
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            reason = "no %APPDATA%\\Mnemosyne directory and no service.path marker to read - "
                + "no hand-built service has ever run for this user";
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            reason = $"marker exists but cannot be opened (access denied): {ex.Message}";
            return null;
        }
        catch (IOException ex)
        {
            reason = $"cannot read marker: {ex.Message}"; // mid-rewrite; the next attempt gets it
            return null;
        }

        if (string.IsNullOrWhiteSpace(marked))
        {
            reason = "the service.path marker is empty";
            return null;
        }
        if (!File.Exists(marked))
        {
            reason = $"the marker points at '{marked}', which does not exist (rebuilt or moved?)";
            return null;
        }

        reason = "";
        return marked;
    }

    /// <summary>Best-effort start. Returns true when a process was spawned, or when a
    /// stage-and-launch is under way — neither is a promise the service won, since losing the
    /// mutex to another client is the happy path. Rate-limited so a permanently-broken exe
    /// cannot spin.</summary>
    public static bool TryLaunch(string? configuredPath, Action<string> log)
    {
        if (DateTime.UtcNow < _nextAttempt)
            return false;
        _nextAttempt = DateTime.UtcNow + Cooldown;

        var target = Resolve(configuredPath);
        if (target.Exe == null)
        {
            log($"[Mnemosyne] service not running and cannot be started: {target.Reason}");
            return false;
        }

        if (!target.NeedsStaging)
            return LaunchOnce(target.Exe, log);

        // The payload has to be copied out before it runs (~95 MB), and this is called from
        // whatever thread noticed the pipe missing — the framework thread. So the copy happens
        // off it, and the launch happens when the copy is done rather than on the next tick.
        if (Interlocked.CompareExchange(ref _staging, 1, 0) != 0)
            return false; // another client is already staging it; their copy will launch

        var payload = BundledService.PayloadDir(PluginDir)!;
        var stageDir = Path.GetDirectoryName(target.Exe)!;
        log($"[Mnemosyne] no service yet — {target.Reason}; copying it out of the plugin folder, "
            + "which a running service would otherwise lock against the next plugin update");
        Task.Run(() =>
        {
            try
            {
                var staged = BundledService.Stage(payload, stageDir);
                _nextAttempt = DateTime.MinValue; // staged: don't wait out the cooldown to start it
                log($"[Mnemosyne] staged the bundled service to {staged}");
                LaunchOnce(staged, log);
            }
            catch (Exception ex)
            {
                log($"[Mnemosyne] could not stage the bundled service: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _staging, 0);
            }
        });
        return true;
    }

    // A zone build runs on every core but two, for tens of seconds to minutes, and on a fresh
    // machine every zone is a new one. At normal priority that competes with the game for the
    // CPU exactly when a character has just arrived and started moving. Below normal, Windows
    // gives the game what it asks for and the build takes what is left: builds finish a little
    // later, the game does not stutter for them. Path queries are milliseconds either way.
    private static void YieldToTheGame(Process service, Action<string> log)
    {
        try
        {
            service.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            // already exited (lost the single-instance race), or access denied: not worth more than a line
            log($"[Mnemosyne] could not lower the service's priority: {ex.GetType().Name}");
        }
    }

    private static bool LaunchOnce(string exe, Action<string> log)
    {
        using var gate = new Mutex(false, @"Global\MnemosyneServiceLaunch");
        var held = false;
        try
        {
            held = gate.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (AbandonedMutexException)
        {
            held = true; // previous holder died mid-launch; we inherit the right to try
        }
        if (!held)
            return false; // another client is launching it — let their attempt land

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true, // service tees its console output to %APPDATA%\Mnemosyne\service.log
            });
            log($"[Mnemosyne] started service: {exe} (pid {proc?.Id.ToString() ?? "?"})");
            if (proc != null)
                YieldToTheGame(proc, log);
            return proc != null;
        }
        catch (Exception ex)
        {
            log($"[Mnemosyne] failed to start service '{exe}': {ex.Message}");
            return false;
        }
        finally
        {
            gate.ReleaseMutex();
        }
    }
}

/// <summary>Where autostart's answer came from. Decides whether a running service that is not
/// that answer may stay: an explicit path or a hand-built marker is a choice, the bundle is not.</summary>
internal enum ServiceSource { None, Configured, Marker, Bundled }
