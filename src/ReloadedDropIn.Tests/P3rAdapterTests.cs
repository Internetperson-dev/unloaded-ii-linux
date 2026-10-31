using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.P3R;
using ReloadedDropIn.Bootstrap.Update;

namespace ReloadedDropIn.Tests;

public class P3rAdapterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AdapterContext Context => new()
    {
        GameDirectory = Path.Combine(_temp.Path, "game"),
        ModsDirectory = Path.Combine(_temp.Path, "game", "mods"),
        DropInDirectory = Path.Combine(_temp.Path, "game", "reloaded-dropin"),
    };

    [Fact]
    public void DetectsP3rExecutable()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "P3R.exe"), "MZ");

        var detection = new P3rAdapter().Detect(Context);

        Assert.True(detection.IsDetected);
        Assert.Equal("P3R.exe", detection.MatchedExecutable);
    }

    [Fact]
    public void DetectsP3rExecutableInUe4BinariesLayout()
    {
        // Tools (CLI, diagnostics) may be pointed at the Steam install root
        // instead of the folder the drop-in is installed in.
        var executable = Path.Combine(
            Context.GameDirectory, "P3R", "Binaries", "Win64", "P3R.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "MZ");

        var detection = new P3rAdapter().Detect(Context);

        Assert.True(detection.IsDetected);
        Assert.Equal("P3R.exe", detection.MatchedExecutable);
        Assert.Equal(executable, detection.ExecutablePath);
    }

    [Fact]
    public void DoesNotDetectExecutableInParentDirectory()
    {
        // The drop-in is installed beside the executable, never in a folder
        // below it, so a parent lookup would only cause false positives.
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(_temp.Path, "P3R.exe"), "MZ");

        Assert.False(new P3rAdapter().Detect(Context).IsDetected);
    }

    [Fact]
    public void DoesNotDetectOtherGames()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "P5R.exe"), "MZ");

        Assert.False(new P3rAdapter().Detect(Context).IsDetected);
    }

    [Fact]
    public void EveryRequiredModHasAnInstallSource()
    {
        foreach (var mod in new P3rAdapter().GetRequiredMods())
        {
            Assert.False(string.IsNullOrWhiteSpace(mod.UpdateRepo), $"{mod.Id} has no UpdateRepo");
            Assert.False(string.IsNullOrWhiteSpace(mod.UpdateAssetPrefix), $"{mod.Id} has no UpdateAssetPrefix");
        }
    }

    [Fact]
    public void OnlyTheModLoaderIsUserEnabled()
    {
        var required = new P3rAdapter().GetRequiredMods();

        // p3rpc.essentials is the only user-facing enabled mod; its UE4
        // dependencies are enabled as required stack mods but not user toggles.
        var userEnabled = required.Where(m => m.Enabled).ToList();
        Assert.Equal(6, userEnabled.Count);
        Assert.Contains(userEnabled, m => m.Id == P3rAdapter.ModLoaderModId);
        Assert.Contains(userEnabled, m => m.Id == "UnrealEssentials");
        Assert.Contains(userEnabled, m => m.Id == "UTOC.Stream.Emulator");
        Assert.Contains(userEnabled, m => m.Id == "reloaded.sharedlib.hooks");
        Assert.Contains(userEnabled, m => m.Id == "Reloaded.Memory.SigScan.ReloadedII");
        Assert.Contains(userEnabled, m => m.Id == "reloaded.universal.fileemulationframework");

        Assert.Equal(6, required.Count);
    }

    [Fact]
    public void UsesSharedWinmmProxy()
    {
        var injection = new P3rAdapter().GetInjectionConfiguration(Context);

        Assert.Equal("winmm.dll", injection.PreferredProxyDll);
        Assert.Equal("winmm=n,b", injection.WineDllOverride);
    }

    [Fact]
    public void DeclaresEssentialsCacheAsDisposable()
    {
        var loader = _temp.CreateMod("game/mods/_base-mods/persona", P3rAdapter.ModLoaderModId);

        var paths = new P3rAdapter().GetDisposableModStatePaths(Context);

        Assert.Equal([Path.Combine(loader, "Cache")], paths);
    }

    [Fact]
    public void ValidationWarnsWhenModsDirectoryIsMissing()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "P3R.exe"), "MZ");

        var result = new P3rAdapter().ValidateInstallation(Context);

        Assert.Contains(result.Messages,
            m => m.Severity == ValidationSeverity.Warning && m.Check == "mods");
        Assert.DoesNotContain(result.Messages, m => m.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void ValidationWarnsAboutMissingP3reDll()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "P3R.exe"), "MZ");

        var result = new P3rAdapter().ValidateInstallation(Context);

        Assert.Contains(result.Messages,
            m => m.Severity == ValidationSeverity.Warning && m.Check == "p3re");
    }

    [Fact]
    public void ValidationAcceptsP3reDllBesideExecutable()
    {
        // This is the real install: the drop-in, P3R.exe and P3RE.dll all
        // share P3R/Binaries/Win64.
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "P3R.exe"), "MZ");
        File.WriteAllText(Path.Combine(Context.GameDirectory, "P3RE.dll"), "MZ");

        var result = new P3rAdapter().ValidateInstallation(Context);

        Assert.DoesNotContain(result.Messages, m => m.Check == "p3re");
    }

    [Fact]
    public void ValidationFindsP3reDllNextToUe4Executable()
    {
        var executable = Path.Combine(
            Context.GameDirectory, "P3R", "Binaries", "Win64", "P3R.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "MZ");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable)!, "P3RE.dll"), "MZ");

        var result = new P3rAdapter().ValidateInstallation(Context);

        Assert.DoesNotContain(result.Messages, m => m.Check == "p3re");
    }

    [Fact]
    public async Task RestoreReportsNothingToDo()
    {
        Directory.CreateDirectory(Context.GameDirectory);

        var log = await new P3rAdapter().RestoreAsync(Context, CancellationToken.None);

        Assert.Contains(log, line => line.Contains("nothing to restore"));
    }

    [Fact]
    public void SiblingAssetsDoNotSatisfyEachOthersPrefixes()
    {
        // These repos publish multiple mods per release; a prefix mix-up would
        // install the wrong mod under the right ModId.
        Assert.NotNull(BaseModInstaller.ParseAssetVersion("UnrealEssentials1.2.3.7z", "UnrealEssentials"));
        Assert.Null(BaseModInstaller.ParseAssetVersion("UTOC.Stream.Emulator1.2.3.7z", "UnrealEssentials"));
        Assert.NotNull(BaseModInstaller.ParseAssetVersion("UTOC.Stream.Emulator1.2.3.7z", "UTOC.Stream.Emulator"));
        Assert.Null(BaseModInstaller.ParseAssetVersion("UnrealEssentials1.2.3.7z", "UTOC.Stream.Emulator"));
    }
}