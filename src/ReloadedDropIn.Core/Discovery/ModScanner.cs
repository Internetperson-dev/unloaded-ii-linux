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
    /// folders that contain further subdirectories are not themselves exposed
    /// as toggles — only the leaf folders that hold the actual content are.
    /// Directories ending with .disabled are mapped back to their original name.
    /// </summary>
    private IReadOnlyList<ModOption> ScanOptions(string modDirectory, List<ScanIssue> issues)
    {
        var optionsDir = Path.Combine(modDirectory, OptionsDirectoryName);
        if (!Directory.Exists(optionsDir))
            return [];

        var options = new List<ModOption>();
        ScanOptionDirectories(optionsDir, optionsDir, options, issues);
        return options.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ScanOptionDirectories(
        string directory,
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
            var (name, canonicalPath) = NormalizeDisabledDirectory(rawName, subdir);

            // Recurse first so nested option folders are discovered regardless
            // of whether this directory is itself exposed as a toggle. Use the
            // on-disk path so a .disabled parent is still explored.
            ScanOptionDirectories(subdir, optionsRoot, options, issues);

            // Grouping folders (contain further subdirectories) are not toggles.
            bool hasChildren;
            try
            {
                hasChildren = Directory.EnumerateDirectories(subdir).Any();
            }
            catch (IOException)
            {
                continue;
            }

            if (hasChildren)
                continue;

            var relativePath = Path.GetRelativePath(optionsRoot, canonicalPath);
            options.Add(new ModOption
            {
                Name = name,
                Directory = canonicalPath,
                RelativePath = Path.Combine(OptionsDirectoryName, relativePath),
            });
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
