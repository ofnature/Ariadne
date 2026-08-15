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

    // ---- movement ----

    /// <summary>Turn the camera to face the direction of travel while following a path.</summary>
    public bool AlignCameraToMovement { get; set; }
    public float AlignCameraHeight { get; set; } = -15;

    /// <summary>Player touching the movement keys cancels the current path.</summary>
    public bool CancelMoveOnUserInput { get; set; }

    /// <summary>Detect no-progress while following and re-path (recovery vnavmesh lacks).</summary>
    public bool DetectStalls { get; set; } = true;
    /// <summary>Stalled = moved less than this many yalms…</summary>
    public float StallMinProgress { get; set; } = 0.5f;
    /// <summary>…over this many milliseconds.</summary>
    public int StallWindowMs { get; set; } = 1500;
    /// <summary>Re-path attempts before giving up on a stalled move.</summary>
    public int StallRetries { get; set; } = 3;

    /// <summary>Also publish the shared-data flag under vnavmesh's name
    /// (<c>vnav.PathIsRunning</c>) so plugins that yield movement to vnavmesh — BossMod's
    /// "someone else is driving" check — yield to Ariadne unmodified. Ariadne's own
    /// <c>ariadne.PathIsRunning</c> is always published.</summary>
    public bool MirrorVnavPathIsRunning { get; set; } = true;
}
