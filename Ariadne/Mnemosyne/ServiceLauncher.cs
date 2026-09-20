using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Ariadne.Mnemosyne;

// Phase 0 of the vnavmesh replacement: the mesh service has to be there without the user
// remembering to start it. When the pipe is absent we start Mnemosyne.Service ourselves.
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

    /// <summary>Path Mnemosyne stamps for us on every service/CLI run, so the plugin does
    /// not have to hardcode a build directory.</summary>
    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "service.path");

    public static string? ResolveExe(string? configured) => ResolveExe(configured, out _);

    /// <summary>Resolve the service exe, and say what failed when it cannot. The reason
    /// matters: "no exe known" covered a configured path that does not exist, a missing
    /// marker file, and a marker pointing at a deleted build, and those need three
    /// different responses from whoever reads the log. One of them cost an evening.</summary>
    public static string? ResolveExe(string? configured, out string reason)
    {
        var hasConfigured = !string.IsNullOrWhiteSpace(configured);
        if (hasConfigured && File.Exists(configured))
        {
            reason = "";
            return configured;
        }

        // Read rather than probe with File.Exists. Both game clients reported "no marker" for
        // a file that demonstrably existed on disk with the right owner and ACL, and a bare
        // Exists check cannot tell "absent" from "present but this process may not open it" -
        // it answers false for both. The exception type does distinguish them.
        var configuredNote = hasConfigured ? $"configured path '{configured}' does not exist; " : "";
        string marked;
        try
        {
            marked = File.ReadAllText(MarkerPath).Trim();
        }
        catch (FileNotFoundException)
        {
            reason = $"{configuredNote}no marker at {MarkerPath} - run Mnemosyne.Service once, or set the path in the config";
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            reason = $"{configuredNote}no {Path.GetDirectoryName(MarkerPath)} directory - Mnemosyne has never run as this user";
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            reason = $"{configuredNote}marker {MarkerPath} exists but cannot be opened (access denied): {ex.Message}";
            return null;
        }
        catch (IOException ex)
        {
            reason = $"{configuredNote}cannot read marker {MarkerPath}: {ex.Message}"; // mid-rewrite; the next attempt gets it
            return null;
        }

        if (string.IsNullOrWhiteSpace(marked))
        {
            reason = $"marker {MarkerPath} is empty";
            return null;
        }
        if (!File.Exists(marked))
        {
            reason = $"marker points at '{marked}', which does not exist (rebuilt or moved?)";
            return null;
        }

        reason = "";
        return marked;
    }

    /// <summary>Best-effort start. Returns true when a process was spawned (which is not a
    /// promise it won). Rate-limited so a permanently-broken exe cannot spin.</summary>
    public static bool TryLaunch(string? configuredPath, Action<string> log)
    {
        if (DateTime.UtcNow < _nextAttempt)
            return false;
        _nextAttempt = DateTime.UtcNow + Cooldown;

        var exe = ResolveExe(configuredPath, out var whyNot);
        if (exe == null)
        {
            log($"[Mnemosyne] service not running and cannot be started: {whyNot}");
            return false;
        }

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
