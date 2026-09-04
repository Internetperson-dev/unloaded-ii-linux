using System.Text.Json;
using ReloadedDropIn.Core.Manifests;

namespace ReloadedDropIn.Core.Discovery;

/// <summary>
/// Finds Reloaded mods under a mods/ directory.
/// Handles distinct mod architectures:
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

    public const string UpdateMetadataFileName = "Sewer56.Update.Metadata.json";

    private static readonly HashSet<string> s_ignoredContentFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        OptionsDirectoryName,
        "Cache", "x86", "x64", "bin", "obj", ".git", ".vs",
        "FEmulator", "P5REssentials", "BGME", "UnrealEssentials", 
        "CriFs.V2.Hook.ReloadedII", "CriFs.V2.Hook.ReloadedII.Prs",
        "RyuModManager", "AFSLib", "Afs2Lib",
        // Game asset directories that shouldn't be treated as standalone mods/options directly
        "FONT", "IMAGE", "ITEM", "OBJECT", "RECOVERY", "ROADMAP", 
        "SKILL", "SP_ATTACK", "BATTLE", "CAMP", "EVENT", "FIELD", 
        "SOUND", "GUI", "BGM", "SE", "VOICE", "MOVIE", "BUSTUP", "MINIGAME"
    };

    public ScanResult Scan(string modsDirectory)
    {
        var mods = new List<DiscoveredMod>();
        var issues = new List<ScanIssue>();

        if (!Directory.Exists(modsDirectory))
            return new ScanResult { Mods = [], Issues = [] };

        try
        {
            foreach (var file in Directory.EnumerateFiles(modsDirectory))
            {
                if (!Path.GetFileName(file).Equals("PUT_MODS_HERE.txt", StringComparison.OrdinalIgnoreCase))
                    issues.Add(new ScanIssue(ScanIssueKind.IgnoredEntry, file, "loose file in mods/ (mods must be extracted folders)"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new ScanIssue(ScanIssueKind.IgnoredEntry, modsDirectory, "could not enumerate root directory files"));
        }

        ScanDirectory(modsDirectory, depth: 0, mods, issues);

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
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            issues.Add(new ScanIssue(ScanIssueKind.IgnoredEntry, directory, "permission denied or I/O error"));
            return;
        }

        foreach (var subdirectory in subdirectories)
        {
            var manifestPath = Path.Combine(subdirectory, ModManifest.FileName);
            if (File.Exists(manifestPath))
            {
                string manifestText;
                try
                {
                    manifestText = File.ReadAllText(manifestPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new ScanIssue(ScanIssueKind.InvalidManifest, manifestPath, $"Failed to read manifest: {ex.Message}"));
                    goto Recurse;
                }

                var manifest = ModManifest.TryParse(manifestText, out var error);
                if (manifest is null)
                {
                    issues.Add(new ScanIssue(ScanIssueKind.InvalidManifest, manifestPath, error!));
                }
                else
                {
                    var options = ScanOptions(subdirectory, issues);
                    var contentSubs = ScanContentSubModules(subdirectory, issues);
                    var configOptions = ScanModConfigOptions(manifestText, subdirectory);

                    var allOptions = options
                        .Concat(contentSubs)
                        .Concat(configOptions)
                        .DistinctBy(o => o.RelativePath, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    mods.Add(new DiscoveredMod 
                    { 
                        Manifest = manifest, 
                        Directory = subdirectory, 
                        Options = allOptions 
                    });
                }
            }

        Recurse:
            ScanDirectory(subdirectory, depth + 1, mods, issues);
        }
    }

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
        }

        return result;
    }

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
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            issues.Add(new ScanIssue(ScanIssueKind.IgnoredEntry, directory, "permission denied or I/O error reading Options/"));
            return;
        }

        foreach (var subdir in subdirectories)
        {
            var rawName = Path.GetFileName(subdir);
            var (name, _) = NormalizeDisabledDirectory(rawName, subdir);
            var canonicalPath = Path.Combine(canonicalDirectory, name);

            bool isOptionLevel = (depth + 1 >= optionDepth);

            if (!isOptionLevel)
            {
                try
                {
                    bool hasContentFiles = Directory.EnumerateFiles(subdir, "*.*", SearchOption.TopDirectoryOnly)
                        .Any(f => !Path.GetFileName(f).Equals(UpdateMetadataFileName, StringComparison.OrdinalIgnoreCase));
                    if (hasContentFiles)
                    {
                        isOptionLevel = true;
                    }
                }
                catch
                {
                }
            }

            if (isOptionLevel)
            {
                var relFromRoot = Path.GetRelativePath(optionsRoot, canonicalPath);
                var checkPath = $"{OptionsDirectoryName}/{relFromRoot.Replace(Path.DirectorySeparatorChar, '/')}";

                if (declared is null || declared.Contains(checkPath) || declared.Any(d => d.StartsWith(checkPath, StringComparison.OrdinalIgnoreCase)))
                {
                    options.Add(new ModOption
                    {
                        Name = name,
                        Directory = canonicalPath,
                        RelativePath = Path.Combine(OptionsDirectoryName, relFromRoot),
                    });
                }
            }
            else
            {
                ScanOptionLevel(subdir, canonicalPath, optionsRoot, depth + 1, optionDepth, declared, options, issues);
            }
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
        catch (Exception)
        {
            return [];
        }

        foreach (var subdir in subdirectories)
        {
            var rawName = Path.GetFileName(subdir);
            var (name, canonicalPath) = NormalizeDisabledDirectory(rawName, subdir);

            if (s_ignoredContentFolders.Contains(name) || name.StartsWith('_') || name.StartsWith('.'))
                continue;

            if (name.Contains(".BIN", StringComparison.OrdinalIgnoreCase) ||
                name.Contains(".CPK", StringComparison.OrdinalIgnoreCase) ||
                name.Contains(".PAC", StringComparison.OrdinalIgnoreCase) ||
                name.Contains(".ARC", StringComparison.OrdinalIgnoreCase) ||
                name.Contains(".AWB", StringComparison.OrdinalIgnoreCase) ||
                name.Contains(".ACB", StringComparison.OrdinalIgnoreCase))
                continue;

            if (File.Exists(Path.Combine(subdir, ModManifest.FileName)))
                continue;

            bool hasDlls;
            try
            {
                hasDlls = Directory.EnumerateFiles(subdir, "*.dll").Any();
            }
            catch
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
