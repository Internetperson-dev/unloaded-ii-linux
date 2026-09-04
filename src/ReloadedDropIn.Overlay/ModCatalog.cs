using System.Text.Json;
using System.Text.Json.Nodes;
using ReloadedDropIn.Core.Configuration;
using ReloadedDropIn.Core.Discovery;

namespace ReloadedDropIn.Overlay;

/// <summary>A toggleable sub-module option within a mod.</summary>
public sealed record CatalogModOption
{
    public required string Name { get; init; }
    public required string Directory { get; init; }
    public required string RelativePath { get; init; }
    public required bool Enabled { get; set; }
}

public sealed record CatalogMod
{
    public required string ModId { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Directory { get; init; }

    /// <summary>Base mods (shipped under mods/_base-mods/) can't be toggled off.</summary>
    public required bool IsBaseMod { get; init; }

    public required bool Enabled { get; set; }

    /// <summary>
    /// Editable user config (generated/User/Mods/&lt;id&gt;/Config.json), if any.
    /// Before the mod's first run that file doesn't exist yet, so it is seeded
    /// from the Config.json the mod ships in its own folder, if present.
    /// </summary>
    public JsonObject? UserConfig { get; set; }
    public string? UserConfigPath { get; init; }
    public bool ConfigExpanded { get; set; }

    /// <summary>
    /// Friendly labels for config settings, read from the mod DLL's
    /// <c>[Display(Name = ...)]</c> attributes (config JSON key to display name).
    /// Empty when the mod has no DLL or declares no display names; the overlay then
    /// falls back to the raw JSON keys.
    /// </summary>
    public IReadOnlyDictionary<string, string> ConfigDisplayNames { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Sub-module options from the mod's Options/ directory.
    /// Empty when the mod has no Options/ folder.
    /// </summary>
    public List<CatalogModOption> Options { get; init; } = [];
}

/// <summary>
/// Disk-backed model behind the overlay: discovered mods, their toggle state
/// (via the drop-in's overrides file), and their user configs.
/// </summary>
public sealed class ModCatalog(string gameDirectory)
{
    private string ModsDirectory => Path.Combine(gameDirectory, "mods");
    private string DropInDirectory => Path.Combine(gameDirectory, "reloaded-dropin");
    private string UserConfigRoot => Path.Combine(DropInDirectory, "generated", "User", "Mods");

    public List<CatalogMod> Mods { get; } = [];

    public bool HideWatermark { get; set; }

    /// <summary>Drop-in version from reloaded-dropin/version.json, for display.</summary>
    public string DropInVersion { get; private set; } = "dev";

    /// <summary>Update info from reloaded-dropin/update-check.json (written by sync).</summary>
    public bool UpdateAvailable { get; private set; }
    public string? LatestVersion { get; private set; }
    public string? UpdateDownloadUrl { get; private set; }

    public void Reload()
    {
        Mods.Clear();
        var overrides = OverlayOverrides.Load(DropInDirectory);
        HideWatermark = overrides.HideWatermark;
        DropInVersion = ReadDropInVersion();
        ReadUpdateCheck();

        // Ensure Skip Intro defaults to OFF (disabled) for p5rpc.modloader
        // if no override exists yet and no user config has been created.
        EnsureP5RModLoaderSkipIntroDefault(overrides, out var overridesChanged);
        if (overridesChanged)
            overrides.Save(DropInDirectory);

        var scan = new ModScanner().Scan(ModsDirectory);

        foreach (var mod in scan.Mods)
        {
            // Library mods aren't user-facing; hide them like the Reloaded launcher does.
            if (mod.Manifest.IsLibrary)
                continue;

            var configPath = Path.Combine(UserConfigRoot, mod.ModId, "Config.json");
            var separator = Path.DirectorySeparatorChar;

            // Build options list with enabled state from overrides.
            var options = mod.Options.Select(o => new CatalogModOption
            {
                Name = o.Name,
                Directory = o.Directory,
                RelativePath = o.RelativePath,
                Enabled = !overrides.IsOptionDisabled(mod.ModId, o.RelativePath),
            }).ToList();

            var modDllPath = string.IsNullOrWhiteSpace(mod.Manifest.ModDll)
                ? null
                : Path.Combine(mod.Directory, mod.Manifest.ModDll);

            // Load config: user config first, then mod-shipped Config.json,
            // then extract defaults from the DLL's [Configurable] class.
            var userConfig = TryLoadConfig(configPath)
                ?? TryLoadConfig(Path.Combine(mod.Directory, "Config.json"));

            // For p5rpc.modloader, ensure P5RConfig.IntroSkip defaults to false
            // if no user config exists yet.
            if (mod.ModId.Equals(P5RModLoaderId, StringComparison.OrdinalIgnoreCase)
                && userConfig is null
                && !File.Exists(configPath))
            {
                userConfig = new JsonObject
                {
                    ["P5RConfig"] = new JsonObject { ["IntroSkip"] = false }
                };
            }

            if (userConfig is null && modDllPath is not null)
            {
                try
                {
                    var dllConfig = ModConfigMetadata.ReadDefaultConfig(modDllPath);
                    if (dllConfig is not null && dllConfig.Count > 0)
                    {
                        userConfig = dllConfig;
                        // Persist the seeded config so future reads don't need the DLL.
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                            File.WriteAllText(configPath,
                                dllConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        }
                        catch { /* best-effort */ }
                    }
                }
                catch { /* DLL inspection can fail on arch mismatch etc. — skip silently. */ }
            }

            // For p5rpc.modloader, ensure P5RConfig.IntroSkip exists and defaults to false
            if (mod.ModId.Equals(P5RModLoaderId, StringComparison.OrdinalIgnoreCase)
                && userConfig is not null)
            {
                var p5rConfig = userConfig["P5RConfig"] as JsonObject;
                if (p5rConfig is null)
                {
                    p5rConfig = new JsonObject();
                    userConfig["P5RConfig"] = p5rConfig;
                }
                if (!p5rConfig.ContainsKey("IntroSkip"))
                {
                    p5rConfig["IntroSkip"] = false;
                }
            }

            Mods.Add(new CatalogMod
            {
                ModId = mod.ModId,
                Name = string.IsNullOrWhiteSpace(mod.Manifest.ModName) ? mod.ModId : mod.Manifest.ModName,
                Version = mod.Manifest.ModVersion,
                Directory = mod.Directory,
                IsBaseMod = mod.Directory.Contains($"{separator}_base-mods{separator}", StringComparison.OrdinalIgnoreCase),
                Enabled = !overrides.IsDisabled(mod.ModId),
                UserConfigPath = configPath,
                UserConfig = userConfig,
                ConfigDisplayNames = ModConfigMetadata.ReadDisplayNames(modDllPath),
                Options = options,
            });
        }

        // User mods first, base mods at the bottom.
        Mods.Sort((a, b) => a.IsBaseMod != b.IsBaseMod
            ? a.IsBaseMod.CompareTo(b.IsBaseMod)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private const string P5RModLoaderId = "p5rpc.modloader";
    private const string SkipIntroOptionRelativePath = "__builtin/skip-intro";

    private void EnsureP5RModLoaderSkipIntroDefault(OverlayOverrides overrides, out bool changed)
    {
        changed = false;
        var configPath = Path.Combine(UserConfigRoot, P5RModLoaderId, "Config.json");

        // If user config already exists, respect it (don't override user's choice)
        if (File.Exists(configPath))
            return;

        // If override already exists for this option, respect it
        if (overrides.IsOptionDisabled(P5RModLoaderId, SkipIntroOptionRelativePath))
            return;

        // Default to disabled (OFF) - add to overrides
        var disabledOptions = overrides.DisabledOptions.ToList();
        var key = $"{P5RModLoaderId}:{SkipIntroOptionRelativePath}";
        if (!disabledOptions.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            disabledOptions.Add(key);
            overrides.DisabledOptions = [.. disabledOptions];
            changed = true;
        }
    }

    /// <summary>Persists current toggle states to the overrides file sync reads.</summary>
    public void SaveToggles()
    {
        var disabledOptions = new List<string>();
        foreach (var mod in Mods.Where(m => !m.IsBaseMod))
        {
            foreach (var option in mod.Options.Where(o => !o.Enabled))
            {
                disabledOptions.Add($"{mod.ModId}:{option.RelativePath}");
            }
        }

        var overrides = new OverlayOverrides
        {
            DisabledMods = [.. Mods.Where(m => !m.Enabled && !m.IsBaseMod).Select(m => m.ModId)],
            DisabledOptions = [.. disabledOptions],
            HideWatermark = HideWatermark,
        };
        overrides.Save(DropInDirectory);

        SyncP5RModLoaderSkipIntroConfig(overrides);
    }

    private void SyncP5RModLoaderSkipIntroConfig(OverlayOverrides overrides)
    {
        var mod = Mods.FirstOrDefault(m => m.ModId.Equals(P5RModLoaderId, StringComparison.OrdinalIgnoreCase));
        if (mod is null || mod.UserConfig is null || mod.UserConfigPath is null)
            return;

        var option = mod.Options.FirstOrDefault(o => o.RelativePath == SkipIntroOptionRelativePath);
        if (option is null)
            return;

        var isDisabled = overrides.IsOptionDisabled(P5RModLoaderId, SkipIntroOptionRelativePath);
        var introSkipValue = !isDisabled;

        var p5rConfig = mod.UserConfig["P5RConfig"] as JsonObject;
        if (p5rConfig is null)
        {
            p5rConfig = new JsonObject();
            mod.UserConfig["P5RConfig"] = p5rConfig;
        }

        p5rConfig["IntroSkip"] = introSkipValue;

        Directory.CreateDirectory(Path.GetDirectoryName(mod.UserConfigPath)!);
        File.WriteAllText(mod.UserConfigPath,
            mod.UserConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private string ReadDropInVersion()
    {
        try
        {
            var versionPath = Path.Combine(DropInDirectory, "version.json");
            if (!File.Exists(versionPath))
                return "dev";
            using var doc = JsonDocument.Parse(File.ReadAllText(versionPath));
            return doc.RootElement.GetProperty("dropin").GetString() ?? "dev";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException)
        {
            return "dev";
        }
    }

    /// <summary>Writes one mod's edited user config back to disk.</summary>
    public void SaveConfig(CatalogMod mod)
    {
        if (mod.UserConfig is null || mod.UserConfigPath is null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(mod.UserConfigPath)!);
        File.WriteAllText(mod.UserConfigPath,
            mod.UserConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject? TryLoadConfig(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void ReadUpdateCheck()
    {
        UpdateAvailable = false;
        LatestVersion = null;
        UpdateDownloadUrl = null;
        try
        {
            var path = Path.Combine(DropInDirectory, "update-check.json");
            if (!File.Exists(path))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            UpdateAvailable = doc.RootElement.GetProperty("UpdateAvailable").GetBoolean();
            LatestVersion = doc.RootElement.GetProperty("LatestVersion").GetString();
            UpdateDownloadUrl = doc.RootElement.GetProperty("DownloadUrl").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException)
        {
            // Stale or corrupt file; no update banner.
        }
    }
}
