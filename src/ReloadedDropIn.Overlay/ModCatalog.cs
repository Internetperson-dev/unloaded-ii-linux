using System.Text.Json;
using System.Text.Json.Nodes;
using ReloadedDropIn.Core.Configuration;
using ReloadedDropIn.Core.Discovery;

namespace ReloadedDropIn.Overlay;

/// <summary>A toggleable sub-module option within a mod.</summary>
public sealed record CatalogModOption
{
    private string[]? _segments;

    public required string Name { get; init; }
    public required string Directory { get; init; }
    public required string RelativePath { get; init; }
    public required bool Enabled { get; set; }

    /// <summary>
    /// Cached path segments for the options tree. The panel renders every frame
    /// it is open, so splitting RelativePath there would allocate on each frame.
    /// </summary>
    public string[] Segments => _segments ??= SplitSegments(RelativePath);

    private static string[] SplitSegments(string relativePath)
    {
        var path = relativePath.StartsWith("Options/", StringComparison.Ordinal)
            ? relativePath["Options/".Length..]
            : relativePath;
        return path.Split('/');
    }
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
    /// from the Config.json the mod ships in its own folder, the mod DLL's
    /// defaults, or a known default (FPS unlocker: 240 FPS).
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

    /// <summary>
    /// Ready-made partitions of <see cref="Mods"/>. The panel renders every
    /// frame it is open; filtering and allocating a new list each frame is
    /// avoidable work on the game's render thread.
    /// </summary>
    public List<CatalogMod> UserMods { get; } = [];
    public List<CatalogMod> BaseMods { get; } = [];

    public bool HideWatermark { get; set; }

    /// <summary>P3R Enhanced (P3RE.dll) is missing from the game directory.</summary>
    public bool P3rEnhancedMissing { get; private set; }

    /// <summary>Drop-in version from reloaded-dropin/version.json, for display.</summary>
    public string DropInVersion { get; private set; } = "dev";

    /// <summary>Update info from reloaded-dropin/update-check.json (written by sync).</summary>
    public bool UpdateAvailable { get; private set; }
    public string? LatestVersion { get; private set; }
    public string? UpdateDownloadUrl { get; private set; }

    public void Reload()
    {
        Mods.Clear();
        UserMods.Clear();
        BaseMods.Clear();
        // The overlay runs inside the game, so gameDirectory is the folder
        // holding P3R.exe (UE4: P3R/Binaries/Win64). P3RE.dll sits beside it.
        P3rEnhancedMissing = File.Exists(Path.Combine(gameDirectory, "P3R.exe"))
            && !File.Exists(Path.Combine(gameDirectory, "P3RE.dll"));
        var overrides = OverlayOverrides.Load(DropInDirectory);
        HideWatermark = overrides.HideWatermark;
        DropInVersion = ReadDropInVersion();
        ReadUpdateCheck();

        // Ensure Skip Intro defaults to OFF (disabled) for p5rpc.modloader
        // if no override exists yet and no user config has been created.
        overrides = EnsureP5RModLoaderSkipIntroDefault(overrides, out var overridesChanged);
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
            var userConfigOnDisk = TryLoadConfig(configPath);
            var userConfig = userConfigOnDisk
                ?? TryLoadConfig(Path.Combine(mod.Directory, "Config.json"));

            // For p5rpc.modloader, ensure P5RConfig.IntroSkip defaults to false
            // if no user config exists yet.
            if (mod.ModId.Equals(P5RModLoaderId, StringComparison.OrdinalIgnoreCase)
                && userConfig is null
                && userConfigOnDisk is null)
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
                        WriteConfig(configPath, dllConfig);
                    }
                }
                catch { /* DLL inspection can fail on arch mismatch etc. — skip silently. */ }
            }

            // The FPS unlocker ships with an effectively uncapped 99999 FPS
            // default. Seed a user config at ~240 so its settings are editable
            // before the mod's first run, but never override a user's own file.
            if (mod.ModId.Equals(KnownModConfigDefaults.FpsUnlockerModId, StringComparison.OrdinalIgnoreCase))
            {
                if (userConfigOnDisk is null)
                {
                    userConfig ??= KnownModConfigDefaults.CreateFpsUnlockerConfig();
                    userConfig["MaxFps"] = KnownModConfigDefaults.FpsUnlockerDefaultMaxFps;
                    KnownModConfigDefaults.FillFpsUnlockerMissingSettings(userConfig);
                    WriteConfig(configPath, userConfig);
                }
                else if (userConfig is not null &&
                         KnownModConfigDefaults.FillFpsUnlockerMissingSettings(userConfig))
                {
                    WriteConfig(configPath, userConfig);
                }
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

        foreach (var mod in Mods)
            (mod.IsBaseMod ? BaseMods : UserMods).Add(mod);
    }

    private const string P5RModLoaderId = "p5rpc.modloader";
    private const string SkipIntroOptionRelativePath = "__builtin/skip-intro";

    private OverlayOverrides EnsureP5RModLoaderSkipIntroDefault(OverlayOverrides overrides, out bool changed)
    {
        changed = false;
        var configPath = Path.Combine(UserConfigRoot, P5RModLoaderId, "Config.json");

        // If user config already exists, respect it (don't override user's choice)
        if (File.Exists(configPath))
            return overrides;

        // If override already exists for this option, respect it
        if (overrides.IsOptionDisabled(P5RModLoaderId, SkipIntroOptionRelativePath))
            return overrides;

        // Default to disabled (OFF) - create new overrides with the option added
        var disabledOptions = overrides.DisabledOptions.ToList();
        var key = $"{P5RModLoaderId}:{SkipIntroOptionRelativePath}";
        if (!disabledOptions.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            disabledOptions.Add(key);
            // Use 'with' expression to create new record with modified DisabledOptions
            overrides = overrides with { DisabledOptions = [.. disabledOptions] };
            changed = true;
        }

        return overrides;
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

    private static void WriteConfig(string path, JsonObject config)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort seeding: the overlay can still edit the in-memory copy
            // and SaveConfig will report a real failure if the user changes it.
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
