using ReloadedDropIn.Core.Manifests;

namespace ReloadedDropIn.Core.Discovery;

/// <summary>A sub-module option within a mod's Options/ directory.</summary>
public sealed record ModOption
{
    /// <summary>Display name derived from the directory name.</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path to the option directory.</summary>
    public required string Directory { get; init; }

    /// <summary>Relative path from the parent mod's directory (e.g. "Options/Censorship").</summary>
    public required string RelativePath { get; init; }
}

/// <summary>A mod directory found under mods/ with a valid manifest.</summary>
public sealed record DiscoveredMod
{
    public required ModManifest Manifest { get; init; }

    /// <summary>Absolute path to the directory containing ModConfig.json.</summary>
    public required string Directory { get; init; }

    public string ModId => Manifest.ModId;

    /// <summary>
    /// Sub-module options found under an Options/ subdirectory.
    /// Empty when the mod has no Options/ folder or the folder is empty.
    /// </summary>
    public IReadOnlyList<ModOption> Options { get; init; } = [];
}
