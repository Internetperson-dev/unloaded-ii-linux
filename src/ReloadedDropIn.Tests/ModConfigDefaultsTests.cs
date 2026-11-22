using System.Text.Json.Nodes;
using ReloadedDropIn.Core.Configuration;

namespace ReloadedDropIn.Tests;

public class ModConfigDefaultsTests
{
    [Fact]
    public void FpsUnlockerDefaultsTo240Fps()
    {
        var config = KnownModConfigDefaults.CreateFpsUnlockerConfig();

        Assert.Equal(240, config["MaxFps"]!.GetValue<double>());
        Assert.False(config["TieGameSpeedToFps"]!.GetValue<bool>());
        Assert.False(config["DebugEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void FpsUnlockerMissingSettingsAreFilledWithoutOverwritingUserValues()
    {
        var config = new JsonObject { ["MaxFps"] = 120.0 };

        Assert.True(KnownModConfigDefaults.FillFpsUnlockerMissingSettings(config));
        Assert.Equal(120, config["MaxFps"]!.GetValue<double>());
        Assert.False(config["TieGameSpeedToFps"]!.GetValue<bool>());
        Assert.False(config["DebugEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void CompleteFpsUnlockerConfigIsNotChanged()
    {
        var config = KnownModConfigDefaults.CreateFpsUnlockerConfig();

        Assert.False(KnownModConfigDefaults.FillFpsUnlockerMissingSettings(config));
    }
}
