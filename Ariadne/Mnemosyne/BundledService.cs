using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Ariadne.Mnemosyne;

// The service that ships inside the plugin package, and the rule for getting it running on a
// machine where Mnemosyne has never been built.
//
// The package carries `service/` beside Ariadne.dll: a self-contained publish of
// Mnemosyne.Service and its CLI, one shared runtime, ~95 MB, put there by
// tools/bundle-mnemosyne.sh. Ariadne resolves it only when nothing else answers - a configured
// path or a marker from a hand-built service both win, so a development machine keeps running
// its own build and never pays for the copy.
//
// Why the payload is copied before it is launched, rather than run from where it sits: a running
// service locks its own DLLs. One launched out of the plugin folder would block Dalamud's next
// plugin update from replacing those files - the exact failure run-service.ps1 exists to avoid
// for development builds. So the payload is a source, not a location: it is staged into
// %APPDATA%\Mnemosyne\service\<version>\ and the staged copy is what runs. The service rewrites
// the marker on start, so the marker ends up naming the staged build, which is the build the
// window compares against.
internal static class BundledService
{
    public const string PayloadFolderName = "service";
    public const string ExeName = "Mnemosyne.Service.exe";
    public const string CliName = "Mnemosyne.Cli.exe";
    public const string VersionFileName = "VERSION";
    public const string StageFolderName = "service";

    /// <summary>Written last, and only when every file made it across: a half-copied stage is
    /// then simply "not staged" and the next attempt copies again over the top.</summary>
    public const string CompleteSentinel = ".complete";

    /// <summary>The folder the payload lives in, or null when this package does not carry one
    /// (a dev build straight out of bin/Release without the bundle step).</summary>
    public static string? PayloadDir(string pluginDir)
    {
        if (string.IsNullOrWhiteSpace(pluginDir))
            return null;
        var dir = Path.Combine(pluginDir, PayloadFolderName);
        return File.Exists(Path.Combine(dir, ExeName)) ? dir : null;
    }

    /// <summary>The payload's version token, from the VERSION file the bundle script writes
    /// (e.g. "0.1.0+38da3f2"). It names the stage directory, so updates land beside the running
    /// service instead of on top of it.</summary>
    public static string PayloadVersion(string payloadDir, string? fallback = null)
    {
        try
        {
            var first = File.ReadLines(Path.Combine(payloadDir, VersionFileName))
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (first is { Length: > 0 })
                return first.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // fall through: no VERSION file, or unreadable
        }
        return string.IsNullOrWhiteSpace(fallback) ? "unknown" : fallback!;
    }

    /// <summary>A version token becomes a directory name, so nothing illegal survives it. A
    /// hand-edited or truncated VERSION file must not produce an unopenable path.</summary>
    public static string SafeDirectoryName(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = version.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == Path.DirectorySeparatorChar)
                chars[i] = '-';
        }
        var name = new string(chars).TrimEnd('.', ' ');
        return name.Length == 0 ? "unknown" : name;
    }

    /// <summary>Where a payload of this version is staged.</summary>
    public static string StageDir(string appDataRoot, string version) => Path.Combine(
        appDataRoot, "Mnemosyne", StageFolderName, SafeDirectoryName(version));

    public static bool IsStaged(string stageDir) =>
        File.Exists(Path.Combine(stageDir, CompleteSentinel)) &&
        File.Exists(Path.Combine(stageDir, ExeName));

    /// <summary>Copy the payload to its stage directory (a no-op when that version is already
    /// there) and return the exe to run. Copies the tree as it finds it, so a payload that grows
    /// a subfolder later still arrives whole.</summary>
    public static string Stage(string payloadDir, string stageDir, DateTime? now = null)
    {
        var exe = Path.Combine(stageDir, ExeName);
        if (IsStaged(stageDir))
            return exe;

        Directory.CreateDirectory(stageDir);
        var queue = new Queue<string>([""]);
        while (queue.Count > 0)
        {
            var rel = queue.Dequeue();
            var src = Path.Combine(payloadDir, rel);
            var dst = Path.Combine(stageDir, rel);
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
            {
                var name = Path.GetFileName(file);
                if (name == CompleteSentinel)
                    continue;
                File.Copy(file, Path.Combine(dst, name), overwrite: true);
            }
            foreach (var sub in Directory.GetDirectories(src))
                queue.Enqueue(Path.Combine(rel, Path.GetFileName(sub)));
        }

        File.WriteAllText(Path.Combine(stageDir, CompleteSentinel),
            $"{PayloadVersion(payloadDir)} staged {(now ?? DateTime.UtcNow):u}{Environment.NewLine}");
        return exe;
    }

    /// <summary>What is in the payload, for the log and the window: the version token, or the
    /// exe's own file version when the VERSION file is missing (a payload assembled by hand).</summary>
    public static string Describe(string payloadDir) => PayloadVersion(payloadDir, FileVersionOf(payloadDir));

    /// <summary>The published exe's file version, or null when it cannot be read.</summary>
    public static string? FileVersionOf(string payloadDir)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(payloadDir, ExeName));
            return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
