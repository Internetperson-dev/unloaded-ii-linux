using System.Text.Json;
using ReloadedDropIn.Core.Manifests;

namespace ReloadedDropIn.Core.Discovery;

/// <summary>
/// Finds Reloaded mods under a mods/ directory.
///
/// Handles 3 distinct mod architectures:
/// 1. Native Reloaded-II C# Options (Code-Based via ModConfig.json)
/// 2. Directory-Based Multi-Option Mods (Category folders under Options/)
/// 3. Standalone Sub-Mod / Toggle Folders (Direct subfolders with optional .disabled suffixes)
/// </summary>
public sealed class ModScanner
{
    /// <summary>How many directory levels below mods/ may contain a manifest.</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Name of the directory that holds sub-module options within a mod.</summary>
    public const string OptionsDirectoryName = "Options";

    /// <summary>
    /// Reloaded-II's release manifest. Its Hashes.Files list every path the mod
    /// ships, which lets us tell real option folders apart from stray content
    /// folders that live inside the Options/ tree (e.g. BASE.CPK, FONT/, BUSTUP/...).
    /// </summary>
    public const string UpdateMetadataFileName = "Sewer56.Update.Metadata.json";

    public ScanResult Scan(string modsDirectory)
    {
        var mods = new List<DiscoveredMod>();
        var issues = new List<ScanIssue>();

        if (!Directory.Exists(modsDirectory))
            return new ScanResult { Mods = [], Issues = [] };

        foreach (var file in Directory.EnumerateFiles(modsDirectory))
        {
            if (!Path.GetFileName(file).Equals("PUT_MODS_HERE.txt", StringComparison.OrdinalIgnoreCase))
                issues.Add(new ScanIssue(ScanIssueKind.IgnoredEntry, file, "loose file in mods/ (mods must be extracted folders)"));
        }

        ScanDirectory(modsDirectory, depth: 0, mods, issues);

        // Deterministic order: sort candidates by directory path, then keep the first
        // occurrence of each ModId so duplicate resolution never depends on OS enumeration order.
        var sorted = mods.OrderBy(m => m.Directory, StringComparer.Ordinal).ToList();
        var byId = new Dictionary<string, DiscoveredMod>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<DiscoveredMod>();

        foreach (var mod in sorted)
        {
            if (byId.TryGetValue(mod.ModId, out var existing))
            {
                issues.Add(new ScanIssue(
                    ScanIssueKind.DuplicateModId,
                    mod.Directory,
                    $"duplicate ModId '{mod.ModId}' (already provided by {existing.Directory})"));
                continue;
            }

            byId.Add(mod.ModId, mod);
            unique.Add(mod);
        }

        return new ScanResult
        {
            Mods = unique.OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase).ToList(),
            Issues = issues,
        };
    }

    private void ScanDirectory(string directory, int depth, List<DiscoveredMod> mods, List<ScanIssue> issues)
    {
        if (depth > MaxDepth)
            return;

        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(directory);
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new ScanIssue(ScanIssueKind.IgnoredEntry, directory, "permission denied"));
            return;
        }

        foreach (var subdirectory in subdirectories)
        {
            var manifestPath = Path.Combine(subdirectory, ModManifest.FileName);
            if (File.Exists(manifestPath))
            {
                var manifestText = File.ReadAllText(manifestPath);
                var manifest = ModManifest.TryParse(manifestText, out var error);
                if (manifest is null)
                {
                    issues.Add(new ScanIssue(ScanIssueKind.InvalidManifest, manifestPath, error!));
                }
                else
                {
                    // 1. Scan Options/ folder options
                    var options = ScanOptions(subdirectory, issues);

                    // 2. Scan direct content sub-module folders
                    var contentSubs = ScanContentSubModules(subdirectory, issues);

                    // 3. Extract JSON/C# configurable options directly from ModConfig.json
                    var configOptions = ScanModConfigOptions(manifestText, subdirectory);

                    var allOptions = options
                        .Concat(contentSubs)
                        .Concat(configOptions)
                        .DistinctBy(o => o.Directory, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    mods.Add(new DiscoveredMod 
                    { 
                        Manifest = manifest, 
                        Directory = subdirectory, 
                        Options = allOptions 
                    });
                }
            }

            // Always recurse: some mods contain nested mods in subdirectories
            // (e.g. texturefixesproject has sub-mods with their own ModConfig.json).
            ScanDirectory(subdirectory, depth + 1, mods, issues);
        }
    }

    /// <summary>
    /// Reads configurable option definitions declared natively in ModConfig.json.
    /// </summary>
    private static IEnumerable<ModOption> ScanModConfigOptions(string manifestText, string modDirectory)
    {
        var result = new List<ModOption>();
        try
        {
            using var doc = JsonDocument.Parse(manifestText);
            if (doc.RootElement.TryGetProperty("ConfigurableOptions", out var configOpts) &&
                configOpts.ValueKind == JsonValueKind.Array)
            {
                foreach (var opt in configOpts.EnumerateArray())
                {
                    if (opt.TryGetProperty("Id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    {
                        var id = idProp.GetString()!;
                        var name = opt.TryGetProperty("Name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                            ? nameProp.GetString()!
                            : id;

                        result.Add(new ModOption
                        {
                            Name = name,
                            Directory = Path.Combine(modDirectory, ".config", id),
                            RelativePath = $"config:{id}"
                        });
                    }
                }
            }
        }
        catch
        {
            // Suppress non-critical deserialization errors during options parsing
        }

        return result;
    }

    /// <summary>
    /// Scans for sub-module options inside a mod's Options/ directory.
    /// </summary>
    private IReadOnlyList<ModOption> ScanOptions(string modDirectory, List<ScanIssue> issues)
    {
        var optionsDir = Path.Combine(modDirectory, OptionsDirectoryName);
        if (!Directory.Exists(optionsDir))
            return [];

        var options = new List<ModOption>();
        var (declared, optionDepth) = ReadUpdateMetadataOptionPaths(modDirectory);

        ScanOptionLevel(optionsDir, canonicalDirectory: optionsDir, optionsRoot: optionsDir,
            depth: 0, optionDepth: optionDepth, declared: declared, options: options, issues: issues);

        return options.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (HashSet<string>? OptionPaths, int OptionDepth) ReadUpdateMetadataOptionPaths(string modDirectory)
    {
        var metadataPath = Path.Combine(modDirectory, UpdateMetadataFileName);
        if (!File.Exists(metadataPath))
            return (null, 1);

        List<string> filePaths;
        try
        {
            filePaths = ReadUpdateMetadataFilePaths(metadataPath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, 1);
        }

        if (filePaths.Count == 0)
            return (null, 1);

        const string optionsPrefix = OptionsDirectoryName + "/";
        var maxSegments = 0;
        foreach (var relative in filePaths)
        {
            var normalized = relative.Replace('\\', '/');
            if (!normalized.StartsWith(optionsPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var segmentCount = normalized[optionsPrefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
            maxSegments = Math.Max(maxSegments, segmentCount);
        }

        var optionDepth = maxSegments >= 3 ? 2 : 1;

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in filePaths)
        {
            var normalized = relative.Replace('\\', '/');
            if (!normalized.StartsWith(optionsPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var segments = normalized[optionsPrefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < optionDepth)
                continue;

            declared.Add(optionsPrefix + string.Join('/', segments.Take(optionDepth)));
        }

        return (declared, optionDepth);
    }

    private static List<string> ReadUpdateMetadataFilePaths(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("Hashes", out var hashes) ||
            !hashes.TryGetProperty("Files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var entry in files.EnumerateArray())
        {
            if (entry.TryGetProperty("RelativePath", out var relative) &&
                relative.ValueKind == JsonValueKind.String)
                result.Add(relative.GetString()!);
        }

        return result;
    }

    private void ScanOptionLevel(
        string directory,
        string canonicalDirectory,
        string optionsRoot,
        int depth,
        int optionDepth,
        HashSet<string>? declared,
        List<ModOption> options,
        List<ScanIssue> issues)
    {
        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(directory);
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new ScanIssue(ScanIssueKind.IgnoredEntry, directory, "permission denied reading Options/"));
            return;
        }

        foreach (var subdir in subdirectories)
        {
            var rawName = Path.GetFileName(subdir);
            var (name, _) = NormalizeDisabledDirectory(rawName, subdir);
            var canonicalPath = Path.Combine(canonicalDirectory, name);

            if (depth + 1 >= optionDepth)
            {
                var relFromRoot = Path.GetRelativePath(optionsRoot, canonicalPath).Replace(Path.DirectorySeparatorChar, '/');
                var relativePath = $"{OptionsDirectoryName}/{relFromRoot}";

                if (declared is null || declared.Contains(relativePath))
                {
                    options.Add(new ModOption
                    {
                        Name = name,
                        Directory = canonicalPath,
                        RelativePath = relativePath,
                    });
                }

                continue;
            }

            ScanOptionLevel(subdir, canonicalPath, optionsRoot, depth + 1, optionDepth, declared, options, issues);
        }
    }

    private IReadOnlyList<ModOption> ScanContentSubModules(string modDirectory, List<ScanIssue> issues)
    {
        var options = new List<ModOption>();
        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(modDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var subdir in subdirectories)
        {
            var rawName = Path.GetFileName(subdir);
            var (name, canonicalPath) = NormalizeDisabledDirectory(rawName, subdir);

            if (name.Equals(OptionsDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Cache", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith('_'))
            {
                continue;
            }

            var diskPath = subdir;

            if (File.Exists(Path.Combine(diskPath, ModManifest.FileName)))
                continue;

            bool hasDlls;
            try
            {
                hasDlls = Directory.EnumerateFiles(diskPath, "*.dll").Any();
            }
            catch (IOException)
            {
                continue;
            }

            if (hasDlls)
                continue;

            options.Add(new ModOption
            {
                Name = name,
                Directory = canonicalPath,
                RelativePath = name,
            });
        }

        return options.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (string Name, string Directory) NormalizeDisabledDirectory(string rawName, string fullPath)
    {
        const string disabledSuffix = ".disabled";
        if (rawName.EndsWith(disabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var originalName = rawName[..^disabledSuffix.Length];
            var canonicalPath = Path.Combine(Path.GetDirectoryName(fullPath)!, originalName);
            return (originalName, canonicalPath);
        }

        return (rawName, fullPath);
    }
}
