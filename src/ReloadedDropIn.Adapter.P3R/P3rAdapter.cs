using ReloadedDropIn.Adapter.Abstractions;

namespace ReloadedDropIn.Adapter.P3R;

/// <summary>
/// Persona 3 Reload (PC, Steam). UE4 4.27 title driven by UnrealEssentials +
/// p3rpc.essentials. Like P5R, detection + required-mod naming is all we do.
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
    ];

    public InjectionConfiguration GetInjectionConfiguration(AdapterContext context) => new()
    {
        PreferredProxyDll = "winmm.dll",
        WineDllOverride = "winmm=n,b",
    };

    public IReadOnlyList<string> GetDisposableModStatePaths(AdapterContext context) => [];

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
