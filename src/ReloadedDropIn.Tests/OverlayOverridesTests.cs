using ReloadedDropIn.Core.Configuration;

namespace ReloadedDropIn.Tests;

public class OverlayOverridesTests
{
    [Fact]
    public void WatermarkIsHiddenByDefaultSoIdleFramesCostNothing()
    {
        Assert.True(new OverlayOverrides().HideWatermark);
    }

    [Fact]
    public void MissingWatermarkSettingInExistingConfigStaysHidden()
    {
        using var temp = new TempDirectory();
        var directory = System.IO.Path.Combine(temp.Path, "reloaded-dropin", "overlay");
        Directory.CreateDirectory(directory);
        File.WriteAllText(System.IO.Path.Combine(directory, "overrides.json"), "{}");

        Assert.True(OverlayOverrides.Load(System.IO.Path.Combine(temp.Path, "reloaded-dropin")).HideWatermark);
    }

    [Fact]
    public void PinnedWatermarkIsRespected()
    {
        using var temp = new TempDirectory();
        var directory = System.IO.Path.Combine(temp.Path, "reloaded-dropin", "overlay");
        Directory.CreateDirectory(directory);
        File.WriteAllText(System.IO.Path.Combine(directory, "overrides.json"),
            "{\"HideWatermark\": false}");

        Assert.False(OverlayOverrides.Load(System.IO.Path.Combine(temp.Path, "reloaded-dropin")).HideWatermark);
    }
}
