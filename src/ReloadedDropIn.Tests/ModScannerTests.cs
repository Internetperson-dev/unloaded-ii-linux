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
    public void DoesNotExposeGroupChildrenWithoutMetadata()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/NestedOptions", "mod.nested.options");

        // Two-level structure but no release manifest: only the direct children
        // of Options/ are treated as options, exactly like v0.6.1.
        Directory.CreateDirectory(Path.Combine(modDir, "Options", "Censorship", "Ryuji Shoes"));

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        var option = Assert.Single(mod.Options);
        Assert.Equal("Censorship", option.Name);
        Assert.Equal("Options/Censorship", option.RelativePath);
    }

    [Fact]
    public void UpdateMetadataExposesTwoLevelOptionsNotContentFolders()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/NestedOptions", "mod.nested.options");

        // Mirror the ps4reverts layout: Options/<category>/<option>/<content...>.
        // Content folders (BASE.CPK, FONT, ...) must NOT surfaced as options.
        var optionsDir = Path.Combine(modDir, "Options");
        Directory.CreateDirectory(Path.Combine(optionsDir, "Censorship", "Almighty Skill Icon", "BASE.CPK", "FONT", "ICON.DDS"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "Censorship", "Ryuji Shoes", "BASE.CPK"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "Epilepsy", "All-Out-Attack Animation", "BASE.CPK"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "Others", "One-way Airlock Color", "BASE.CPK"));

        File.WriteAllText(Path.Combine(modDir, "Sewer56.Update.Metadata.json"), """
        {
          "ExtraData": null, "Type": 0, "Version": "1.5.0",
          "Hashes": {
            "Files": [
              { "RelativePath": "ModConfig.json", "Hash": 1 },
              { "RelativePath": "Options\\Censorship\\Almighty Skill Icon\\BASE.CPK\\FONT\\ICON.DDS", "Hash": 2 },
              { "RelativePath": "Options\\Censorship\\Ryuji Shoes\\BASE.CPK", "Hash": 3 },
              { "RelativePath": "Options\\Epilepsy\\All-Out-Attack Animation\\BASE.CPK", "Hash": 4 },
              { "RelativePath": "Options\\Others\\One-way Airlock Color\\BASE.CPK", "Hash": 5 }
            ]
          },
          "IgnoreRegexes": [], "IncludeRegexes": [], "DeltaData": null
        }
        """);

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        // The four option folders, never the grouping folders or content dirs.
        Assert.Equal(4, mod.Options.Count);
        Assert.DoesNotContain(mod.Options, o => o.Name is "Censorship" or "Epilepsy" or "Others");
        Assert.DoesNotContain(mod.Options, o => o.Name is "BASE.CPK" or "FONT" or "ICON.DDS");

        var almighty = mod.Options.Single(o => o.Name == "Almighty Skill Icon");
        Assert.Equal("Options/Censorship/Almighty Skill Icon", almighty.RelativePath);
        Assert.EndsWith(Path.Combine("Censorship", "Almighty Skill Icon"), almighty.Directory);

        var ryuji = mod.Options.Single(o => o.Name == "Ryuji Shoes");
        Assert.Equal("Options/Censorship/Ryuji Shoes", ryuji.RelativePath);

        var other = mod.Options.Single(o => o.Name == "One-way Airlock Color");
        Assert.Equal("Options/Others/One-way Airlock Color", other.RelativePath);
    }

    [Fact]
    public void UpdateMetadataDropsFoldersOutsideDeclaredOptions()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/MetaMod", "meta.options.mod");

        Directory.CreateDirectory(Path.Combine(modDir, "Options", "Category", "Existing Option", "BASE.CPK"));
        // Stray folder under Options/ that is not declared as an option.
        Directory.CreateDirectory(Path.Combine(modDir, "Options", "Stray", "one"));

        File.WriteAllText(Path.Combine(modDir, "Sewer56.Update.Metadata.json"), """
        {
          "ExtraData": null, "Type": 0, "Version": "1.5.0",
          "Hashes": {
            "Files": [
              { "RelativePath": "ModConfig.json", "Hash": 1 },
              { "RelativePath": "Options\\Category\\Existing Option\\BASE.CPK", "Hash": 2 }
            ]
          },
          "IgnoreRegexes": [], "IncludeRegexes": [], "DeltaData": null
        }
        """);

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        var option = Assert.Single(mod.Options);
        Assert.Equal("Existing Option", option.Name);
        Assert.Equal("Options/Category/Existing Option", option.RelativePath);
    }

    [Fact]
    public void UpdateMetadataNestedDisabledOptionsAreNormalized()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/NestedOptions", "mod.nested.options");

        var optionsDir = Path.Combine(modDir, "Options");
        // Option disabled by OptionStateHealer rename, inside an enabled group.
        Directory.CreateDirectory(Path.Combine(optionsDir, "Censorship", "Ryuji Shoes.disabled", "BASE.CPK"));
        Directory.CreateDirectory(Path.Combine(optionsDir, "Censorship", "Almighty Skill Icon", "BASE.CPK"));

        File.WriteAllText(Path.Combine(modDir, "Sewer56.Update.Metadata.json"), """
        {
          "Type": 0, "Version": "1.5.0",
          "Hashes": {
            "Files": [
              { "RelativePath": "Options\\Censorship\\Ryuji Shoes\\BASE.CPK", "Hash": 1 },
              { "RelativePath": "Options\\Censorship\\Almighty Skill Icon\\BASE.CPK", "Hash": 2 }
            ]
          },
          "IgnoreRegexes": [], "IncludeRegexes": [], "DeltaData": null
        }
        """);

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        Assert.Equal(2, mod.Options.Count);
        var disabled = mod.Options.Single(o => o.Name == "Ryuji Shoes");
        Assert.Equal("Options/Censorship/Ryuji Shoes", disabled.RelativePath);
        Assert.DoesNotContain(".disabled", disabled.Directory);
    }

    [Fact]
    public void UpdateMetadataDisabledGroupingFolderStillExposesLeaves()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/NestedOptions", "mod.nested.options");

        // Whole category disabled by a previous launch (renamed with .disabled).
        Directory.CreateDirectory(Path.Combine(modDir, "Options", "Censorship.disabled", "Ryuji Shoes", "BASE.CPK"));

        File.WriteAllText(Path.Combine(modDir, "Sewer56.Update.Metadata.json"), """
        {
          "Type": 0, "Version": "1.5.0",
          "Hashes": {
            "Files": [
              { "RelativePath": "Options\\Censorship\\Ryuji Shoes\\BASE.CPK", "Hash": 1 }
            ]
          },
          "IgnoreRegexes": [], "IncludeRegexes": [], "DeltaData": null
        }
        """);

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        // The leaf is still discoverable under its canonical path so the user
        // can re-enable it individually.
        var leaf = Assert.Single(mod.Options);
        Assert.Equal("Ryuji Shoes", leaf.Name);
        Assert.Equal("Options/Censorship/Ryuji Shoes", leaf.RelativePath);
        Assert.DoesNotContain(".disabled", leaf.Directory);
    }

    [Fact]
    public void UpdateMetadataOnlyShowsOptionsPresentOnDisk()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/MetaMod", "meta.options.mod");

        Directory.CreateDirectory(Path.Combine(modDir, "Options", "Category", "Existing Option", "BASE.CPK"));

        // Manifest also declares options that were never installed: they must not
        // be fabricated as readable options.
        File.WriteAllText(Path.Combine(modDir, "Sewer56.Update.Metadata.json"), """
        {
          "ExtraData": null, "Type": 0, "Version": "1.5.0",
          "Hashes": {
            "Files": [
              { "RelativePath": "ModConfig.json", "Hash": 1 },
              { "RelativePath": "Options\\Category\\Existing Option\\BASE.CPK\\a.GMD", "Hash": 2 },
              { "RelativePath": "Options\\Category\\Missing Option A\\BASE.CPK\\b.GMD", "Hash": 3 },
              { "RelativePath": "Options\\Category\\Missing Option B\\BASE.CPK\\c.GMD", "Hash": 4 }
            ]
          },
          "IgnoreRegexes": [], "IncludeRegexes": [], "DeltaData": null
        }
        """);

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        var option = Assert.Single(mod.Options);
        Assert.Equal("Existing Option", option.Name);
        Assert.Equal("Options/Category/Existing Option", option.RelativePath);
    }

    [Fact]
    public void UpdateMetadataOnlyShowsPresentSingleLevelOptions()
    {
        using var temp = new TempDirectory();
        var modDir = temp.CreateMod("mods/MetaModSingle", "meta.single.options.mod");

        Directory.CreateDirectory(Path.Combine(modDir, "Options", "Existing One"));

        File.WriteAllText(Path.Combine(modDir, "Sewer56.Update.Metadata.json"), """
        {
          "ExtraData": null, "Type": 0, "Version": "1.0.0",
          "Hashes": {
            "Files": [
              { "RelativePath": "Options\\Existing One\\x.dll", "Hash": 1 },
              { "RelativePath": "Options\\Missing One\\y.dll", "Hash": 2 }
            ]
          },
          "IgnoreRegexes": [], "IncludeRegexes": [], "DeltaData": null
        }
        """);

        var result = new ModScanner().Scan(Path.Combine(temp.Path, "mods"));
        var mod = Assert.Single(result.Mods);

        var option = Assert.Single(mod.Options);
        Assert.Equal("Existing One", option.Name);
        Assert.Equal("Options/Existing One", option.RelativePath);
    }
}
