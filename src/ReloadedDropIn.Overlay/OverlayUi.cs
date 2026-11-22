using System.Runtime.InteropServices;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace ReloadedDropIn.Overlay;

/// <summary>
/// The Dear ImGui overlay window. Rendered every frame by the swapchain hook;
/// INSERT toggles visibility. All changes are saved to disk immediately and
/// applied by the drop-in's sync on the next game launch.
///
/// The panel is completely idle while closed: <see cref="ShouldRenderFrame"/>
/// is a cheap input poll that tells the Present hook to forward the frame
/// untouched when there is nothing to draw. Mod discovery (including DLL
/// metadata reads) runs on a background thread, never on the render thread.
/// </summary>
public sealed class OverlayUi
{
    private const int VK_INSERT = 0x2D;

    /// <summary>
    /// The corner badge appears briefly at launch as proof the loader is
    /// active, then disappears unless the user pins it from the panel.
    /// </summary>
    private const long WatermarkShowMilliseconds = 10_000;

    private readonly string _gameDirectory;
    private readonly IOverlayImGui _imgui;
    private readonly Action<string> _log;
    private readonly Action<bool>? _setCursorHooksEnabled;
    private readonly long _watermarkDeadline = Environment.TickCount64 + WatermarkShowMilliseconds;

    private ModCatalog _catalog;
    private volatile bool _catalogReady;
    private int _reloadRunning;
    private int _reloadPending;

    private bool _visible;
    private bool _scaleApplied;
    private volatile bool _togglePressed;
    private volatile bool _suspended;
    private int _keyPollerStarted;
    private bool _cursorHooksActive;
    private bool _cursorHookFaulted;
    private bool _pendingRelaunchChanges;
    private string? _lastSaveError;

    /// <summary>
    /// When set, the renderer owns the toggle key and this supplies its current
    /// menu state (the native uih D3D12 path). Null means OverlayUi polls INSERT
    /// itself, which is what the Reloaded.Imgui.Hook D3D11 path uses.
    /// </summary>
    public Func<bool>? MenuOpenSource { get; init; }

    // Self-update download state (touched from the render thread + bg thread).
    private volatile int _downloadState; // 0=idle, 1=downloading, 2=done, 3=error
    private string _downloadStatus = "";

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr pszPath);

    private static readonly Guid FolderDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public OverlayUi(
        string gameDirectory,
        IOverlayImGui imgui,
        Action<string> log,
        bool handleToggleKey = true,
        Action<bool>? setCursorHooksEnabled = null)
    {
        _gameDirectory = gameDirectory;
        _imgui = imgui;
        _log = log;
        _setCursorHooksEnabled = setCursorHooksEnabled;
        _handleToggleKey = handleToggleKey;
        _catalog = new ModCatalog(gameDirectory);

        // Start before the first frame: a synchronous mod scan here would stall
        // the game's first Present for as long as the DLL metadata reads take.
        RequestReload();
        StartKeyPoller();
    }

    /// <summary>
    /// Polls INSERT off the render thread. The game's Present hook then only
    /// reads a volatile flag instead of calling GetAsyncKeyState every frame.
    /// </summary>
    private void StartKeyPoller()
    {
        if (!_handleToggleKey || Interlocked.CompareExchange(ref _keyPollerStarted, 1, 0) != 0)
            return;

        var thread = new Thread(() =>
        {
            var held = false;
            while (true)
            {
                // Renderers that own the menu key (uih D3D12) opt out after
                // MenuOpenSource is assigned in the object initializer.
                if (!_suspended && MenuOpenSource is null)
                {
                    var down = (GetAsyncKeyState(VK_INSERT) & 0x8000) != 0;
                    if (down && !held)
                        _togglePressed = true;
                    held = down;
                }
                Thread.Sleep(30);
            }
        })
        {
            IsBackground = true,
            Name = "dropin-overlay-key-poller",
        };
        thread.Start();
    }

    private readonly bool _handleToggleKey;

    /// <summary>Atomically published, fully populated catalog snapshot.</summary>
    private ModCatalog Catalog => Volatile.Read(ref _catalog);

    /// <summary>
    /// Cheap per-frame gate for the D3D11 Present hook. Polls the toggle input
    /// without touching ImGui or the device, then reports whether this frame
    /// actually has anything to draw. With the menu closed and the badge
    /// hidden the hook forwards the frame untouched — no ImGui new-frame, draw
    /// pass, or COM interop on Wine/DXVK.
    /// </summary>
    public bool ShouldRenderFrame()
    {
        PollToggleInput();
        return _visible || WatermarkVisible(Catalog);
    }

    /// <summary>
    /// For renderers that own their ImGui frame (native uih D3D12, Faith),
    /// skip our managed UI calls entirely while idle. uih/Faith still render
    /// their own frame; this avoids the managed -> native P/Invoke stream for
    /// controls that are not on screen.
    /// </summary>
    public void RenderIfNeeded()
    {
        if (ShouldRenderFrame())
            Render();
    }

    private bool WatermarkVisible(ModCatalog catalog) =>
        !catalog.HideWatermark || unchecked(Environment.TickCount64 - _watermarkDeadline) < 0;

    private void PollToggleInput()
    {
        if (MenuOpenSource is not null)
        {
            var visible = MenuOpenSource();
            if (visible && !_visible)
                RequestReload();
            _visible = visible;
        }
        else if (_handleToggleKey && _togglePressed)
        {
            // Consume the edge queued by the background key poller. No
            // user32 call on the game's render thread while idle.
            _togglePressed = false;
            _visible = !_visible;
            if (_visible)
                RequestReload();
        }

        CursorLiberator.PanelOpen = _visible;

        // Cursor hooks are the only always-installed managed callbacks in the
        // input path; enable them only for the frames where the panel is open.
        if (_cursorHooksActive != _visible && !_cursorHookFaulted)
        {
            _cursorHooksActive = _visible;
            try
            {
                _setCursorHooksEnabled?.Invoke(_visible);
            }
            catch (Exception ex)
            {
                _cursorHookFaulted = true;
                _log($"cursor hook toggle failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Queues a catalog reload. Repeated requests (rapid toggles, a rescan
    /// click) coalesce into one background scan; nothing blocks the renderer.
    /// </summary>
    private void RequestReload()
    {
        Volatile.Write(ref _reloadPending, 1);
        StartReloadWorker();
    }

    private void StartReloadWorker()
    {
        if (Interlocked.CompareExchange(ref _reloadRunning, 1, 0) != 0)
            return;

        // Dedicated low-priority thread: the scan (file I/O + assembly metadata)
        // must not run on the render thread or steal cycles from game threads.
        var thread = new Thread(ReloadLoop)
        {
            IsBackground = true,
            Name = "dropin-overlay-mod-scan",
        };
        try
        {
            thread.Priority = ThreadPriority.BelowNormal;
        }
        catch
        {
            // Some runtimes reject priority changes; the scan still works.
        }
        thread.Start();
    }

    private void ReloadLoop()
    {
        try
        {
            do
            {
                Volatile.Write(ref _reloadPending, 0);
                var catalog = new ModCatalog(_gameDirectory);
                catalog.Reload();
                Volatile.Write(ref _catalog, catalog);
                _catalogReady = true;
            }
            while (Volatile.Read(ref _reloadPending) == 1);
        }
        catch (Exception ex)
        {
            _log($"mod scan failed: {ex.Message}");
            // Show an empty list rather than "Loading mods..." forever.
            Volatile.Write(ref _catalog, new ModCatalog(_gameDirectory));
            _catalogReady = true;
        }
        finally
        {
            Volatile.Write(ref _reloadRunning, 0);
            // A request may have landed after the loop's last check.
            if (Volatile.Read(ref _reloadPending) == 1)
                StartReloadWorker();
        }
    }

    public void Render()
    {
        var catalog = Catalog;

        if (!_scaleApplied)
        {
            _imgui.SetScale(2.0f);
            _scaleApplied = true;
        }

        RenderWatermark(catalog);

        // The D3D11 hook already polls input in ShouldRenderFrame; the other
        // renderers call ShouldRenderFrame via RenderIfNeeded. Keep the poll
        // here as well: edge detection lives in _insertHeld, so it is safe.
        PollToggleInput();

        // Games hide the hardware cursor; have ImGui draw a software cursor
        // while the panel is open so the mouse is usable. CursorLiberator's
        // hooks read PanelOpen to stop the game recentering/capturing it.
        _imgui.SetMouseDrawCursor(_visible);

        if (!_visible)
            return;

        var open = true;
        // "###" keeps the window ID stable while the visible title carries the
        // version, so ImGui window state survives version changes.
        if (!_imgui.Begin($"Reloaded-II Drop-In v{FormatVersion(catalog.DropInVersion)}###dropin-panel", ref open, WindowNoCollapse))
        {
            _imgui.End();
            return;
        }

        _imgui.Text($"Mods folder: {Path.Combine(_gameDirectory, "mods")}");

        if (!_catalogReady)
        {
            _imgui.TextDisabled("Loading mods...");
            _imgui.End();
            return;
        }

        RenderUpdateBanner(catalog);
        RenderP3rEnhancedBanner(catalog);
        if (_lastSaveError is not null)
            _imgui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f),
                $"SAVE FAILED - change will NOT apply: {_lastSaveError}");
        else if (_pendingRelaunchChanges)
            _imgui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1f),
                "Changes saved - they apply on the next game launch.");
        _imgui.Separator();

        if (_imgui.SmallButton("Rescan mods"))
            RequestReload();

        _imgui.SameLine(0, 12);
        var showWatermark = !catalog.HideWatermark;
        if (_imgui.Checkbox("Corner watermark", ref showWatermark))
        {
            catalog.HideWatermark = !showWatermark;
            Save(() => catalog.SaveToggles(), "watermark setting");
        }

        RenderModList(catalog, catalog.UserMods, "Your mods", baseMods: false);
        RenderModList(catalog, catalog.BaseMods, "Base mods (required)", baseMods: true);

        _imgui.End();

        if (!open)
            _visible = false;
    }

    private void RenderModList(ModCatalog catalog, List<CatalogMod> mods, string header, bool baseMods)
    {
        if (!_imgui.CollapsingHeader(header, baseMods ? 0 : TreeDefaultOpen))
            return;

        foreach (var mod in mods)
        {
            _imgui.PushId(mod.ModId);

            if (mod.IsBaseMod)
            {
                _imgui.Text($"[on] {mod.Name} {mod.Version}");
            }
            else
            {
                var enabled = mod.Enabled;
                if (_imgui.Checkbox($"{mod.Name} {mod.Version}", ref enabled))
                {
                    mod.Enabled = enabled;
                    Save(() => catalog.SaveToggles(), "toggles");
                }
            }

            if (mod.UserConfig is not null)
            {
                _imgui.SameLine(0, 12);
                var label = mod.ConfigExpanded ? "close settings" : "settings";
                if (mod.IsBaseMod)
                {
                    if (_imgui.SmallButton($"[{label}]###{mod.ModId}-cfg"))
                        mod.ConfigExpanded = !mod.ConfigExpanded;
                }
                else
                {
                    if (_imgui.SmallButton(label))
                        mod.ConfigExpanded = !mod.ConfigExpanded;
                }
                if (mod.ConfigExpanded)
                    RenderConfigEditor(catalog, mod);
            }

            if (mod.Options.Count > 0)
                RenderModOptions(catalog, mod);

            _imgui.PopId();
        }

        if (mods.Count == 0)
            _imgui.TextDisabled(baseMods ? "none" : "none yet - drop mod folders into mods/");
    }

    private void RenderModOptions(ModCatalog catalog, CatalogMod mod)
    {
        _imgui.Indent(16);
        _imgui.TextDisabled("Options:");
        RenderOptionTree(catalog, mod.Options, depth: 0);
        _imgui.Unindent(16);
    }

    /// <summary>
    /// Renders options as a tree grouped by their path segments (e.g.
    /// Options/Censorship/Ryuji Shoes renders "Censorship" as a collapsible
    /// section containing the "Ryuji Shoes" checkbox). Options directly under
    /// Options/ render as plain checkboxes.
    /// </summary>
    private void RenderOptionTree(ModCatalog catalog, List<CatalogModOption> options, int depth)
    {
        var leaves = new List<CatalogModOption>();
        var groups = new Dictionary<string, List<CatalogModOption>>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in options)
        {
            var segments = option.Segments;
            if (segments.Length == depth + 1)
                leaves.Add(option);
            else
            {
                var key = segments[depth];
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = [];
                list.Add(option);
            }
        }

        foreach (var leaf in leaves.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
            RenderOptionCheckbox(catalog, leaf);

        foreach (var group in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            _imgui.PushId(group.Key);
            if (_imgui.CollapsingHeader(group.Key, 0))
            {
                _imgui.Indent(16);
                RenderOptionTree(catalog, group.Value, depth + 1);
                _imgui.Unindent(16);
            }
            _imgui.PopId();
        }
    }

    private void RenderOptionCheckbox(ModCatalog catalog, CatalogModOption option)
    {
        _imgui.PushId(option.RelativePath);
        var enabled = option.Enabled;
        if (_imgui.Checkbox(option.Name, ref enabled))
        {
            option.Enabled = enabled;
            Save(() => catalog.SaveToggles(), "option toggles");
        }
        _imgui.PopId();
    }

    private void RenderConfigEditor(ModCatalog catalog, CatalogMod mod)
    {
        var config = mod.UserConfig!;
        _imgui.Indent(16);
        RenderConfigProperties(config, mod, ref _configChanged);
        _imgui.Unindent(16);

        if (_configChanged)
            Save(() => catalog.SaveConfig(mod), $"config for {mod.ModId}");
        _configChanged = false;
    }

    private bool _configChanged;

    private void RenderConfigProperties(JsonObject obj, CatalogMod mod, ref bool changed)
    {
        foreach (var property in obj.ToList())
        {
            var label = mod.ConfigDisplayNames.TryGetValue(property.Key, out var displayName)
                && !string.IsNullOrWhiteSpace(displayName)
                    ? $"{displayName}##{property.Key}"
                    : property.Key;

            if (property.Value is JsonObject nestedObj)
            {
                // Nested config section (e.g. ConfigCommon, ConfigP5R).
                if (_imgui.CollapsingHeader($"{label}###{property.Key}-section", TreeDefaultOpen))
                {
                    _imgui.Indent(16);
                    RenderConfigProperties(nestedObj, mod, ref changed);
                    _imgui.Unindent(16);
                }
            }
            else if (property.Value is JsonValue value)
            {
                if (value.TryGetValue<bool>(out var boolValue))
                {
                    if (_imgui.Checkbox(label, ref boolValue))
                    {
                        obj[property.Key] = boolValue;
                        changed = true;
                    }
                }
                else if (value.TryGetValue<long>(out var longValue))
                {
                    var intValue = (int)Math.Clamp(longValue, int.MinValue, int.MaxValue);
                    if (_imgui.InputInt(label, ref intValue))
                    {
                        obj[property.Key] = (long)intValue;
                        changed = true;
                    }
                }
                else if (value.TryGetValue<double>(out var numberValue))
                {
                    if (_imgui.InputDouble(label, ref numberValue))
                    {
                        obj[property.Key] = numberValue;
                        changed = true;
                    }
                }
                else if (value.TryGetValue<string>(out var stringValue))
                {
                    var buffer = new byte[256];
                    var bytes = Encoding.UTF8.GetBytes(stringValue);
                    Array.Copy(bytes, buffer, Math.Min(bytes.Length, buffer.Length - 1));
                    var edited = _imgui.InputText(label, buffer);

                    if (edited)
                    {
                        var terminator = Array.IndexOf(buffer, (byte)0);
                        obj[property.Key] = Encoding.UTF8.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator);
                        changed = true;
                    }
                }
            }
        }
    }

    private void RenderP3rEnhancedBanner(ModCatalog catalog)
    {
        if (!catalog.P3rEnhancedMissing)
            return;

        _imgui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f),
            "P3R Enhanced mod is missing (P3RE.dll not found in game directory).");
        _imgui.Text("See https://www.patreon.com/huutaiii/posts/p3r-enhanced-fsr-159746051");
    }

    /// <summary>
    /// Banner shown when the sync pass found a newer release on GitHub.
    /// Offers a one-click download to the user's Downloads folder.
    /// </summary>
    private void RenderUpdateBanner(ModCatalog catalog)
    {
        if (!catalog.UpdateAvailable || catalog.UpdateDownloadUrl is null)
            return;

        _imgui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f),
            $"Update available: v{catalog.LatestVersion} (you have v{FormatVersion(catalog.DropInVersion)})");
        _imgui.SameLine(0, 12);

        switch (_downloadState)
        {
            case 0: // idle
                if (_imgui.SmallButton("Download"))
                    StartDownload(catalog.UpdateDownloadUrl, catalog.LatestVersion!);
                break;
            case 1: // downloading
                _imgui.TextDisabled("downloading...");
                break;
            case 2: // done
                _imgui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), _downloadStatus);
                break;
            case 3: // error
                _imgui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), _downloadStatus);
                _imgui.SameLine(0, 12);
                if (_imgui.SmallButton("Retry"))
                    StartDownload(catalog.UpdateDownloadUrl, catalog.LatestVersion!);
                break;
        }
    }

    private static string FormatVersion(string version) =>
        version.EndsWith("-dev", StringComparison.Ordinal) ? version[..^4] : version;

    private void StartDownload(string url, string version)
    {
        _downloadState = 1;
        _downloadStatus = "";
        var fileName = $"unloaded-ii-{version}.zip";

        Task.Run(async () =>
        {
            try
            {
                var downloadsDir = GetDownloadsFolder();
                Directory.CreateDirectory(downloadsDir);
                var destPath = Path.Combine(downloadsDir, fileName);

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Unloaded-II-DropIn");
                var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(destPath, bytes).ConfigureAwait(false);

                _downloadStatus = $"saved to {destPath}";
                _downloadState = 2;
                _log($"downloaded update: {destPath}");
            }
            catch (Exception ex)
            {
                _downloadStatus = $"download failed: {ex.Message}";
                _downloadState = 3;
                _log($"update download failed: {ex.Message}");
            }
        });
    }

    private static string GetDownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(FolderDownloads, 0, IntPtr.Zero, out var pathPtr) == 0)
            {
                var path = Marshal.PtrToStringUni(pathPtr);
                Marshal.FreeCoTaskMem(pathPtr);
                if (!string.IsNullOrEmpty(path))
                    return path;
            }
        }
        catch
        {
            // Fall through to the manual path.
        }

        // Fallback: under Wine/Proton the Windows user profile maps to the
        // Linux home, so ~/Downloads is almost always correct.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>
    /// Short-lived corner badge (REFramework-style) proving the loader is
    /// active. Bottom-right, click-through, hidden from steady-state frames
    /// unless pinned from the panel.
    /// </summary>
    private void RenderWatermark(ModCatalog catalog)
    {
        if (!WatermarkVisible(catalog))
            return;

        var display = _imgui.DisplaySize;
        _imgui.SetNextWindowPos(
            new Vector2(display.X - 10, display.Y - 10),
            CondAlways,
            Vector2.One);
        _imgui.SetNextWindowBgAlpha(0.35f);

        var open = true;
        const int flags = WindowNoTitleBar | WindowNoResize | WindowNoScrollbar |
                          WindowNoCollapse | WindowAlwaysAutoResize | WindowNoSavedSettings |
                          WindowNoFocusOnAppearing | WindowNoNav | WindowNoMouseInputs | WindowNoMove;
        if (_imgui.Begin("##dropin-watermark", ref open, flags))
        {
            // ASCII only: even Latin-1 glyphs render as '?' through this stack.
            _imgui.Text($"Unloaded-II - v{FormatVersion(catalog.DropInVersion)} - Press Insert");
        }

        _imgui.End();
    }

    public void ToggleVisible()
    {
        SetVisible(!_visible);
    }

    public void SetVisible(bool visible)
    {
        if (_visible == visible)
            return;

        _visible = visible;
        if (visible)
            RequestReload();
    }

    /// <summary>Pauses off-thread key polling while the mod is suspended.</summary>
    public void SetSuspended(bool suspended) => _suspended = suspended;

    /// <summary>
    /// Called when the mod is suspended: drop the cached hook state so the
    /// next poll after Resume reinstalls the cursor hooks if the panel is open.
    /// </summary>
    public void ResetCursorHookState()
    {
        _cursorHooksActive = false;
        _cursorHookFaulted = false;
        try
        {
            _setCursorHooksEnabled?.Invoke(false);
        }
        catch (Exception ex)
        {
            _cursorHookFaulted = true;
            _log($"cursor hook toggle failed: {ex.Message}");
        }
    }

    private void Save(Action save, string what)
    {
        try
        {
            save();
            _pendingRelaunchChanges = true;
            _lastSaveError = null;
        }
        catch (Exception ex)
        {
            _log($"failed to save {what}: {ex.Message}");
            _lastSaveError = $"{what} ({ex.GetType().Name}: {ex.Message})";
            // Re-read disk truth so the UI can't show a toggle that didn't stick.
            RequestReload();
        }
    }

    // Dear ImGui flag values are stable across the 1.88 D3D11 and Faith 1.92
    // bindings. Keeping them local avoids leaking either binding into the UI.
    private const int WindowNoTitleBar = 1 << 0;
    private const int WindowNoResize = 1 << 1;
    private const int WindowNoMove = 1 << 2;
    private const int WindowNoScrollbar = 1 << 3;
    private const int WindowNoCollapse = 1 << 5;
    private const int WindowAlwaysAutoResize = 1 << 6;
    private const int WindowNoSavedSettings = 1 << 8;
    private const int WindowNoMouseInputs = 1 << 9;
    private const int WindowNoFocusOnAppearing = 1 << 12;
    private const int WindowNoNav = 1 << 18;
    private const int TreeDefaultOpen = 1 << 5;
    private const int CondAlways = 1 << 0;
}
