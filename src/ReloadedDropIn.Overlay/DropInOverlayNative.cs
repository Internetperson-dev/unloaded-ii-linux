using System.Reflection;
using System.Runtime.InteropServices;

namespace ReloadedDropIn.Overlay;

/// <summary>
/// P/Invoke bridge to DropInOverlay.dll, our C ABI wrapper around the vendored
/// Universal-Dear-ImGui-Hook (uih) D3D12/D3D11 renderer.
///
/// The bridge lives next to this assembly (the overlay mod folder), not next to
/// the game executable, so a DllImport resolver maps the bare library name to
/// the assembly directory.
/// </summary>
internal static class DropInOverlayNative
{
    private const string LibraryName = "DropInOverlay.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void RenderCallback();

    [StructLayout(LayoutKind.Sequential)]
    public struct Config
    {
        public int Backend;
        public int MenuKey;
        public int UnloadKey;
        public uint MenuButtons;
        public float Scale;
        public int DebugLog;
        public int RemoteControl;
        [MarshalAs(UnmanagedType.LPStr)] public string? RemotePipe;
        public IntPtr Render;
        public IntPtr Setup;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec2
    {
        public float X;
        public float Y;
    }

    static DropInOverlayNative()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(DropInOverlayNative).Assembly,
            static (name, assembly, _) =>
            {
                if (!string.Equals(name, LibraryName, StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                var assemblyDirectory = Path.GetDirectoryName(assembly.Location);
                if (!string.IsNullOrEmpty(assemblyDirectory))
                {
                    var besideAssembly = Path.Combine(assemblyDirectory, LibraryName);
                    if (File.Exists(besideAssembly))
                        return NativeLibrary.Load(besideAssembly);
                }

                return IntPtr.Zero;
            });
    }

    public static bool IsAvailable
    {
        get
        {
            try
            {
                var assemblyDirectory = Path.GetDirectoryName(typeof(DropInOverlayNative).Assembly.Location);
                return !string.IsNullOrEmpty(assemblyDirectory)
                       && File.Exists(Path.Combine(assemblyDirectory, LibraryName));
            }
            catch
            {
                return false;
            }
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void DropInOverlay_Start(ref Config config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void DropInOverlay_Stop();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int DropInOverlay_IsMenuOpen();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void DropInOverlay_SetMenuOpen(int open);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int DropInOverlay_ActiveBackend();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr DropInOverlay_BackendName(int backend);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_GetDisplaySize")]
    public static extern void GetDisplaySize(out Vec2 size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_SetMouseDrawCursor")]
    public static extern void SetMouseDrawCursor(int enabled);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_Begin")]
    public static extern int Begin(string name, ref int open, int flags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_End")]
    public static extern void End();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_Text")]
    public static extern void Text(string text);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_TextColored")]
    public static extern void TextColored(float r, float g, float b, float a, string text);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_TextDisabled")]
    public static extern void TextDisabled(string text);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_Separator")]
    public static extern void Separator();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_SmallButton")]
    public static extern int SmallButton(string label);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_SameLine")]
    public static extern void SameLine(float offset, float spacing);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_Checkbox")]
    public static extern int Checkbox(string label, ref int value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_CollapsingHeader")]
    public static extern int CollapsingHeader(string label, int flags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_PushId")]
    public static extern void PushId(string id);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_PopId")]
    public static extern void PopId();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_Indent")]
    public static extern void Indent(float amount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_Unindent")]
    public static extern void Unindent(float amount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_InputInt")]
    public static extern int InputInt(string label, ref int value, int step, int stepFast, int flags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_InputDouble")]
    public static extern int InputDouble(
        string label, ref double value, double step, double stepFast, string format, int flags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_InputText")]
    public static extern int InputText(string label, [In, Out] byte[] buffer, nuint bufferSize, int flags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_SetNextWindowPos")]
    public static extern void SetNextWindowPos(float x, float y, int condition, float pivotX, float pivotY);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DropInImGui_SetNextWindowBgAlpha")]
    public static extern void SetNextWindowBgAlpha(float alpha);
}
