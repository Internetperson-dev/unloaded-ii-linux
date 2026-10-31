using System.Runtime.InteropServices;

namespace ReloadedDropIn.Overlay;

/// <summary>Minimal P/Invoke bridge for Universal-Dear-ImGui-Hook (D3D12/D3D11/etc.).</summary>
public static class UniversalHookDx12
{
    public enum Backend
    {
        None = 0, D3D9 = 1, D3D10 = 2, D3D11 = 3, D3D12 = 4, Vulkan = 5, OpenGL = 6
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Config
    {
        public IntPtr module;
        public int backend; // Backend enum
        public int menuKey;
        public int unloadKey;
        public uint menuButtons;
        public float scale;
        public bool debugLog;
        public bool remoteControl;
        [MarshalAs(UnmanagedType.LPStr)] public string remotePipe;
        public IntPtr render; // function pointer
        public IntPtr setup;  // function pointer
    }

    [DllImport("Universal-ImGui-Hook.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Start(ref Config config);

    [DllImport("Universal-ImGui-Hook.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Stop();

    [DllImport("Universal-ImGui-Hook.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr BackendName(int backend);
}
