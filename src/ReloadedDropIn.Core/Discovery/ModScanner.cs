using System.Text.Json;
using ReloadedDropIn.Core.Manifests;

namespace ReloadedDropIn.Core.Discovery;

/// <summary>
/// Finds Reloaded mods under a mods/ directory.
///
/// Rule (plan §12): a mod is a directory containing a valid ModConfig.json.
/// The scan is depth-limited, never executes mod code, rejects duplicate mod IDs
/// deterministically (lexicographically-first directory wins), and reports every
/// ignored entry with a reason.
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
    /// folders that live inside the Options/ tree (e.g. BASE.CPK, FONT/,
    /// BUSTUP/...). When present, the manifest is treated as the authoritative
    /// option list and the on-disk scan is filtered down to those paths.
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
                var manifest = ModManifest.TryParse(File.ReadAllText(manifestPath), out var error);
                if (manifest is null)
                    issues.Add(new ScanIssue(ScanIssueKind.InvalidManifest, manifestPath, error!));
                else
                {
                    var options = ScanOptions(subdirectory, issues);
                    var contentSubs = ScanContentSubModules(subdirectory, issues);
                    var allOptions = options.Concat(contentSubs).ToList();
                    mods.Add(new DiscoveredMod { Manifest = manifest, Directory = subdirectory, Options = allOptions });
                }
            }

            // Always recurse: some mods contain nested mods in subdirectories
            // (e.g. texturefixesproject has sub-mods with their own ModConfig.json).
            ScanDirectory(subdirectory, depth + 1, mods, issues);
        }
    }

    /// <summary>
    /// Scans for sub-module options inside a mod's Options/ directory.
    /// When the mod ships a Sewer56.Update.Metadata.json its layout (one-level
    /// Options/&lt;option&gt; or two-level Options/&lt;category&gt;/&lt;option&gt;) is used to
    /// expose exactly the declared option folders and drop stray content folders
    /// (BASE.CPK, FONT/, ...). Without a manifest the classic one-level layout is
    /// assumed and every direct child of Options/ is an option. Directories ending
    /// with .disabled are mapped back to their original name at any depth.
    /// </summary>
    private IReadOnlyList<ModOption> ScanOptions(string modDirectory, List<ScanIssue> issues)
    {
        var optionsDir = Path.Combine(modDirectory, OptionsDirectoryName);
        if (!Directory.Exists(optionsDir))
            return [];

        var options = new List<ModOption>();
        var declared = ReadUpdateMetadataOptionPaths(modDirectory);

        // A release manifest tells us exactly how the Options/ tree is laid out
        // (one-level: Options/<option>, or two-level: Options/<category>/<option>),
        // so options can be resolved at the declared depth and stray content
        // folders can be dropped. Without a manifest we fall back to the classic
        // one-level layout where every direct child of Options/ is an option.
        var optionDepth = declared is null ? 1 : declared.Any(p => p[OptionsDirectoryName.Length + 1..].Contains('/')) ? 2 : 1;

        ScanOptionLevel(optionsDir, canonicalDirectory: optionsDir, optionsRoot: optionsDir,
            depth: 0, optionDepth: optionDepth, declared: declared, options: options, issues: issues);

        return options.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Reads the set of option paths declared in Sewer56.Update.Metadata.json, or
    /// null when the mod has no (readable) manifest. Option granularity mirrors the
    /// mod's own layout: when an option path nests further (Options/Category/Option)
    /// the first two segments form the option; otherwise the first segment does.
    /// </summary>
    private static HashSet<string>? ReadUpdateMetadataOptionPaths(string modDirectory)
    {
        var metadataPath = Path.Combine(modDirectory, UpdateMetadataFileName);
        if (!File.Exists(metadataPath))
            return null;

        List<string> filePaths;
        try
        {
            filePaths = ReadUpdateMetadataFilePaths(metadataPath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (filePaths.Count == 0)
            return null;

        const string optionsPrefix = OptionsDirectoryName + "/";
        var twoLevel = filePaths.Any(p =>
            p.Replace('\\', '/').StartsWith(optionsPrefix, StringComparison.OrdinalIgnoreCase) &&
            p.Replace('\\', '/')[optionsPrefix.Length..].Contains('/'));

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in filePaths)
        {
            var normalized = relative.Replace('\\', '/');
            if (!normalized.StartsWith(optionsPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var segments = normalized[optionsPrefix.Length..].Split('/');
            if (segments.Length == 0)
                continue;

            var optionPath = twoLevel
                ? segments.Length >= 2 ? string.Join('/', segments.Take(2)) : null
                : segments.Length >= 1 ? segments[0] : null;
            if (optionPath is not null)
                declared.Add(optionsPrefix + optionPath);
        }

        return declared;
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

            // Rebuild the canonical path from the canonical parent so a .disabled
            // folder along the chain (e.g. Options/Censorship.disabled/Ryuji Shoes)
            // doesn't leak the suffix into the logical option path.
            var canonicalPath = Path.Combine(canonicalDirectory, name);

            if (depth + 1 >= optionDepth)
            {
                // At the option level. Everything nested below (BASE.CPK, FONT/,
                // BUSTUP/...) is a mod's internal layout, not more options.
                var relativePath = $"{OptionsDirectoryName}/{Path.GetRelativePath(optionsRoot, canonicalPath).Replace(Path.DirectorySeparatorChar, '/')}";
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

            // Still above the option level: grouping folders are recursed into,
            // using the on-disk path so a .disabled parent is still explored.
            ScanOptionLevel(subdir, canonicalPath, optionsRoot, depth + 1, optionDepth, declared, options, issues);
        }
    }

    /// <summary>
    /// Detects content subdirectories at the mod's root level — folders that
    /// don't have a ModConfig.json and don't contain DLLs. Mods like
    /// p5rpc.texturefixesproject ship texture packs as subdirectories (e.g.
    /// BetterJokerTycoonPortrait/) that users want to toggle on/off.
    /// Directories named Options, Cache, x86, x64, or starting with _
    /// are excluded. Directories ending with .disabled are mapped back to
    /// their original name.
    /// </summary>
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

            // Skip well-known non-content directories.
            if (name.Equals(OptionsDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Cache", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith('_'))
                continue;

            // Use the actual on-disk path for filesystem checks (the canonical
            // path may not exist if the dir is .disabled).
            var diskPath = subdir;

            // Skip directories that have a ModConfig.json (those are nested mods, not content).
            if (File.Exists(Path.Combine(diskPath, ModManifest.FileName)))
                continue;

            // Skip directories that contain DLLs (those are dependency folders, not content).
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

            // This looks like a content sub-module (e.g. texture pack folder).
            options.Add(new ModOption
            {
                Name = name,
                Directory = canonicalPath,
                RelativePath = name,
            });
        }

        return options.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Strips the .disabled suffix from a directory name so the scanner maps
    /// renamed (disabled) directories back to their original option identity.
    /// Always returns the canonical (non-.disabled) directory path so that
    /// OptionStateHealer can derive the correct .disabled path.
    /// </summary>
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
