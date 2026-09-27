using System;
using System.IO;
using Ariadne.Mnemosyne;

namespace Ariadne.Tests;

// The service that ships inside the plugin package: which copy actually runs, and where it gets
// copied to before it does. The staging step is the part worth pinning down — it is the only thing
// between a fresh install and a working service, it moves ~95 MB, and it must never try to write
// over a service that is already running (Windows refuses, and the failure looks like "the update
// did nothing").
public class BundledServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ariadne-bundle-" + Guid.NewGuid().ToString("N")[..8]);

    public BundledServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a stray handle on a temp dir is not worth failing a test run over
        }
    }

    private string AppData(string name = "appdata") => Path.Combine(_root, name);

    /// <summary>A plugin folder carrying a payload, as the bundle script leaves it.</summary>
    private string PluginDir(string version = "0.1.0+38da3f2", bool withServiceExe = true, bool withVersion = true)
    {
        var payload = Path.Combine(_root, "plugin", BundledService.PayloadFolderName);
        Directory.CreateDirectory(payload);
        if (withServiceExe)
            File.WriteAllText(Path.Combine(payload, BundledService.ExeName), "service");
        File.WriteAllText(Path.Combine(payload, BundledService.CliName), "cli");
        File.WriteAllText(Path.Combine(payload, "Mnemosyne.Core.dll"), "deps");
        if (withVersion)
            File.WriteAllText(Path.Combine(payload, BundledService.VersionFileName),
                version + Environment.NewLine + "Mnemosyne 0.1.0, commit 38da3f2" + Environment.NewLine);
        return Path.Combine(_root, "plugin");
    }

    private string Marker(string target, string appDataName = "appdata")
    {
        var dir = Path.Combine(AppData(appDataName), "Mnemosyne");
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, "service.path");
        File.WriteAllText(marker, target);
        return marker;
    }

    [Fact]
    public void APayloadCounts_OnlyWhenTheServiceExeIsInIt()
    {
        Assert.Null(BundledService.PayloadDir(Path.Combine(_root, "nothing-here")));
        Assert.Null(BundledService.PayloadDir(PluginDir(withServiceExe: false)));
        Assert.Equal(Path.Combine(PluginDir(), BundledService.PayloadFolderName),
            BundledService.PayloadDir(PluginDir()));
    }

    [Fact]
    public void TheVersionTokenIsTheFirstLine_AndTheRestIsForHumans()
    {
        var payload = Path.Combine(PluginDir(), BundledService.PayloadFolderName);
        Assert.Equal("0.1.0+38da3f2", BundledService.PayloadVersion(payload));
    }

    [Fact]
    public void NoVersionFile_StillDescribesThePayload()
    {
        // a hand-assembled payload (copy-paste out of bin/serve) must not produce an empty or
        // unopenable stage name
        var payload = Path.Combine(PluginDir(withVersion: false), BundledService.PayloadFolderName);
        Assert.Equal("unknown", BundledService.PayloadVersion(payload));
        Assert.False(string.IsNullOrWhiteSpace(BundledService.Describe(payload)));
    }

    [Fact]
    public void AVersionTokenBecomesADirectoryName_WhateverItContains()
    {
        Assert.Equal("0.1.0+38da3f2", BundledService.SafeDirectoryName("0.1.0+38da3f2"));
        Assert.Equal("unknown", BundledService.SafeDirectoryName("   "));
        // the token lands in a path component: nothing that could climb out of it, and nothing
        // Windows refuses to open
        var hostile = BundledService.SafeDirectoryName("0.1.0/../.." + Path.DirectorySeparatorChar + "evil");
        Assert.DoesNotContain(Path.DirectorySeparatorChar, hostile);
        Assert.DoesNotContain('/', hostile);
        Assert.NotEqual("..", hostile);
        Assert.NotEqual(".", hostile);
        foreach (var c in hostile)
            Assert.DoesNotContain(c, Path.GetInvalidFileNameChars());
    }

    [Fact]
    public void TheStageDirectory_IsNamedForTheVersion()
    {
        Assert.Equal(Path.Combine(AppData(), "Mnemosyne", "service", "0.1.0+38da3f2"),
            BundledService.StageDir(AppData(), "0.1.0+38da3f2"));
    }

    [Fact]
    public void StagingCopiesTheWholeTree_AndSaysWhenItIsDone()
    {
        var plugin = PluginDir();
        var payload = Path.Combine(plugin, BundledService.PayloadFolderName);
        Directory.CreateDirectory(Path.Combine(payload, "runtimes"));
        File.WriteAllText(Path.Combine(payload, "runtimes", "extra.dll"), "nested");
        var stage = BundledService.StageDir(AppData(), BundledService.PayloadVersion(payload));

        var exe = BundledService.Stage(payload, stage);

        Assert.Equal(Path.Combine(stage, BundledService.ExeName), exe);
        Assert.True(File.Exists(Path.Combine(stage, BundledService.ExeName)));
        Assert.True(File.Exists(Path.Combine(stage, BundledService.CliName)));
        Assert.True(File.Exists(Path.Combine(stage, "runtimes", "extra.dll")));
        Assert.True(BundledService.IsStaged(stage));
        Assert.Contains("0.1.0+38da3f2", File.ReadAllText(Path.Combine(stage, BundledService.CompleteSentinel)));
    }

    [Fact]
    public void AStagedVersionIsNotCopiedTwice()
    {
        // the point of the sentinel: the second client (or the retry after a failed launch) must
        // not spend another ~95 MB of copying, and must not touch a possibly-running service
        var payload = Path.Combine(PluginDir(), BundledService.PayloadFolderName);
        var stage = BundledService.StageDir(AppData(), BundledService.PayloadVersion(payload));
        BundledService.Stage(payload, stage);

        File.WriteAllText(Path.Combine(payload, "appeared-later.dll"), "new in the payload");
        BundledService.Stage(payload, stage);

        Assert.False(File.Exists(Path.Combine(stage, "appeared-later.dll")));
    }

    [Fact]
    public void AHalfCopiedStage_IsCopiedAgain()
    {
        // a copy that died partway leaves the sentinel unwritten, so the stage is simply not
        // good yet and the next attempt overwrites it
        var payload = Path.Combine(PluginDir(), BundledService.PayloadFolderName);
        var stage = BundledService.StageDir(AppData(), BundledService.PayloadVersion(payload));
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "Mnemosyne.Core.dll"), "truncated");

        BundledService.Stage(payload, stage);

        Assert.True(BundledService.IsStaged(stage));
        Assert.Equal("deps", File.ReadAllText(Path.Combine(stage, "Mnemosyne.Core.dll")));
    }

    [Fact]
    public void ResolutionPrefersConfigured_ThenTheMarker_ThenTheBundle()
    {
        var plugin = PluginDir();
        var configured = Path.Combine(_root, "hand-built", "Mnemosyne.Service.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(configured)!);
        File.WriteAllText(configured, "dev build");

        // 1. an explicit path always wins, even with a payload sitting there
        var chosen = ServiceLauncher.Resolve(configured, AppData(), plugin);
        Assert.Equal(configured, chosen.Exe);
        Assert.False(chosen.NeedsStaging);

        // 2. the marker wins over the bundle: a development machine keeps its own build
        var marked = Path.Combine(_root, "marked", "Mnemosyne.Service.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(marked)!);
        File.WriteAllText(marked, "marked build");
        Marker(marked);

        var viaMarker = ServiceLauncher.Resolve(null, AppData(), plugin);
        Assert.Equal(marked, viaMarker.Exe);
        Assert.Contains("marker", viaMarker.Reason);
        Assert.False(viaMarker.NeedsStaging);

        // 3. nothing known: the payload, staged rather than run in place
        var fresh = AppData("fresh-appdata");
        var viaBundle = ServiceLauncher.Resolve(null, fresh, plugin);
        Assert.Equal(Path.Combine(fresh, "Mnemosyne", "service", "0.1.0+38da3f2", BundledService.ExeName),
            viaBundle.Exe);
        Assert.True(viaBundle.NeedsStaging);
        Assert.Contains("0.1.0+38da3f2", viaBundle.Reason);
    }

    [Fact]
    public void AStagedBundle_NoLongerNeedsStaging()
    {
        var plugin = PluginDir();
        var appData = AppData("fresh-appdata");
        var payload = Path.Combine(plugin, BundledService.PayloadFolderName);
        BundledService.Stage(payload, BundledService.StageDir(appData, BundledService.PayloadVersion(payload)));

        var resolved = ServiceLauncher.Resolve(null, appData, plugin);

        Assert.False(resolved.NeedsStaging);
        Assert.Contains("already staged", resolved.Reason);
    }

    [Fact]
    public void TheBundleIsNeverTreatedAsTheMarker()
    {
        // the window compares the marker against the build that is answering. A bundled service
        // is by definition not the build a half-finished restart left behind, so it must not
        // appear in that answer.
        var plugin = PluginDir();
        var bare = AppData("no-mnemosyne-yet"); // no marker file anywhere under here

        Assert.Null(ServiceLauncher.ResolveMarkerExe(out var reason, bare));
        Assert.Contains("marker", reason);
        Assert.NotNull(ServiceLauncher.Resolve(null, bare, plugin).Exe);
    }

    [Fact]
    public void WithNothingAnywhere_TheReasonSaysWhatToDo()
    {
        var bare = Path.Combine(_root, "bare-plugin");
        Directory.CreateDirectory(bare);

        var resolved = ServiceLauncher.Resolve(null, AppData("fresh-appdata"), bare);

        Assert.Null(resolved.Exe);
        Assert.Contains("carries no service", resolved.Reason);
        Assert.Contains("reinstall the plugin", resolved.Reason);
        Assert.Contains("hand-built service", resolved.Reason); // why the marker is missing
    }
}
