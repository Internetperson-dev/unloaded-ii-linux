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

    [Fact]
    public void DisabledOptionDirectoriesAreNormalized()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/ModWithDisabled", "mod.disabled.opts");
        var optionsDir = Path.Combine(modDir, "Options");

        // One enabled option, one disabled (.disabled suffix).
        Directory.CreateDirectory(Path.Combine(optionsDir, "EnabledThing"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "DisabledThing.disabled"));

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        Assert.Equal(2, mod.Options.Count);
        // Both should appear with their original name (no .disabled suffix).
        Assert.Contains(mod.Options, o => o.Name == "EnabledThing");
        Assert.Contains(mod.Options, o => o.Name == "DisabledThing");
        // RelativePath should also use the canonical name.
        Assert.Contains(mod.Options, o => o.RelativePath == "Options/DisabledThing");
        // The Directory should point to the canonical (non-.disabled) path.
        var disabled = mod.Options.Single(o => o.Name == "DisabledThing");
        Assert.EndsWith("DisabledThing", disabled.Directory);
        Assert.DoesNotContain(".disabled", disabled.Directory);
    }

    [Fact]
    public void ContentSubModulesDetectedAsOptions()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/TexturePackMod", "texture.pack.mod");

        // Create content subdirectories (no ModConfig.json, no DLLs).
        Directory.CreateDirectory(Path.Combine(modDir, "HDTextures"));
        File.WriteAllText(Path.Combine(modDir, "HDTextures", "texture.dds"), "fake");
        Directory.CreateDirectory(Path.Combine(modDir, "BetterUI"));
        File.WriteAllText(Path.Combine(modDir, "BetterUI", "ui.png"), "fake");

        // Well-known dirs should be excluded.
        Directory.CreateDirectory(Path.Combine(modDir, "Options"));
        Directory.CreateDirectory(Path.Combine(modDir, "_common"));
        Directory.CreateDirectory(Path.Combine(modDir, "Cache"));

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        Assert.Equal(2, mod.Options.Count);
        Assert.Contains(mod.Options, o => o.Name == "BetterUI");
        Assert.Contains(mod.Options, o => o.Name == "HDTextures");
    }

    [Fact]
    public void DisabledContentSubModulesNormalized()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/TexturePackMod", "texture.pack.mod");

        // Content sub-module with .disabled suffix (renamed by OptionStateHealer).
        Directory.CreateDirectory(Path.Combine(modDir, "HDTextures.disabled"));

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        var option = Assert.Single(mod.Options);
        Assert.Equal("HDTextures", option.Name);
        Assert.Equal("HDTextures", option.RelativePath);
        Assert.DoesNotContain(".disabled", option.Directory);
    }

    [Fact]
    public void DiscoversNestedOptionDirectories()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/NestedOptions", "mod.nested.options");

        // Mirror the ps4reverts layout: two-level options tree.
        var optionsDir = Path.Combine(modDir, "Options");
        Directory.CreateDirectory(Path.Combine(optionsDir, "Censorship", "Almighty Skill Icon"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "Censorship", "Ryuji Shoes"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "Epilepsy", "All-Out-Attack Animation"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "Others", "One-way Airlock Color"));

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        // All four leaves should appear; no grouping folders (Censorship etc.).
        Assert.Equal(4, mod.Options.Count);
        Assert.DoesNotContain(mod.Options, o => o.Name == "Censorship");
        Assert.DoesNotContain(mod.Options, o => o.Name == "Epilepsy");

        var almighty = mod.Options.Single(o => o.Name == "Almighty Skill Icon");
        Assert.Equal("Options/Censorship/Almighty Skill Icon", almighty.RelativePath);
        Assert.EndsWith(Path.Combine("Censorship", "Almighty Skill Icon"), almighty.Directory);

        var ryuji = mod.Options.Single(o => o.Name == "Ryuji Shoes");
        Assert.Equal("Options/Censorship/Ryuji Shoes", ryuji.RelativePath);

        var other = mod.Options.Single(o => o.Name == "One-way Airlock Color");
        Assert.Equal("Options/Others/One-way Airlock Color", other.RelativePath);
    }

    [Fact]
    public void NestedDisabledOptionDirectoriesAreNormalized()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/NestedOptions", "mod.nested.options");

        var optionsDir = Path.Combine(modDir, "Options");
        Directory.CreateDirectory(Path.Combine(optionsDir, "Censorship"));
        // Leaf disabled by OptionStateHealer rename.
        Directory.CreateDirectory(Path.Combine(optionsDir, "Censorship", "Ryuji Shoes.disabled"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "Censorship", "Almighty Skill Icon"));

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        Assert.Equal(2, mod.Options.Count);
        var disabled = mod.Options.Single(o => o.Name == "Ryuji Shoes");
        Assert.Equal("Options/Censorship/Ryuji Shoes", disabled.RelativePath);
        Assert.DoesNotContain(".disabled", disabled.Directory);
    }

    [Fact]
    public void DisabledGroupingFolderStillExposesLeaves()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/NestedOptions", "mod.nested.options");

        var optionsDir = Path.Combine(modDir, "Options");
        // Whole category disabled by a previous launch (renamed with .disabled).
        Directory.CreateDirectory(Path.Combine(optionsDir, "Censorship.disabled", "Ryuji Shoes"));

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        // The leaf is still discoverable under its canonical path so the user
        // can re-enable it individually.
        var leaf = Assert.Single(mod.Options);
        Assert.Equal("Ryuji Shoes", leaf.Name);
        Assert.Equal("Options/Censorship/Ryuji Shoes", leaf.RelativePath);
        Assert.DoesNotContain(".disabled", leaf.Directory);
    }
}
