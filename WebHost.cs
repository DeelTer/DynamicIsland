using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Island;

/// Borderless, per-pixel transparent top-level window hosting one WebView2 page.
sealed class WebHost : Form
{
    static Task<CoreWebView2Environment>? environment;
    readonly WebView2 view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
    readonly List<string> pending = new();
    readonly string page;
    readonly bool tool;
    bool ready;

    public event Action<string, JsonElement, List<string>>? Message;
    public event Action? DpiMoved;
    public Func<int, nint, bool>? Hook;

    public WebHost(string page, bool tool)
    {
        this.page = page; this.tool = tool;
        // Transparent from the very first frame: otherwise Chromium paints white until the page composites.
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00000000");
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        TopMost = true;
        Text = "Island";
        Controls.Add(view);
        _ = Initialize();
    }

    public new double Scale => Native.GetDpiForWindow(Handle) / 96.0;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: no taskbar / Alt+Tab entry
            return p;
        }
    }

    protected override bool ShowWithoutActivation => tool;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.MakeTransparent(Handle);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Color.Black);

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == 0x02E0) // WM_DPICHANGED: sizes are managed by the owner, not by WinForms scaling
        {
            m.Result = 0;
            DpiMoved?.Invoke();
            return;
        }
        if (Hook != null && Hook(m.Msg, m.WParam)) { m.Result = 0; return; }
        base.WndProc(ref m);
    }

    async Task Initialize()
    {
        try
        {
            environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Folder, "webview"),
                new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection,OverscrollHistoryNavigation --force-color-profile=srgb"));
            await view.EnsureCoreWebView2Async(await environment);
        }
        catch (Exception error)
        {
            MessageBox.Show("Для Island нужен Microsoft Edge WebView2 Runtime.\n\n" + error.Message, "Island");
            Application.Exit();
            return;
        }
        var core = view.CoreWebView2;
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("ISLAND_DEVTOOLS") == "1";
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        core.SetVirtualHostNameToFolderMapping("island.local", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
        // Clipboard thumbnails as cached files instead of megabytes of data URLs per update.
        Directory.CreateDirectory(Path.Combine(Store.Folder, "thumbs"));
        core.SetVirtualHostNameToFolderMapping("thumbs.island", Path.Combine(Store.Folder, "thumbs"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += Received;
        core.Navigate("https://island.local/" + page);
    }

    void Received(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement message;
        try { using var document = JsonDocument.Parse(e.WebMessageAsJson); message = document.RootElement.Clone(); }
        catch (JsonException) { return; }
        string type = message.TryGetProperty("t", out var t) ? t.GetString() ?? "" : "";
        if (type == "ready")
        {
            ready = true;
            foreach (var json in pending) view.CoreWebView2.PostWebMessageAsJson(json);
            pending.Clear();
        }
        var files = new List<string>();
        if (e.AdditionalObjects != null)
            foreach (var item in e.AdditionalObjects)
                if (item is CoreWebView2File file) files.Add(file.Path);
        // Never run nested message loops (dialogs, drag-drop) inside a WebView2 callback.
        BeginInvoke(() => Message?.Invoke(type, message, files));
    }

    public void Send(object payload)
    {
        string json = JsonSerializer.Serialize(payload, Store.Web);
        if (!ready || view.CoreWebView2 == null) pending.Add(json);
        else view.CoreWebView2.PostWebMessageAsJson(json);
    }

    /// Collapsed island: ask Chromium to trim caches; the panel restores the normal level.
    public void Lean(bool on)
    {
        if (view.CoreWebView2 != null)
            view.CoreWebView2.MemoryUsageTargetLevel = on ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
    }

    public void FocusPage() { if (view.CoreWebView2 != null) view.Focus(); }
}
