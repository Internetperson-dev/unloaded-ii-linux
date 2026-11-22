using System.Text.Json;
using ReloadedDropIn.Core.Filesystem;

namespace ReloadedDropIn.Core.Configuration;

/// <summary>
/// User choices made in the in-game overlay, applied by sync on the next launch.
/// Lives at reloaded-dropin/overlay/overrides.json — written by the overlay mod,
/// read here. Deleting the file resets everything to "enabled".
/// </summary>
public sealed record OverlayOverrides
{
    public string[] DisabledMods { get; init; } = [];

    /// <summary>
    /// Disabled sub-module options in "ModId:RelativePath" format (e.g.
    /// "p5rpc.misc.ps4reverts:Options/Censorship").
    /// </summary>
    public string[] DisabledOptions { get; init; } = [];

    /// <summary>
    /// Hides the corner watermark drawn by the overlay mod. Hidden by default:
    /// an always-visible badge forces an ImGui draw pass on every game frame.
    /// Set to false in overrides.json (or tick "Corner watermark" in the panel)
    /// to keep it on screen permanently.
    /// </summary>
    public bool HideWatermark { get; init; } = true;

    public static string PathFor(string dropInDirectory) =>
        Path.Combine(dropInDirectory, "overlay", "overrides.json");

    public static OverlayOverrides Load(string dropInDirectory)
    {
        var path = PathFor(dropInDirectory);
        if (!File.Exists(path))
            return new OverlayOverrides();

        try
        {
            return JsonSerializer.Deserialize<OverlayOverrides>(File.ReadAllText(path))
                ?? new OverlayOverrides();
        }
        catch (JsonException)
        {
            return new OverlayOverrides();
        }
    }

    public void Save(string dropInDirectory) =>
        AtomicFile.WriteAllText(PathFor(dropInDirectory),
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    public bool IsDisabled(string modId) =>
        DisabledMods.Contains(modId, StringComparer.OrdinalIgnoreCase);

    public bool IsOptionDisabled(string modId, string relativePath)
    {
        var key = $"{modId}:{relativePath}";
        return DisabledOptions.Contains(key, StringComparer.OrdinalIgnoreCase);
    }
}
