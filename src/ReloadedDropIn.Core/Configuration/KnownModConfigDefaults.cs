using System.Text.Json.Nodes;

namespace ReloadedDropIn.Core.Configuration;

/// <summary>
/// Defaults the overlay applies to mods that have not written a user config yet.
/// Without this, the FPS unlocker uses its upstream 99999 FPS default and never
/// shows a settings button until the mod has run once.
/// </summary>
public static class KnownModConfigDefaults
{
    public const string FpsUnlockerModId = "p5rpc.fpsUnlocker";
    public const double FpsUnlockerDefaultMaxFps = 240;

    /// <summary>Creates the config used the first time the FPS unlocker is seen.</summary>
    public static JsonObject CreateFpsUnlockerConfig() => new()
    {
        ["MaxFps"] = FpsUnlockerDefaultMaxFps,
        ["TieGameSpeedToFps"] = false,
        ["DebugEnabled"] = false,
    };

    /// <summary>
    /// Adds FPS unlocker settings missing from an existing config. Explicit user
    /// values are preserved, including a previously chosen framerate limit.
    /// </summary>
    /// <returns>True when at least one setting was added.</returns>
    public static bool FillFpsUnlockerMissingSettings(JsonObject config)
    {
        var defaults = CreateFpsUnlockerConfig();
        var changed = false;

        foreach (var setting in defaults)
        {
            if (config.ContainsKey(setting.Key))
                continue;

            config[setting.Key] = setting.Value?.DeepClone();
            changed = true;
        }

        return changed;
    }
}
