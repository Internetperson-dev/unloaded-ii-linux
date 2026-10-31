// C ABI bridge between the managed Reloaded Drop-In overlay and
// Universal-Dear-ImGui-Hook (uih).
//
// uih renders through its own Dear ImGui version and swapchain hook. The
// managed overlay cannot share that ImGui context through DearImguiSharp
// (different ImGui versions, different ABI), so the panel is drawn by calling
// the small C wrappers below. Every wrapper runs on the render thread, inside
// uih's frame, because that is where ImGui::NewFrame()/Render() are paired.
//
// The native `render` callback is a managed delegate: reverse P/Invoke from
// uih's worker thread back into the overlay assembly.

#include "uih.h"

#include "imgui.h"

#include <cstddef>

struct DropInOverlayConfig {
    int backend;
    int menuKey;
    int unloadKey;
    unsigned menuButtons;
    float scale;
    int debugLog;
    int remoteControl;
    const char* remotePipe;
    void (*render)();
    void (*setup)();
};

struct DropInImVec2 {
    float x;
    float y;
};

extern "C" {

__declspec(dllexport) void DropInOverlay_Start(const DropInOverlayConfig* config) {
    uih::Config native;
    if (config) {
        native.backend = static_cast<uih::Backend>(config->backend);
        native.menuKey = config->menuKey;
        native.unloadKey = config->unloadKey;
        native.menuButtons = config->menuButtons;
        native.scale = config->scale;
        native.debugLog = config->debugLog != 0;
        native.remoteControl = config->remoteControl != 0;
        if (config->remotePipe)
            native.remotePipe = config->remotePipe;
        native.render = config->render;
        native.setup = config->setup;
    }
    // Never let uih free the bridge DLL on the unload key: the managed overlay
    // still owns it. With a null module, the unload key just hides the panel
    // and the menu key brings it back.
    native.module = nullptr;
    uih::Start(native);
}

__declspec(dllexport) void DropInOverlay_Stop() { uih::Stop(); }

__declspec(dllexport) int DropInOverlay_IsMenuOpen() { return uih::IsMenuOpen() ? 1 : 0; }

__declspec(dllexport) void DropInOverlay_SetMenuOpen(int open) {
    uih::SetMenuOpen(open != 0);
}

__declspec(dllexport) int DropInOverlay_ActiveBackend() {
    return static_cast<int>(uih::ActiveBackend());
}

__declspec(dllexport) const char* DropInOverlay_BackendName(int backend) {
    return uih::BackendName(static_cast<uih::Backend>(backend));
}

// --- Dear ImGui surface used by OverlayUi ---------------------------------

__declspec(dllexport) void DropInImGui_GetDisplaySize(DropInImVec2* size) {
    if (!size)
        return;
    const ImVec2 display = ImGui::GetIO().DisplaySize;
    size->x = display.x;
    size->y = display.y;
}

__declspec(dllexport) void DropInImGui_SetMouseDrawCursor(int enabled) {
    ImGui::GetIO().MouseDrawCursor = enabled != 0;
}

__declspec(dllexport) int DropInImGui_Begin(const char* name, int* open, int flags) {
    bool value = open && *open != 0;
    const bool result = ImGui::Begin(name, open ? &value : nullptr, static_cast<ImGuiWindowFlags>(flags));
    if (open)
        *open = value ? 1 : 0;
    return result ? 1 : 0;
}

__declspec(dllexport) void DropInImGui_End() { ImGui::End(); }

__declspec(dllexport) void DropInImGui_Text(const char* text) {
    ImGui::TextUnformatted(text ? text : "");
}

__declspec(dllexport) void DropInImGui_TextColored(float r, float g, float b, float a, const char* text) {
    ImGui::TextColored(ImVec4(r, g, b, a), "%s", text ? text : "");
}

__declspec(dllexport) void DropInImGui_TextDisabled(const char* text) {
    ImGui::TextDisabled("%s", text ? text : "");
}

__declspec(dllexport) void DropInImGui_Separator() { ImGui::Separator(); }

__declspec(dllexport) int DropInImGui_SmallButton(const char* label) {
    return ImGui::SmallButton(label ? label : "") ? 1 : 0;
}

__declspec(dllexport) void DropInImGui_SameLine(float offset, float spacing) {
    ImGui::SameLine(offset, spacing);
}

__declspec(dllexport) int DropInImGui_Checkbox(const char* label, int* value) {
    bool checked = value && *value != 0;
    const bool result = ImGui::Checkbox(label ? label : "", &checked);
    if (value)
        *value = checked ? 1 : 0;
    return result ? 1 : 0;
}

__declspec(dllexport) int DropInImGui_CollapsingHeader(const char* label, int flags) {
    return ImGui::CollapsingHeader(label ? label : "", static_cast<ImGuiTreeNodeFlags>(flags)) ? 1 : 0;
}

__declspec(dllexport) void DropInImGui_PushId(const char* id) { ImGui::PushID(id ? id : ""); }

__declspec(dllexport) void DropInImGui_PopId() { ImGui::PopID(); }

__declspec(dllexport) void DropInImGui_Indent(float amount) { ImGui::Indent(amount); }

__declspec(dllexport) void DropInImGui_Unindent(float amount) { ImGui::Unindent(amount); }

__declspec(dllexport) int DropInImGui_InputInt(const char* label, int* value, int step, int step_fast, int flags) {
    if (!value)
        return 0;
    return ImGui::InputInt(label ? label : "", value, step, step_fast,
                           static_cast<ImGuiInputTextFlags>(flags))
        ? 1
        : 0;
}

__declspec(dllexport) int DropInImGui_InputDouble(const char* label, double* value, double step,
                                                 double step_fast, const char* format, int flags) {
    if (!value)
        return 0;
    return ImGui::InputDouble(label ? label : "", value, step, step_fast, format ? format : "%g",
                              static_cast<ImGuiInputTextFlags>(flags))
        ? 1
        : 0;
}

__declspec(dllexport) int DropInImGui_InputText(const char* label, char* buffer, size_t buffer_size, int flags) {
    if (!buffer || buffer_size == 0)
        return 0;
    return ImGui::InputText(label ? label : "", buffer, buffer_size,
                            static_cast<ImGuiInputTextFlags>(flags))
        ? 1
        : 0;
}

__declspec(dllexport) void DropInImGui_SetNextWindowPos(float x, float y, int condition, float pivot_x,
                                                        float pivot_y) {
    ImGui::SetNextWindowPos(ImVec2(x, y), static_cast<ImGuiCond>(condition), ImVec2(pivot_x, pivot_y));
}

__declspec(dllexport) void DropInImGui_SetNextWindowBgAlpha(float alpha) {
    ImGui::SetNextWindowBgAlpha(alpha);
}

} // extern "C"
