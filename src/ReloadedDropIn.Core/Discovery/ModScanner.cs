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
    /// Reloaded-II's release manifest. Its Hashes.Files contain every path the mod
    /// ships, including option folders that may not be present on disk yet (e.g.
    /// optional content the user has not downloaded). We use it to surface the
    /// full, authoritative option set even when the folder was never installed.
    /// </summary>
    public const string UpdateMetadataFileName = "Sewer56.Update.Metadata.json";

    /// <summary>
    /// Names of the game's own top-level asset folders. As soon as one of these
    /// (or any "*.CPK" archive folder) turns up as a direct child of a candidate
    /// option folder, everything below it is the mod's payload - the game's own
    /// directory layout - not further options to expose as toggles. Extend this
    /// set if a mod ships content under a top-level folder not listed here.
    /// </summary>
    private static readonly HashSet<string> GameContentRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MODEL", "FIELD", "FONT", "BATTLE", "BUSTUP", "EVENT", "IMAGE", "MINIGAME", "DATA",
    };

    private static bool IsGameContentRoot(string directoryName) =>
        directoryName.EndsWith(".CPK", StringComparison.OrdinalIgnoreCase)
        || GameContentRootNames.Contains(directoryName);

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
    /// The tree is walked recursively, so option folders nested several levels
    /// deep (e.g. Options/Censorship/Ryuji Shoes/) are discovered. Grouping
    /// folders that only contain further named folders are not themselves
    /// exposed as toggles - only the folders that actually hold the mod's
    /// content are. Directories ending with .disabled are mapped back to
    /// their original name.
    /// </summary>
    private IReadOnlyList<ModOption> ScanOptions(string modDirectory, List<ScanIssue> issues)
    {
        var optionsDir = Path.Combine(modDirectory, OptionsDirectoryName);
        if (!Directory.Exists(optionsDir))
            return [];

        var options = new List<ModOption>();
        ScanOptionDirectories(optionsDir, canonicalDirectory: optionsDir, optionsRoot: optionsDir, options, issues);
        MergeUpdateMetadataOptions(modDirectory, optionsRoot: optionsDir, options);
        return options.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Adds option folders declared in Sewer56.Update.Metadata.json that are
    /// not present on disk, so the overlay shows the mod's full option set
    /// (e.g. optional content the user has not downloaded yet). Folders already
    /// discovered on disk are left untouched; the metadata only supplements.
    ///
    /// The option granularity mirrors the on-disk scan: when the mod already
    /// has two-level options (Options/&lt;category&gt;/&lt;option&gt;/) the metadata is
    /// read the same way; otherwise one-level options are assumed.
    /// </summary>
    private void MergeUpdateMetadataOptions(string modDirectory, string optionsRoot, List<ModOption> options)
    {
        if (options.Count == 0)
            return;

        var metadataPath = Path.Combine(modDirectory, UpdateMetadataFileName);
        if (!File.Exists(metadataPath))
            return;

        List<string> filePaths;
        try
        {
            filePaths = ReadUpdateMetadataFilePaths(metadataPath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (filePaths.Count == 0)
            return;

        var twoLevel = options.Any(o => o.RelativePath.StartsWith("Options/", StringComparison.Ordinal)
            && o.RelativePath["Options/".Length..].Contains('/'));

        var existing = new HashSet<string>(options.Select(o => o.RelativePath), StringComparer.OrdinalIgnoreCase);
        const string optionsPrefix = "Options/";

        foreach (var relative in filePaths)
        {
            var normalized = relative.Replace('\\', '/');
            if (!normalized.StartsWith(optionsPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var segments = normalized[optionsPrefix.Length..].Split('/');
            var candidate = twoLevel
                ? segments.Length >= 2 ? string.Join('/', segments.Take(2)) : null
                : segments.Length >= 1 ? segments[0] : null;
            if (candidate is null)
                continue;

            var relativePath = optionsPrefix + candidate;
            if (!existing.Add(relativePath))
                continue;

            options.Add(new ModOption
            {
                Name = candidate.Split('/')[^1],
                Directory = Path.Combine(optionsRoot, candidate),
                RelativePath = relativePath,
            });
        }
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

    /// <summary>
    /// Walks the Options/ tree looking for the folders that should actually be
    /// exposed as toggles. A folder is treated as an option (a "boundary") and
    /// NOT explored further as soon as either it has no subdirectories at all,
    /// or one of its immediate children is the start of the game's own content
    /// structure (see <see cref="IsGameContentRoot"/>) - e.g. a "BASE.CPK"
    /// archive folder or a "BUSTUP"/"MODEL"/etc. top-level game folder. Anything
    /// short of that boundary (e.g. a category like "Censorship" that only
    /// contains further named option folders) is a grouping folder and is
    /// recursed into, but never added as a toggle itself.
    /// </summary>
    private void ScanOptionDirectories(
        string directory,
        string canonicalDirectory,
        string optionsRoot,
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

            if (IsOptionBoundary(subdir, issues))
            {
                // This folder IS the toggle (e.g. Options/Censorship/Ryuji Shoes).
                // Stop here - everything below is the mod's own payload
                // (BASE.CPK/MODEL/..., BUSTUP/..., etc.), not more options.
                // Relative paths are always built with '/' so later string
                // comparisons (e.g. in MergeUpdateMetadataOptions) are reliable
                // regardless of the OS's native path separator.
                var relativePath = Path.GetRelativePath(optionsRoot, canonicalPath).Replace('\\', '/');
                options.Add(new ModOption
                {
                    Name = name,
                    Directory = canonicalPath,
                    RelativePath = $"{OptionsDirectoryName}/{relativePath}",
                });
            }
            else
            {
                // Grouping folder (e.g. a category) - keep looking underneath it.
                ScanOptionDirectories(subdir, canonicalPath, optionsRoot, options, issues);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="subdir"/> should be exposed as a toggle rather
    /// than explored further: either it has no subdirectories at all (a bare,
    /// flat option), or one of its immediate children marks the start of the
    /// game's own content structure - in which case <paramref name="subdir"/>
    /// itself is the option, and its contents are the payload, not more options.
    /// </summary>
    private bool IsOptionBoundary(string subdir, List<ScanIssue> issues)
    {
        List<string> children;
        try
        {
            children = Directory.EnumerateDirectories(subdir).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new ScanIssue(ScanIssueKind.IgnoredEntry, subdir, "permission denied reading Options/"));
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        if (children.Count == 0)
            return true;

        return children.Any(child => IsGameContentRoot(Path.GetFileName(child) ?? string.Empty));
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
