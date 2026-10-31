namespace ReloadedDropIn.Core.Overlay;

/// <summary>
/// Renderer facts that decide whether the universal INSERT panel can hook a
/// game's swapchain. The panel's only backend is Direct3D 11; games that are
/// Direct3D 12 only need a separate backend (or a bridge to the game's own
/// renderer, as FFXVI does through Faith).
/// </summary>
public static class OverlayRendererPolicy
{
    /// <summary>
    /// Persona 3 Reload ships a Direct3D 12-only UE4 renderer. Its UE4 build
    /// ignores the usual -dx11/-d3d11 switches, so the D3D11 swapchain hook
    /// cannot render into its swapchain and must not be installed.
    /// </summary>
    private static readonly string[] Direct3D12OnlyExecutables =
    [
        "p3r.exe",
    ];

    public static bool IsDirect3D12Only(string executableName) =>
        Direct3D12OnlyExecutables.Contains(executableName, StringComparer.OrdinalIgnoreCase);
}
