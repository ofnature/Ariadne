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
}
