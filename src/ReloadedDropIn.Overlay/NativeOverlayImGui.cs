using System.Numerics;

namespace ReloadedDropIn.Overlay;

/// <summary>
/// <see cref="IOverlayImGui"/> implementation backed by the native
/// DropInOverlay.dll bridge. The bridge renders through uih's own Dear ImGui
/// (1.92), so this type must only be used from the native render callback.
/// </summary>
internal sealed class NativeOverlayImGui : IOverlayImGui
{
    public Vector2 DisplaySize
    {
        get
        {
            DropInOverlayNative.GetDisplaySize(out var size);
            return new Vector2(size.X, size.Y);
        }
    }

    // uih applies Config.Scale (style + DPI) before every frame; applying a
    // second font scale here would double it.
    public void SetScale(float scale) { }

    // uih drives io.MouseDrawCursor from its own menu state.
    public void SetMouseDrawCursor(bool enabled) { }

    public bool Begin(string title, ref bool open, int flags)
    {
        var value = open ? 1 : 0;
        var result = DropInOverlayNative.Begin(title, ref value, flags) != 0;
        open = value != 0;
        return result;
    }

    public void End() => DropInOverlayNative.End();
    public void Text(string text) => DropInOverlayNative.Text(text);
    public void TextColored(Vector4 color, string text) =>
        DropInOverlayNative.TextColored(color.X, color.Y, color.Z, color.W, text);
    public void TextDisabled(string text) => DropInOverlayNative.TextDisabled(text);
    public void Separator() => DropInOverlayNative.Separator();
    public bool SmallButton(string label) => DropInOverlayNative.SmallButton(label) != 0;
    public void SameLine(float offset, float spacing) => DropInOverlayNative.SameLine(offset, spacing);

    public bool Checkbox(string label, ref bool value)
    {
        var checkedValue = value ? 1 : 0;
        var result = DropInOverlayNative.Checkbox(label, ref checkedValue) != 0;
        value = checkedValue != 0;
        return result;
    }

    public bool CollapsingHeader(string label, int flags) =>
        DropInOverlayNative.CollapsingHeader(label, flags) != 0;
    public void PushId(string id) => DropInOverlayNative.PushId(id);
    public void PopId() => DropInOverlayNative.PopId();
    public void Indent(float amount) => DropInOverlayNative.Indent(amount);
    public void Unindent(float amount) => DropInOverlayNative.Unindent(amount);

    public bool InputInt(string label, ref int value) =>
        DropInOverlayNative.InputInt(label, ref value, 1, 10, 0) != 0;

    public bool InputDouble(string label, ref double value) =>
        DropInOverlayNative.InputDouble(label, ref value, 0.1, 1.0, "%g", 0) != 0;

    public bool InputText(string label, byte[] buffer) =>
        DropInOverlayNative.InputText(label, buffer, (nuint)buffer.Length, 0) != 0;

    public void SetNextWindowPos(Vector2 position, int condition, Vector2 pivot) =>
        DropInOverlayNative.SetNextWindowPos(position.X, position.Y, condition, pivot.X, pivot.Y);

    public void SetNextWindowBgAlpha(float alpha) => DropInOverlayNative.SetNextWindowBgAlpha(alpha);
}
