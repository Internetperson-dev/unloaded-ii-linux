using ReloadedDropIn.Core.Discovery;

namespace ReloadedDropIn.Tests;

public class ModScannerTests
{
    [Fact]
    public void DiscoversModsAtTopLevelAndNested()
    {
        using var temp = new TempDirectory();
        temp.CreateMod("mods/ValidMod", "valid.mod");
        temp.CreateMod("mods/ExtraFolder/AnotherValidMod", "another.valid.mod");

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));

        Assert.Equal(["another.valid.mod", "valid.mod"], result.Mods.Select(m => m.ModId));
    }

    [Fact]
    public void MissingModsDirectoryYieldsEmptyResult()
    {
        var result = new ModScanner().Scan("/nonexistent/mods");

        Assert.Empty(result.Mods);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void InvalidManifestIsReportedNotThrown()
    {
        using var temp = new TempDirectory();
        var modDirectory = Path.Combine(temp.Path, "mods/Broken");
        Directory.CreateDirectory(modDirectory);
        File.WriteAllText(Path.Combine(modDirectory, "ModConfig.json"), "{ not json !");

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));

        Assert.Empty(result.Mods);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(ScanIssueKind.InvalidManifest, issue.Kind);
    }

    [Fact]
    public void ManifestWithoutModIdIsRejected()
    {
        using var temp = new TempDirectory();
        var modDirectory = Path.Combine(temp.Path, "mods/NoId");
        Directory.CreateDirectory(modDirectory);
        File.WriteAllText(Path.Combine(modDirectory, "ModConfig.json"), """{ "ModName": "nameless" }""");

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));

        Assert.Empty(result.Mods);
        Assert.Equal(ScanIssueKind.InvalidManifest, Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void DuplicateModIdKeepsLexicographicallyFirstDirectory()
    {
        using var temp = new TempDirectory();
        temp.CreateMod("mods/BBB", "same.id");
        temp.CreateMod("mods/AAA", "same.id");

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));

        var mod = Assert.Single(result.Mods);
        Assert.EndsWith("AAA", mod.Directory);
        var issue = Assert.Single(result.Issues, i => i.Kind == ScanIssueKind.DuplicateModId);
        Assert.EndsWith("BBB", issue.Path);
    }

    [Fact]
    public void LooseFilesAreReportedAsIgnored()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "mods"));
        File.WriteAllText(Path.Combine(temp.Path, "mods/random-readme.txt"), "hello");

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));

        var issue = Assert.Single(result.Issues);
        Assert.Equal(ScanIssueKind.IgnoredEntry, issue.Kind);
        Assert.Contains("random-readme.txt", issue.Path);
    }

    [Fact]
    public void PutModsHerePlaceholderIsNotReported()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "mods"));
        File.WriteAllText(Path.Combine(temp.Path, "mods/PUT_MODS_HERE.txt"), "");

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void DoesScanInsideAManifestRoot()
    {
        using var temp = new TempDirectory();
        temp.CreateMod("mods/Outer", "outer.mod");
        temp.CreateMod("mods/Outer/nested", "nested.mod");

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));

        Assert.Equal(["nested.mod", "outer.mod"], result.Mods.Select(m => m.ModId));
    }

    [Fact]
    public void DepthLimitIsRespected()
    {
        using var temp = new TempDirectory();
        temp.CreateMod("mods/a/b/c/d/TooDeep", "too.deep");

        var result = new ModScanner { MaxDepth = 3 }.Scan(Path.Combine(temp.Path, "mods"));

        Assert.Empty(result.Mods);
    }

    [Fact]
    public void DiscoversModOptionsFromOptionsDirectory()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/ModWithOptions", "mod.with.options");

        // Create Options/ directory with subdirectories.
        var optionsDir = Path.Combine(modDir, "Options");
        Directory.CreateDirectory(Path.Combine(optionsDir, "Censorship"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "Almighty Skill Icon"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "Others"));

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));

        var mod = Assert.Single(result.Mods);
        Assert.Equal(3, mod.Options.Count);
        Assert.Equal("Almighty Skill Icon", mod.Options[0].Name);
        Assert.Equal("Censorship", mod.Options[1].Name);
        Assert.Equal("Others", mod.Options[2].Name);
    }

    [Fact]
    public void ModWithoutOptionsDirectoryHasEmptyOptions()
    {
        using var temp = new TempDirectory();
        temp.CreateMod("mods/ModWithoutOptions", "mod.no.options");

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));

        var mod = Assert.Single(result.Mods);
        Assert.Empty(mod.Options);
    }

    [Fact]
    public void EmptyOptionsDirectoryYieldsEmptyOptions()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/ModEmptyOptions", "mod.empty.options");
        Directory.CreateDirectory(Path.Combine(modDir, "Options"));

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));

        var mod = Assert.Single(result.Mods);
        Assert.Empty(mod.Options);
    }

    [Fact]
    public void ModWithSubModulesAndOptions()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/ModWithSubModules", "mod.sub.modules");

        // Create Options/ directory with subdirectories.
        var optionsDir = Path.Combine(modDir, "Options");
        Directory.CreateDirectory(Path.Combine(optionsDir, "OptionA"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "OptionB"));

        // Create another sub-module with its own manifest (should NOT be scanned as an option).
        var subModDir = Path.Combine(modDir, "SubModule");
        temp.CreateMod("mods/ModWithSubModules/SubModule", "sub.module");

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));

        // Should only find the parent mod (not the sub-module inside it).
        var mod = Assert.Single(result.Mods, m => m.ModId == "mod.sub.modules");
        Assert.Equal(2, mod.Options.Count);
        Assert.Equal("OptionA", mod.Options[0].Name);
        Assert.Equal("OptionB", mod.Options[1].Name);
    }
}
