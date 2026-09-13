using Dalamud.Configuration;
using System;

namespace Ariadne.Config;

[Serializable]
public sealed class AriadneConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Seed vnavmesh's meshcache automatically when a zone loads and Mnemosyne has
    /// a current mesh the local cache lacks.</summary>
    public bool AutoSeed { get; set; } = true;

    /// <summary>When nobody has a mesh for the zone, capture the live scene and ask
    /// Mnemosyne to build it out of process (the vnavmesh-replacement path: no in-game
    /// build cost, exact live variant).</summary>
    public bool BuildOnMiss { get; set; } = true;

    // ---- movement ----

    /// <summary>Turn the camera to face the direction of travel while following a path.</summary>
    public bool AlignCameraToMovement { get; set; }
    public float AlignCameraHeight { get; set; } = -15;

    /// <summary>Player touching the movement keys cancels the current path.</summary>
    public bool CancelMoveOnUserInput { get; set; }

    /// <summary>Detect no-progress while following and re-path (recovery vnavmesh lacks).</summary>
    public bool DetectStalls { get; set; } = true;
    /// <summary>Hard stall: displaced less than this many yalms…</summary>
    public float StallMinProgress { get; set; } = 0.5f;
    /// <summary>…over this many milliseconds.</summary>
    public int StallWindowMs { get; set; } = 1500;
    /// <summary>Soft stall (wobbling without gaining): closed less than this many yalms
    /// toward the destination…</summary>
    public float ProgressMinGain { get; set; } = 10f;
    /// <summary>…over this many milliseconds. Gaining ground buys the clock back.</summary>
    public int ProgressWindowMs { get; set; } = 15000;
    /// <summary>Futile recovery attempts (re-paths that gained less than ProgressMinGain)
    /// before giving up. Recoveries that gain ground don't count against this.</summary>
    public int StallRetries { get; set; } = 3;

    // ---- vnavmesh compat ----

    /// <summary>Claim the vnavmesh.* IPC gates when the real vnavmesh isn't loaded, so
    /// existing consumers (Olympus, Theseus, …) run on Ariadne unmodified. Applies at
    /// plugin load.</summary>
    public bool EnableVnavCompat { get; set; } = true;

    /// <summary>Claim the vnavmesh.* gates even while the real vnavmesh is loaded — Ariadne
    /// registers over its names, so consumers path and move through Ariadne while vnavmesh
    /// stays installed for its viewer, in-game builds, and the seeded cache. Requires
    /// EnableVnavCompat. Re-applied every couple of seconds; vnavmesh's own IPC status is
    /// unreadable while this is on (the gates answer with Ariadne's state).</summary>
    public bool VnavCompatTakeover { get; set; }

    /// <summary>Budget for sync-shaped compat gates (Query.Mesh.*): how long a blocking
    /// caller waits on the pipe before getting the not-found fallback. Warm queries answer
    /// in 1–3 ms; this only bites when Mnemosyne is cold or away.</summary>
    public int SyncGateBudgetMs { get; set; } = 100;

    // ---- UI / overlay ----

    /// <summary>Show the Ariadne entry in the server info bar (DTR).</summary>
    public bool EnableDtrBar { get; set; } = true;
    /// <summary>Append live query/movement detail to the DTR entry.</summary>
    public bool DtrShowDetail { get; set; } = true;
    /// <summary>Draw the active path's waypoints in the world while following.</summary>
    public bool ShowWaypoints { get; set; } = true;

    /// <summary>Also publish the shared-data flag under vnavmesh's name
    /// (<c>vnav.PathIsRunning</c>) so plugins that yield movement to vnavmesh — BossMod's
    /// "someone else is driving" check — yield to Ariadne unmodified. Ariadne's own
    /// <c>ariadne.PathIsRunning</c> is always published.</summary>
    public bool MirrorVnavPathIsRunning { get; set; } = true;

    /// <summary>Start Mnemosyne.Service when the pipe is absent instead of waiting for the
    /// user to. Safe with several game clients running: the service is single-instance.</summary>
    public bool AutoStartMnemosyne { get; set; } = true;

    /// <summary>Explicit path to Mnemosyne.Service.exe. Empty = read the path Mnemosyne
    /// stamps at <c>%APPDATA%\Mnemosyne\service.path</c> on every run.</summary>
    public string MnemosyneServicePath { get; set; } = "";
}
