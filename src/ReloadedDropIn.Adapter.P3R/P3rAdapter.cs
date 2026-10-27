using ReloadedDropIn.Adapter.Abstractions;

namespace ReloadedDropIn.Adapter.P3R;

/// <summary>
/// Persona 3 Reload (PC, Steam). UE4 4.27 title driven by UnrealEssentials +
/// p3rpc.essentials. Like P5R, the adapter detects the game and declares the
/// required mod stack; p3rpc.essentials handles runtime redirection so no
/// on-disk archive rewrites or backups are needed.
/// </summary>
public sealed class P3rAdapter : IGameAdapter
{
    public const string ModLoaderModId = "p3rpc.essentials";

    public string Id => "p3r";
    public string DisplayName => "Persona 3 Reload";

    public IReadOnlyList<string> ExecutableNames { get; } = ["P3R.exe"];
    public IReadOnlyList<uint> SteamAppIds { get; } = [2161700u];

    public GameDetectionResult Detect(AdapterContext context)
    {
        foreach (var executable in ExecutableNames)
        {
            var path = Path.Combine(context.GameDirectory, executable);
            if (File.Exists(path))
                return GameDetectionResult.Detected(executable, path);
        }

        return GameDetectionResult.NotDetected;
    }

    public ValidationResult ValidateInstallation(AdapterContext context)
    {
        var messages = new List<ValidationMessage>();

        if (!Detect(context).IsDetected)
        {
            messages.Add(new ValidationMessage(ValidationSeverity.Error, "executable",
                $"none of [{string.Join(", ", ExecutableNames)}] found in {context.GameDirectory}"));
        }

        if (!Directory.Exists(context.ModsDirectory))
        {
            messages.Add(new ValidationMessage(ValidationSeverity.Warning, "mods",
                $"mods directory does not exist yet: {context.ModsDirectory}"));
        }

        if (!File.Exists(Path.Combine(context.GameDirectory, "P3RE.dll")))
        {
            messages.Add(new ValidationMessage(ValidationSeverity.Warning, "p3re",
                "P3R Enhanced mod is missing (P3RE.dll not found in game directory); " +
                "see https://www.patreon.com/huutaiii/posts/p3r-enhanced-fsr-159746051"));
        }

        return ValidationResult.From(messages);
    }

    public IReadOnlyList<RequiredMod> GetRequiredMods() =>
    [
        new RequiredMod
        {
            Id = ModLoaderModId, Enabled = true,
            UpdateRepo = "AnimatedSwine37/p3rpc.essentials",
            UpdateAssetPrefix = "p3rpc.essentials",
        },
        new RequiredMod
        {
            Id = "UnrealEssentials", Enabled = false,
            UpdateRepo = "AnimatedSwine37/UnrealEssentials",
            UpdateAssetPrefix = "UnrealEssentials",
        },
        new RequiredMod
        {
            Id = "UTOC.Stream.Emulator", Enabled = false,
            UpdateRepo = "AnimatedSwine37/UnrealEssentials",
            UpdateAssetPrefix = "UTOC.Stream.Emulator",
        },
        new RequiredMod
        {
            Id = "reloaded.sharedlib.hooks", Enabled = false,
            UpdateRepo = "Sewer56/Reloaded.SharedLib.Hooks.ReloadedII",
            UpdateAssetPrefix = "Reloaded.Hooks.ReloadedII",
        },
        new RequiredMod
        {
            Id = "Reloaded.Memory.SigScan.ReloadedII", Enabled = false,
            UpdateRepo = "Reloaded-Project/Reloaded.Memory.SigScan",
            UpdateAssetPrefix = "Reloaded.Memory.SigScan.ReloadedII",
        },
        new RequiredMod
        {
            Id = "p3rpc.UE4SS.Reloaded-SSAO", Enabled = false,
            UpdateRepo = "Internetperson-dev/p3rpc.UE4SS.Reloaded-SSAO",
            UpdateAssetPrefix = "p3rpc.UE4SS.Reloaded",
        },
    ];

    public InjectionConfiguration GetInjectionConfiguration(AdapterContext context) => new()
    {
        PreferredProxyDll = "winmm.dll",
        WineDllOverride = "winmm=n,b",
    };

    /// <summary>
    /// p3rpc.essentials stores merged mod outputs in a Cache directory. It is
    /// recreated automatically, so invalidate it whenever the active mod set
    /// changes rather than risk retaining output from a removed mod.
    /// </summary>
    public IReadOnlyList<string> GetDisposableModStatePaths(AdapterContext context)
    {
        var loader = new Core.Discovery.ModScanner().Scan(context.ModsDirectory).Mods
            .FirstOrDefault(m => m.ModId.Equals(ModLoaderModId, StringComparison.OrdinalIgnoreCase));
        return loader is null ? [] : [Path.Combine(loader.Directory, "Cache")];
    }

    public Task<IReadOnlyList<string>> AfterGenerateConfigurationAsync(
        AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> CreateBackupsAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> RestoreAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["p3r: no on-disk game files are modified; nothing to restore"]);

    public Task<IReadOnlyList<string>> AfterModsLoadedAsync(
        AdapterContext context, DateTime launchStartedUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
