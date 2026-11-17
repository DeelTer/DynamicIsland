using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Island;

/// Native core: window placement and motion, system integrations. All UI lives in web/.
sealed partial class IslandApp : ApplicationContext
{
    const double WinW = 760, WinH = 520, EdgeMargin = 140;
    readonly WebHost island = new("island.html", true);
    readonly NotifyIcon tray = new() { Text = "Island", Visible = true };
    readonly System.Windows.Forms.Timer tick = new() { Interval = 250 };
    readonly System.Windows.Forms.Timer noteSave = new() { Interval = 500 };
    State state = Store.Load();
    nint homeMonitor, shownMonitor = -1;
    bool paused;
    DateTimeOffset leanAt = DateTimeOffset.Now.AddSeconds(15);
    int ticks;
    uint ownSequence;
    ToolStripMenuItem? historyItem, hoverItem;

    // Motion state, physical pixels: island centre X and window top.
    double posX, posTop, velX, velY, goalX, goalTop, grabX, grabY;
    volatile bool moving;
    bool dragging;
    double regionX, regionY = 0, regionW = 320, regionH = 70;

    public IslandApp()
    {
        tray.Icon = MakeTrayIcon();
        island.Hook = WndHook;
        island.Message += OnIsland;
        island.DpiMoved += () => { Place(!moving); ApplyRegion(); };
        island.Deactivate += (_, _) => island.Send(new { t = "blur" });
        _ = island.Handle;
        regionX = WinW / 2 - 160;
        Native.AddClipboardFormatListener(island.Handle);
        if (!Native.RegisterHotKey(island.Handle, 1, 0x4003, 0x49))
            tray.ShowBalloonTip(2500, "Island", "Ctrl+Alt+I уже занято. Откройте Island из трея.", ToolTipIcon.Info);
        Native.RegisterHotKey(island.Handle, 2, 0x4003, 0x56);
        Native.RegisterHotKey(island.Handle, 3, 0x4003, 0x44); // Ctrl+Alt+D: dictate into the focused field
        homeMonitor = FindMonitor(state.AnchorMonitor);
        ConfigureTray();
        StartDownloadWatch();
        Task.Run(CleanThumbs);
        StartNotifyWatch();
        tick.Tick += (_, _) => Tick();
        tick.Start();
        noteSave.Tick += (_, _) => { noteSave.Stop(); Save(); };
        CheckForeground();
    }

    // ---------------------------------------------------------------- placement & motion

    static nint FindMonitor(string? device)
    {
        var all = Native.Monitors();
        return all.FirstOrDefault(m => Native.Monitor(m).Device == device, Native.MonitorFromPoint(default, 1));
    }

    int placedW, placedH;

    void Place(bool geo)
    {
        double s = island.Scale;
        int w = (int)Math.Round(WinW * s), h = (int)Math.Round(WinH * s);
        // Pure moves pass SWP_NOSIZE: a size in the call makes WebView2 rebuild its surface and flash white.
        uint flags = 0x114 | (w == placedW && h == placedH ? 0x1u : 0); // NOZORDER|NOACTIVATE|NOCOPYBITS
        Native.SetWindowPos(island.Handle, 0, (int)Math.Round(posX - w / 2.0), (int)Math.Round(posTop), w, h, flags);
        placedW = w; placedH = h;
        if (geo) SendGeo();
    }

    void SendGeo()
    {
        var monitor = Native.Monitor(Native.MonitorFromPoint(new Native.Point { X = (int)posX, Y = (int)posTop + 2 }, 2));
        double s = island.Scale, left = posX - WinW * s / 2;
        island.Send(new { t = "geo", visLeft = (monitor.Screen.Left - left) / s, visRight = (monitor.Screen.Right - left) / s, w = WinW, h = WinH });
    }

    void ApplyRegion()
    {
        double s = island.Scale;
        nint region = Native.CreateRectRgn((int)Math.Floor(regionX * s), (int)Math.Floor(regionY * s),
            (int)Math.Ceiling((regionX + regionW) * s), (int)Math.Ceiling((regionY + regionH) * s));
        // bRedraw=false: a redraw erases the WebView2 child with its white class brush for one frame;
        // DWM applies the new shape anyway and Chromium keeps compositing its own content.
        if (Native.SetWindowRgn(island.Handle, region, false) == 0) Native.DeleteObject(region);
    }

    void HomePosition(nint monitor)
    {
        var info = Native.Monitor(monitor);
        double margin = EdgeMargin * Native.MonitorScale(monitor);
        posX = Math.Clamp(info.Screen.Left + state.AnchorX * info.Screen.Width, info.Screen.Left + margin, info.Screen.Right - margin);
        posTop = info.Screen.Top;
        velX = velY = 0;
    }

    void StartMotion()
    {
        if (moving) return;
        moving = true;
        new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            double last = 0;
            while (moving)
            {
                Native.DwmFlush(); // pace on the compositor's vsync
                double now = clock.Elapsed.TotalSeconds;
                if (now - last < 0.002) { Thread.Sleep(2); continue; }
                double dt = Math.Min(now - last, 1 / 20.0);
                last = now;
                try { island.Invoke(() => MotionFrame(dt)); } catch (Exception) { moving = false; }
            }
        }) { IsBackground = true, Name = "Island motion" }.Start();
    }

    void MotionFrame(double dt)
    {
        if (!moving) return;
        if (dragging)
        {
            if (!Native.MouseDown) { EndDrag(); return; }
            var cursor = Native.Cursor;
            double x = cursor.X - grabX, y = cursor.Y - grabY;
            var under = Native.Monitor(Native.MonitorFromPoint(cursor, 2)).Screen;
            y = Math.Max(y, under.Top);
            x = Math.Clamp(x, under.Left + 60, under.Right - 60);
            velX = velX * .6 + (x - posX) / dt * .4;
            velY = velY * .6 + (y - posTop) / dt * .4;
            posX = x; posTop = y;
            Place(false);
            return;
        }
        const double stiffness = 560, damping = 40;
        int steps = Math.Max(1, (int)Math.Ceiling(dt * 480));
        double h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            velX += (-stiffness * (posX - goalX) - damping * velX) * h;
            velY += (-stiffness * (posTop - goalTop) - damping * velY) * h;
            posX += velX * h; posTop += velY * h;
        }
        if (Math.Abs(posX - goalX) < .4 && Math.Abs(posTop - goalTop) < .4 && Math.Abs(velX) + Math.Abs(velY) < 10)
        {
            posX = goalX; posTop = goalTop; velX = velY = 0;
            moving = false;
            Place(true);
            island.Send(new { t = "settled" });
            return;
        }
        Place(false);
    }

    void StartDrag()
    {
        if (shownMonitor == 0) return;
        var cursor = Native.Cursor;
        grabX = cursor.X - posX; grabY = cursor.Y - posTop;
        dragging = true;
        StartMotion();
    }

    void EndDrag()
    {
        dragging = false;
        var cursor = Native.Cursor;
        var monitor = Native.MonitorFromPoint(cursor, 2);
        var info = Native.Monitor(monitor);
        double margin = EdgeMargin * Native.MonitorScale(monitor);
        goalX = Math.Clamp(posX, info.Screen.Left + margin, info.Screen.Right - margin);
        goalTop = info.Screen.Top;
        velX = Math.Clamp(velX, -3000, 3000); velY = Math.Clamp(velY, -3000, 3000);
        homeMonitor = shownMonitor = monitor;
        state = state with { AnchorMonitor = info.Device, AnchorX = (goalX - info.Screen.Left) / info.Screen.Width };
        Save();
        island.Send(new { t = "dragEnd" });
    }

    void ResetPosition()
    {
        state = state with { AnchorX = .5 };
        Save();
        if (shownMonitor <= 0) return;
        var info = Native.Monitor(shownMonitor);
        goalX = info.Screen.Left + info.Screen.Width / 2.0; goalTop = info.Screen.Top;
        StartMotion();
    }

    // ---------------------------------------------------------------- fullscreen / game avoidance

    static bool Covers(Native.Rect frame, Native.Rect screen) =>
        screen.Right > screen.Left && frame.Left <= screen.Left + 2 && frame.Top <= screen.Top + 2 &&
        frame.Right >= screen.Right - 2 && frame.Bottom >= screen.Bottom - 2;

    static readonly string[] Overlays = ["ScreenClippingHost", "SnippingTool", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchApp", "TextInputHost", "LockApp", "ShellHost", "Island", "explorer"];
    nint gameCandidate = -1, lastProcessWindow;
    string lastProcess = "";

    void CheckForeground()
    {
        if (dragging || picking) return;
        var window = Native.GetForegroundWindow();
        bool own = window == island.Handle;
        if (own && shownMonitor > 0) return;
        if (own) window = 0;
        var monitors = Native.Monitors();
        if (!monitors.Contains(homeMonitor)) homeMonitor = FindMonitor(state.AnchorMonitor);
        nint game = 0;
        if (window != 0)
        {
            var monitor = Native.MonitorFromWindow(window, 2);
            bool desktop = Native.WindowClass(window) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
            bool full = !desktop && Covers(Native.Bounds(window), Native.Monitor(monitor).Screen);
            string process = window == lastProcessWindow ? lastProcess : Native.ProcessName(window);
            lastProcessWindow = window; lastProcess = process;
            bool overlay = Overlays.Contains(process, StringComparer.OrdinalIgnoreCase);
            if (!overlay && (full || state.Games.Contains(process, StringComparer.OrdinalIgnoreCase))) game = monitor;
            if (window != 0 && !overlay) lastExternal = window;
        }
        // A fullscreen window must persist for two checks: transient overlays (snipping, Start) never move the island.
        if (game != gameCandidate) { gameCandidate = game; return; }
        nint target = paused ? 0 : homeMonitor;
        if (target != 0 && target == game) target = monitors.FirstOrDefault(m => m != game);
        if (target != shownMonitor) Log($"relocate {shownMonitor}->{target} home={homeMonitor} game={game} fg={Native.ProcessName(window)} monitors={string.Join(",", monitors)}");
        if (target != shownMonitor) Relocate(target);
    }

    async void Relocate(nint target)
    {
        bool wasShown = shownMonitor > 0 && island.Visible;
        shownMonitor = target;
        if (wasShown) { island.Send(new { t = "vanish" }); await Task.Delay(230); }
        if (shownMonitor != target) return;
        if (target == 0) { island.Hide(); return; }
        moving = dragging = false;
        HomePosition(target);
        Place(true);
        if (!island.Visible) island.Show();
        Place(true); // DPI of the destination monitor is known once the window lives there
        ApplyRegion();
        island.Send(new { t = "appear" });
    }

    // ---------------------------------------------------------------- messages

    bool WndHook(int message, nint wParam)
    {
        if (message == 0x312)
        {
            if (wParam == 1) { island.Send(new { t = "toggle" }); Native.SetForegroundWindow(island.Handle); island.FocusPage(); }
            else if (wParam == 2) ShowClipboard();
            else if (wParam == 3) VoiceHotkey();
            return true;
        }
        if (message == 0x31D) { island.BeginInvoke(Capture); return true; }
        return false;
    }

    void OnIsland(string type, JsonElement m, List<string> files)
    {
        switch (type)
        {
            case "ready": PushAll(); SendGeo(); break;
            case "region":
                regionX = m.GetProperty("x").GetDouble(); regionY = m.GetProperty("y").GetDouble();
                regionW = m.GetProperty("w").GetDouble(); regionH = m.GetProperty("h").GetDouble();
                ApplyRegion();
                break;
            case "dragStart": StartDrag(); break;
            case "volume": Native.MediaKey(m.GetProperty("dir").GetInt32() > 0 ? (byte)0xAF : (byte)0xAE); break;
            case "pasteLast": PasteLast(); break;
            case "audioList": PushAudio(); break;
            case "audioSet":
                try { AudioDevices.SetDefault(Str(m, "id")); } catch (Exception) { Toast("error", "Не удалось переключить", "", "warning-circle", "#ff6b6b"); }
                PushAudio();
                break;
            case "fxRefresh": _ = RefreshRates(true); break;
            case "fxPrefs": state = state with { Fx = m.GetProperty("v").GetString() }; Save(); break;
            case "shelfClearOld":
                foreach (var key in (state.Shelf ?? []).Where(k => !k.StartsWith("ref:") && File.Exists(Store.ShelfPath(k)) &&
                    DateTime.Now - File.GetCreationTime(Store.ShelfPath(k)) > TimeSpan.FromHours(24)).ToList()) RemoveShelf(key, false);
                Save(); PushShelf();
                break;
            case "panel":
                bool panelOpen = m.GetProperty("open").GetBoolean();
                leanAt = panelOpen ? DateTimeOffset.MaxValue : DateTimeOffset.Now.AddSeconds(15);
                if (panelOpen) { island.Lean(false); if (recorder == null && !voiceBusy) _ = CaptureDictationTarget(); }
                break;
            case "focus": Native.SetForegroundWindow(island.Handle); break;
            case "timerAck": StopTimerSound(); timerDone = null; PushTimer(); break;
            case "clipHistory": case "clipClear": case "clipPin": case "clipRemove": case "clipCopy": case "clipPaste": case "clipDrag":
                OnClip(type, m); break;
            case "media": ControlMedia(Str(m, "cmd"), m.TryGetProperty("frac", out var f) ? f.GetDouble() : 0); break;
            case "launch": Launch(Str(m, "id")); break;
            case "addApp": AddApp(); break;
            case "removeApp": state.Apps.RemoveAll(a => a.Path == Str(m, "id")); Save(); PushApps(); break;
            case "appDrop": foreach (var file in files) AddAppPath(file); break;
            case "timer": TimerCommand(Str(m, "cmd"), m.TryGetProperty("min", out var min) ? min.GetDouble() : 0); break;
            case "voice": ToggleVoice(); break;
            case "pick": PickColor(); break;
            case "snip": StartSnip(); break;
            case "fullshot": FullShot(); break;
            case "clipboard": ShowClipboard(); break;
            case "note":
                state = state with { Note = Str(m, "text") };
                noteSave.Stop(); noteSave.Start();
                break;
            case "copy": CopyText(Str(m, "text")); break;
            case "shelfDrop": AddShelfFiles(files); break;
            case "shelfPaste": PasteToShelf(); break;
            case "shelfRemove": RemoveShelf(Str(m, "id")); break;
            case "shelfClear": foreach (var key in (state.Shelf ?? []).ToList()) RemoveShelf(key, false); Save(); PushShelf(); break;
            case "shelfOpen": OpenShelf(Str(m, "id")); break;
            case "shelfDrag": DragShelf(m.GetProperty("ids").EnumerateArray().Select(e => e.GetString() ?? "").ToList()); break;
            case "toastAction": ToastAction(Str(m, "id")); break;
            case "hover": SetHover(m.GetProperty("on").GetBoolean()); break;
            case "menu": tray.ContextMenuStrip?.Show(Cursor.Position); break;
        }
    }

    static string Str(JsonElement m, string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    void Tick()
    {
        ticks++;
        if (DateTimeOffset.Now > leanAt) { leanAt = DateTimeOffset.MaxValue; island.Lean(true); }
        CheckTimer();
        if (ticks % 2 == 0) PollDownloads();
        if (ticks % 20 == 7 && state.Notify) CheckNotifications(); // fallback if the file watcher misses a write
        if (ticks % 4 == 0)
        {
            CheckForeground();
            if (ticks % 12 == 0 || mediaPlaying) RefreshMedia(); // events drive changes; this is only a fallback
            if (recorder != null && DateTimeOffset.Now - recordingStarted > TimeSpan.FromMinutes(3)) ToggleVoice();
        }
        if (ticks % 120 == 1) PushBattery();
        if (ticks % 14400 == 2 && ExpireClips()) { Save(); PushClips(); } // hourly
        if (ticks % 7200 == 3) _ = RefreshRates(false); // every 30 min
    }

    void Patch(string key, object? value) => island.Send(new Dictionary<string, object?> { ["t"] = "patch", [key] = value });

    void PushAll()
    {
        Patch("hoverOpen", state.HoverOpen);
        Patch("note", state.Note);
        Patch("colors", state.Colors ?? []);
        Patch("fxPrefs", state.Fx);
        PushRates();
        PushApps(); PushShelf(); PushTimer(); PushRec(); PushBattery(); PushClips();
        mediaSignature = null; artKey = null; RefreshMedia();
    }

    void PushBattery()
    {
        var power = SystemInformation.PowerStatus;
        bool has = !power.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery) && power.BatteryChargeStatus != BatteryChargeStatus.Unknown;
        Patch("battery", new { has, pct = (int)Math.Round(power.BatteryLifePercent * 100), charging = power.PowerLineStatus == PowerLineStatus.Online });
    }

    [Conditional("DEBUG")]
    static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(Store.Folder, "island.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + text + Environment.NewLine); }
        catch (Exception) { }
    }

    void Save()
    {
        if (!Store.Save(state)) tray.ShowBalloonTip(2000, "Island", "Не удалось сохранить настройки или историю.", ToolTipIcon.Warning);
    }

    public void Toast(string kind, string title, string sub = "", string icon = "", string color = "", object[]? actions = null, string? image = null, int ms = 4200, object? extra = null) =>
        island.Send(new { t = "toast", kind, title, sub, icon, color, actions = actions ?? [], image, ms, extra });

    static object Act(string id, string? label = null, string? icon = null, bool primary = false) => new { id, label, icon, primary };

    void ToastAction(string id)
    {
        if (id == "timer5") StartTimer(5);
        else if (id.StartsWith("timer:") && int.TryParse(id[6..], out int minutes)) StartTimer(minutes);
        else if (id == "shelfFolder") Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Path.Combine(Store.Folder, "shelf") + "\"") { UseShellExecute = true });
        else if (id.StartsWith("copy:")) CopyText(id[5..]);
        else if (id.StartsWith("url:") && Uri.TryCreate(id[4..], UriKind.Absolute, out var url) && url.Scheme is "http" or "https") Shell(url.AbsoluteUri);
        else if (id.StartsWith("open:") && File.Exists(id[5..])) Shell(id[5..]);
        else if (id.StartsWith("reveal:") && File.Exists(id[7..])) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{id[7..]}\"") { UseShellExecute = true });
        else if (id.StartsWith("shelfAdd:") && File.Exists(id[9..])) AddShelfFiles([id[9..]]);
        else if (id.StartsWith("app:")) FocusApp(id[4..]);
    }

    static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception) { }
    }

    // ---------------------------------------------------------------- tray

    static Icon MakeTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(3, 10, 12, 12, 90, 180); path.AddArc(17, 10, 12, 12, 270, 180); path.CloseFigure();
            g.FillPath(Brushes.White, path);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    void ConfigureTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть Island", null, (_, _) => { island.Send(new { t = "open" }); Native.SetForegroundWindow(island.Handle); });
        menu.Items.Add("Буфер обмена   Ctrl+Alt+V", null, (_, _) => ShowClipboard());
        menu.Items.Add(new ToolStripSeparator());
        var pause = new ToolStripMenuItem("Скрыть остров") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => { paused = pause.Checked; CheckForeground(); };
        menu.Items.Add(pause);
        hoverItem = new ToolStripMenuItem("Раскрывать при наведении") { Checked = state.HoverOpen, CheckOnClick = true };
        hoverItem.CheckedChanged += (_, _) => SetHover(hoverItem.Checked);
        menu.Items.Add(hoverItem);
        historyItem = new ToolStripMenuItem("Записывать историю буфера") { Checked = state.History, CheckOnClick = true };
        historyItem.CheckedChanged += (_, _) => SetHistory(historyItem.Checked);
        menu.Items.Add(historyItem);
        var notify = new ToolStripMenuItem("Уведомления Telegram / Discord / WhatsApp") { Checked = state.Notify, CheckOnClick = true };
        var notifyText = new ToolStripMenuItem("   Показывать текст сообщений") { Checked = state.NotifyText, CheckOnClick = true, Enabled = state.Notify };
        notify.CheckedChanged += (_, _) => { state = state with { Notify = notify.Checked }; notifyText.Enabled = notify.Checked; Save(); };
        notifyText.CheckedChanged += (_, _) => { state = state with { NotifyText = notifyText.Checked }; Save(); };
        menu.Items.Add(notify);
        menu.Items.Add(notifyText);
        var autostart = new ToolStripMenuItem("Запускать вместе с Windows") { Checked = Autostart, CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => { Autostart = autostart.Checked; autostart.Checked = Autostart; };
        menu.Items.Add(autostart);
        menu.Items.Add("Вернуть остров в центр", null, (_, _) => ResetPosition());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Добавить игру в исключения…", null, (_, _) => AddGame());
        menu.Items.Add("Сбросить исключения игр", null, (_, _) => { state.Games.Clear(); Save(); CheckForeground(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { island.Send(new { t = "open" }); Native.SetForegroundWindow(island.Handle); } };
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// Per-user autostart via HKCU\...\Run (no admin rights needed).
    static bool Autostart
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Island") is string value && value.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue("Island", $"\"{Environment.ProcessPath}\"");
                else key.DeleteValue("Island", false);
            }
            catch (Exception) { }
        }
    }

    void SetHover(bool on)
    {
        state = state with { HoverOpen = on };
        Save();
        if (hoverItem != null && hoverItem.Checked != on) hoverItem.Checked = on;
        Patch("hoverOpen", on);
    }

    void AddGame()
    {
        using var dialog = new OpenFileDialog { Title = "Выберите файл игры", Filter = "Программы|*.exe" };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        var name = Path.GetFileNameWithoutExtension(dialog.FileName);
        if (!state.Games.Contains(name, StringComparer.OrdinalIgnoreCase)) state.Games.Add(name);
        Save(); CheckForeground();
    }

    protected override void ExitThreadCore()
    {
        moving = false;
        recorder?.Dispose();
        if (noteSave.Enabled) Save();
        Native.RemoveClipboardFormatListener(island.Handle);
        Native.UnregisterHotKey(island.Handle, 1);
        Native.UnregisterHotKey(island.Handle, 2);
        tray.Visible = false;
        tray.Dispose();
        island.Close();
        base.ExitThreadCore();
    }

    // ---------------------------------------------------------------- images

    internal static string DataUrl(Image image, int maxSide, bool jpeg = false)
    {
        double k = Math.Min(1, maxSide / (double)Math.Max(image.Width, image.Height));
        int w = Math.Max(1, (int)Math.Round(image.Width * k)), h = Math.Max(1, (int)Math.Round(image.Height * k));
        using var scaled = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(image, 0, 0, w, h);
        }
        using var stream = new MemoryStream();
        scaled.Save(stream, jpeg ? ImageFormat.Jpeg : ImageFormat.Png);
        return (jpeg ? "data:image/jpeg;base64," : "data:image/png;base64,") + Convert.ToBase64String(stream.ToArray());
    }

    readonly Dictionary<string, string?> iconCache = new(StringComparer.OrdinalIgnoreCase);

    string? IconUrl(string path, bool thumbnail = false, int size = 96)
    {
        string key = (thumbnail ? "t:" : "i:") + path;
        if (iconCache.TryGetValue(key, out var cached)) return cached;
        using var bitmap = Native.ShellImage(path, size * 2, !thumbnail);
        cached = bitmap == null ? null : DataUrl(bitmap, size * 2);
        if (iconCache.Count > 150) iconCache.Clear(); // data URLs are big; keep the cache bounded
        iconCache[key] = cached;
        return cached;
    }
}
