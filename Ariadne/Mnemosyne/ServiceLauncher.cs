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

    public static string? ResolveExe(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;
        try
        {
            var marked = File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : null;
            return !string.IsNullOrWhiteSpace(marked) && File.Exists(marked) ? marked : null;
        }
        catch (IOException)
        {
            return null; // marker being rewritten right now; next attempt will read it
        }
    }

    /// <summary>Best-effort start. Returns true when a process was spawned (which is not a
    /// promise it won). Rate-limited so a permanently-broken exe cannot spin.</summary>
    public static bool TryLaunch(string? configuredPath, Action<string> log)
    {
        if (DateTime.UtcNow < _nextAttempt)
            return false;
        _nextAttempt = DateTime.UtcNow + Cooldown;

        var exe = ResolveExe(configuredPath);
        if (exe == null)
        {
            log("[Mnemosyne] service not running and no exe known — run Mnemosyne.Service once, or set its path in the config");
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
