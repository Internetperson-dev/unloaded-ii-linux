using System.Text.Json;
using ReloadedDropIn.Core.Discovery;
using ReloadedDropIn.Core.Filesystem;

namespace ReloadedDropIn.Core.Configuration;

/// <summary>
/// Applies sub-module option states by renaming option directories.
/// Disabled options are renamed with a ".disabled" suffix so Reloaded-II
/// doesn't scan them; enabled options are restored to their original names.
///
/// Built-in/runtime options (identified by the "__builtin/" prefix) are not
/// filesystem-backed and are therefore ignored by this class. Their state is
/// handled by the game-specific runtime adapter.
/// </summary>
public sealed class OptionStateHealer
{
    private const string DisabledSuffix = ".disabled";
    private const string BuiltInOptionPrefix = "__builtin/";

    private sealed record State
    {
        public int SchemaVersion { get; init; } = 1;
        public string[] DisabledOptions { get; init; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    public IReadOnlyList<string> Reconcile(
        string modsDirectory,
        string dropInDirectory,
        IReadOnlyList<DiscoveredMod> mods,
        IReadOnlyList<string> disabledOptions)
    {
        var log = new List<string>();
        var statePath = Path.Combine(
            dropInDirectory,
            "state",
            "option-states.json");

        var previous = Load(statePath);

        var canonicalDisabled = disabledOptions
            .OrderBy(
                s => s,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Check if state changed.
        if (previous is not null &&
            previous.SchemaVersion == 1 &&
            previous.DisabledOptions.SequenceEqual(
                canonicalDisabled,
                StringComparer.Ordinal))
        {
            return log;
        }

        var failed = false;

        // Build a lookup of all known filesystem-backed options by their key
        // (ModId:RelativePath).
        //
        // Built-in/runtime options are intentionally excluded because they do
        // not correspond to directories and must not be renamed.
        var allOptions =
            new Dictionary<string, (string Directory, string Key)>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            foreach (var option in mod.Options)
            {
                // Runtime/built-in options such as:
                //
                //     __builtin/skip-intro
                //
                // are handled by the game-specific runtime layer.
                if (option.RelativePath.StartsWith(
                        BuiltInOptionPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key =
                    $"{mod.ModId}:{option.RelativePath}";

                allOptions[key] =
                    (option.Directory, key);
            }
        }

        var disabledSet = new HashSet<string>(
            disabledOptions,
            StringComparer.OrdinalIgnoreCase);

        // Process each known filesystem-backed option.
        foreach (var (key, (optionDir, _)) in allOptions)
        {
            var isDisabled =
                disabledSet.Contains(key);

            var disabledPath =
                optionDir + DisabledSuffix;

            var isCurrentlyDisabled =
                Directory.Exists(disabledPath);

            var isEnabled =
                Directory.Exists(optionDir) &&
                !isCurrentlyDisabled;

            try
            {
                if (isDisabled && isEnabled)
                {
                    // Disable: rename directory to .disabled suffix.
                    if (Directory.Exists(disabledPath))
                        Directory.Delete(
                            disabledPath,
                            recursive: true);

                    Directory.Move(
                        optionDir,
                        disabledPath);

                    log.Add(
                        $"disabled option: {Path.GetRelativePath(modsDirectory, optionDir)}");
                }
                else if (!isDisabled && isCurrentlyDisabled)
                {
                    // Enable: rename back from .disabled suffix.
                    if (Directory.Exists(optionDir))
                        Directory.Delete(
                            optionDir,
                            recursive: true);

                    Directory.Move(
                        disabledPath,
                        optionDir);

                    log.Add(
                        $"enabled option: {Path.GetRelativePath(modsDirectory, optionDir)}");
                }
            }
            catch (Exception ex) when (
                ex is IOException or
                UnauthorizedAccessException)
            {
                failed = true;

                log.Add(
                    $"could not toggle option {key}: {ex.Message}");
            }
        }

        if (failed)
        {
            log.Add(
                "option state reconciliation incomplete; it will be retried next launch");

            return log;
        }

        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(statePath)!);

            AtomicFile.WriteAllText(
                statePath,
                JsonSerializer.Serialize(
                    new State
                    {
                        DisabledOptions = canonicalDisabled
                    },
                    JsonOptions) +
                Environment.NewLine);

            log.Add(
                previous is null
                    ? "established option state baseline"
                    : "option states changed; updated baseline");
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException)
        {
            log.Add(
                $"could not record option state baseline; changes will repeat next launch: {ex.Message}");
        }

        return log;
    }

    private static State? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<State>(
                File.ReadAllText(path));
        }
        catch (Exception ex) when (
            ex is JsonException or
            IOException or
            UnauthorizedAccessException)
        {
            return null;
        }
    }
}

