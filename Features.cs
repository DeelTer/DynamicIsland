using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Media.Capture;
using Windows.Media.Control;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Island;

sealed partial class IslandApp
{
    // ---------------------------------------------------------------- media

    GlobalSystemMediaTransportControlsSessionManager? mediaManager;
    GlobalSystemMediaTransportControlsSession? mediaSession;
    bool mediaBusy, mediaPlaying;
    readonly HashSet<string> subscribed = new();

    // GSMTC events arrive on a thread-pool thread; coalesce them onto the UI thread.
    bool mediaQueued;
    void MediaEvent()
    {
        if (mediaQueued) return;
        mediaQueued = true;
        island.BeginInvoke(async () => { await Task.Delay(60); mediaQueued = false; RefreshMedia(); });
    }
    string? mediaSignature, artKey;

    static bool Playing(GlobalSystemMediaTransportControlsSession? session) =>
        session?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    static string SourceName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        string lower = id.ToLowerInvariant();
        foreach (var (key, name) in new[] { ("spotify", "Spotify"), ("chrome", "Chrome"), ("msedge", "Edge"), ("firefox", "Firefox"),
            ("yandex", "Яндекс"), ("zunemusic", "Медиаплеер"), ("vlc", "VLC"), ("telegram", "Telegram"), ("opera", "Opera"), ("brave", "Brave") })
            if (lower.Contains(key)) return name;
        string name2 = id.Split('!')[0].Split('_')[0];
        name2 = Path.GetFileNameWithoutExtension(name2);
        return name2.Contains('.') ? name2[(name2.LastIndexOf('.') + 1)..] : name2;
    }

    async void RefreshMedia()
    {
        if (mediaBusy) return;
        mediaBusy = true;
        try
        {
            if (mediaManager == null)
            {
                mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                mediaManager.CurrentSessionChanged += (_, _) => MediaEvent();
                mediaManager.SessionsChanged += (_, _) => MediaEvent();
            }
            var current = mediaManager.GetCurrentSession();
            var session = Playing(current) ? current : mediaManager.GetSessions().FirstOrDefault(Playing) ?? current;
            // Each GSMTC call hands back a fresh wrapper, so track subscriptions by app id — otherwise
            // handlers pile up on every refresh and each media event fans out into dozens of refreshes.
            if (session != null && subscribed.Add(session.SourceAppUserModelId))
            {
                session.MediaPropertiesChanged += (_, _) => MediaEvent();
                session.PlaybackInfoChanged += (_, _) => MediaEvent();
            }
            mediaSession = session;
            mediaPlaying = Playing(session);
            if (session == null)
            {
                if (mediaSignature != "none") { mediaSignature = "none"; Patch("media", new { has = false }); }
                return;
            }
            var properties = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();
            bool playing = Playing(session);
            double duration = timeline == null ? 0 : (timeline.EndTime - timeline.StartTime).TotalSeconds;
            double position = timeline == null ? 0 : (timeline.Position - timeline.StartTime).TotalSeconds;
            if (playing && timeline != null && timeline.LastUpdatedTime.Year > 2000)
                position += Math.Max(0, (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds);
            position = Math.Clamp(position, 0, Math.Max(duration, 0));
            string key = session.SourceAppUserModelId + "|" + properties?.Title + "|" + properties?.Artist;
            string? art = null;
            if (key != artKey)
            {
                artKey = key;
                art = "";
                if (properties?.Thumbnail != null)
                {
                    using var stream = await properties.Thumbnail.OpenReadAsync();
                    if (stream.Size is > 0 and < 6_000_000)
                    {
                        using var reader = new Windows.Storage.Streams.DataReader(stream);
                        await reader.LoadAsync((uint)stream.Size);
                        byte[] bytes = new byte[(int)stream.Size];
                        reader.ReadBytes(bytes);
                        using var memory = new MemoryStream(bytes);
                        using var image = Image.FromStream(memory);
                        art = DataUrl(CropSquare(image), 256, true);
                    }
                }
            }
            string signature = $"{key}|{playing}|{Math.Round(duration)}|{(playing ? 0 : Math.Round(position))}";
            if (art == null && signature == mediaSignature && playing) { Patch("mediaPos", new { pos = position, at = Now() }); return; }
            mediaSignature = signature;
            Patch("media", new
            {
                has = true, title = string.IsNullOrWhiteSpace(properties?.Title) ? "Без названия" : properties!.Title,
                artist = properties?.Artist ?? "", source = SourceName(session.SourceAppUserModelId),
                playing, pos = position, dur = duration, at = Now(), art, key
            });
        }
        catch (Exception) { mediaSession = null; }
        finally { mediaBusy = false; }
    }

    static Image CropSquare(Image image)
    {
        // Album thumbnails from browsers are often letterboxed 16:9; centre-crop to a square.
        int side = Math.Min(image.Width, image.Height);
        if (image.Width == image.Height) return image;
        var square = new Bitmap(side, side);
        using var g = Graphics.FromImage(square);
        g.DrawImage(image, new Rectangle(0, 0, side, side), new Rectangle((image.Width - side) / 2, (image.Height - side) / 2, side, side), GraphicsUnit.Pixel);
        return square;
    }

    static long Now() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    async void ControlMedia(string command, double fraction)
    {
        var session = mediaSession;
        byte key = command switch { "next" => 0xB0, "prev" => 0xB1, _ => 0xB3 };
        if (session == null) { if (command != "seek") Native.MediaKey(key); return; }
        try
        {
            if (command == "seek")
            {
                var timeline = session.GetTimelineProperties();
                var target = timeline.StartTime + (timeline.EndTime - timeline.StartTime) * Math.Clamp(fraction, 0, 1);
                await session.TryChangePlaybackPositionAsync(target.Ticks);
            }
            else
            {
                bool ok = command switch
                {
                    "next" => await session.TrySkipNextAsync(),
                    "prev" => await session.TrySkipPreviousAsync(),
                    _ => await session.TryTogglePlayPauseAsync()
                };
                if (!ok) Native.MediaKey(key);
            }
        }
        catch (Exception) { Native.MediaKey(key); }
        mediaSignature = null;
        await Task.Delay(120);
        RefreshMedia();
    }

    // ---------------------------------------------------------------- apps

    void PushApps() => Patch("apps", state.Apps.OrderByDescending(a => a.Count).Take(12)
        .Select(a => new { id = a.Path, name = Path.GetFileNameWithoutExtension(a.Path), icon = IconUrl(a.Path) }).ToList());

    void AddApp()
    {
        using var dialog = new OpenFileDialog { Title = "Выберите приложение или ярлык", Filter = "Программы и ярлыки|*.exe;*.lnk;*.url;*.appref-ms|Все файлы|*.*" };
        if (dialog.ShowDialog() == DialogResult.OK) AddAppPath(dialog.FileName);
    }

    void AddAppPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if (!state.Apps.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) state.Apps.Add(new(path));
        Save(); PushApps();
    }

    void Launch(string path)
    {
        var app = state.Apps.FirstOrDefault(a => a.Path == path);
        if (app == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(app.Path) { UseShellExecute = true });
            state.Apps[state.Apps.IndexOf(app)] = app with { Count = app.Count + 1 };
            Save(); PushApps();
        }
        catch (Exception) { Toast("error", "Ярлык недоступен", "Проверьте путь к приложению", "warning-circle", "#ff6b6b"); }
    }

    // ---------------------------------------------------------------- timer

    DateTimeOffset? timerEnd;
    TimeSpan timerPaused, timerTotal;

    DateTimeOffset? timerDone;

    void PushTimer() => Patch("timer", new
    {
        endsAt = timerEnd?.ToUnixTimeMilliseconds(), pausedMs = timerPaused.TotalMilliseconds, totalMs = timerTotal.TotalMilliseconds,
        doneAt = timerDone?.ToUnixTimeMilliseconds()
    });

    static System.Media.SoundPlayer? alarm;
    static System.Media.SoundPlayer? startCue;

    static void PlayStartCue(string file)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "sounds", file);
            if (!File.Exists(path)) return;
            startCue?.Dispose();
            startCue = new System.Media.SoundPlayer(path);
            startCue.Play();
        }
        catch (Exception) { }
    }

    static void PlayTimerSound()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "sounds", "timer-finished-soft.wav");
        try { if (File.Exists(path)) { alarm ??= new System.Media.SoundPlayer(path); alarm.Play(); } else System.Media.SystemSounds.Asterisk.Play(); }
        catch (Exception) { }
    }

    /// Any reaction to a finished timer (ack, +5 min, new timer, reset) silences it.
    static void StopTimerSound() { try { alarm?.Stop(); } catch (Exception) { } }

    void StartTimer(double minutes)
    {
        if (!double.IsFinite(minutes) || minutes is < 1.0 / 60 or > 7 * 1440) return;
        StopTimerSound();
        timerTotal = TimeSpan.FromMinutes(minutes);
        timerEnd = DateTimeOffset.Now + timerTotal;
        timerPaused = TimeSpan.Zero;
        timerDone = null;
        PushTimer();
        if (shownMonitor > 0) PlayStartCue("timer-start.wav");
    }

    void TimerCommand(string command, double minutes)
    {
        if (command == "start") StartTimer(minutes);
        else if (command == "toggle")
        {
            if (timerEnd is { } end) { timerPaused = end - DateTimeOffset.Now; timerEnd = null; }
            else if (timerPaused > TimeSpan.Zero) { timerEnd = DateTimeOffset.Now + timerPaused; timerPaused = TimeSpan.Zero; }
            PushTimer();
        }
        else if (command == "reset") { StopTimerSound(); timerEnd = null; timerDone = null; timerPaused = timerTotal = TimeSpan.Zero; PushTimer(); }
    }

    void CheckTimer()
    {
        if (timerEnd is not { } end || end > DateTimeOffset.Now) return;
        var total = timerTotal;
        timerEnd = null; timerPaused = timerTotal = TimeSpan.Zero;
        timerDone = DateTimeOffset.Now;
        PushTimer();
        if (shownMonitor > 0) PlayTimerSound();
        Toast("timer", "Таймер завершён", total.TotalMinutes >= 1 ? $"{Math.Round(total.TotalMinutes)} мин прошло" : "Время вышло", "bell", "#ffb340",
            [Act("timer5", "+5 мин"), Act("dismiss", null, "check", true)], ms: 30000);
    }

    // ---------------------------------------------------------------- dictation

    Mic? recorder;
    string? recordingPath;
    bool voiceBusy;
    DateTimeOffset recordingStarted;
    long levelSentAt;

    // Where dictated text goes: the window (and whether it had a text field focused) captured
    // while that app still had focus — when the panel opened or the hotkey was pressed.
    nint dictTarget;
    bool dictEditable;
    DateTimeOffset dictTargetAt;

    void PushRec() => Patch("rec", new { state = recorder != null ? "rec" : voiceBusy ? "busy" : "off", startedAt = recordingStarted.ToUnixTimeMilliseconds() });

    /// Remembers the foreground app and whether its focused element accepts typing.
    async Task CaptureDictationTarget()
    {
        var window = Native.GetForegroundWindow();
        if (window == 0 || window == island.Handle) return;
        bool editable = Native.HasCaret(window) || await Task.Run(FocusedIsEditable);
        dictTarget = window; dictEditable = editable; dictTargetAt = DateTimeOffset.Now;
    }

    static bool FocusedIsEditable()
    {
        try { return Uia.FocusedIsEditable(); }
        catch (Exception) { return false; }
    }

    async void VoiceHotkey()
    {
        if (recorder == null && !voiceBusy) await CaptureDictationTarget();
        ToggleVoice();
    }

    async void ToggleVoice()
    {
        if (voiceBusy && recorder == null) return;
        try
        {
            if (recorder == null)
            {
                string engine = Path.Combine(AppContext.BaseDirectory, "speech", "whisper-cli.exe");
                if (!File.Exists(engine) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "speech", "ggml-base.bin")))
                { Toast("error", "Нет распознавания речи", "Не найдена модель в папке speech", "warning-circle", "#ff6b6b"); return; }
                if (DateTimeOffset.Now - dictTargetAt > TimeSpan.FromMinutes(2) || !Native.IsWindow(dictTarget)) dictTarget = 0;
                Directory.CreateDirectory(Store.Folder);
                recordingPath = Path.Combine(Store.Folder, "dictation-" + Guid.NewGuid().ToString("N") + ".wav");
                var mic = new Mic(recordingPath);
                mic.Level += level =>
                {
                    long now = Environment.TickCount64;
                    if (now - levelSentAt < 45) return;
                    levelSentAt = now;
                    island.BeginInvoke(() => island.Send(new { t = "lvl", v = Math.Round(level, 3) }));
                };
                recorder = mic;
                recordingStarted = DateTimeOffset.Now;
                PushRec();
                if (shownMonitor > 0) PlayStartCue("recording-start.wav");
            }
            else
            {
                var mic = recorder;
                recorder = null;
                voiceBusy = true;
                PushRec();
                await Task.Run(mic.Stop);
                mic.Dispose();
                string text = await Transcribe(recordingPath!);
                if (!string.IsNullOrWhiteSpace(text) && dictEditable && dictTarget != 0 && Native.IsWindow(dictTarget))
                {
                    // Paste where the caret was; the text also stays in the clipboard.
                    CopyText(text, false);
                    Native.SetForegroundWindow(dictTarget);
                    await Native.ModifiersUp();
                    await Task.Delay(120);
                    Native.Paste();
                    Toast("voice", text, "Вставлено в " + Native.ProcessName(dictTarget), "microphone", "#34d399", [Act("tab:note", null, "arrow-square-out")], ms: 4000);
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    state = state with { Note = string.IsNullOrWhiteSpace(state.Note) ? text : state.Note.TrimEnd() + Environment.NewLine + text };
                    Save();
                    Patch("note", state.Note);
                    CopyText(text, false);
                    var actions = new List<object> { Act("tab:note", null, "arrow-square-out") };
                    var match = Regex.Match(text, @"(?:запусти|поставь|установи)\s+таймер\s+на\s+(\d{1,4})\s+мин", RegexOptions.IgnoreCase);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int minutes) && minutes is >= 1 and <= 1440)
                        actions.Insert(0, Act("timer:" + minutes, $"Таймер {minutes} мин", null, true));
                    Toast("voice", text, "Текст в заметке и в буфере", "microphone", "#34d399", actions.ToArray(), ms: 7000);
                }
                else Toast("error", "Речь не распознана", "Попробуйте говорить ближе к микрофону", "microphone", "#ff6b6b");
            }
        }
        catch (Exception)
        {
            recorder?.Dispose(); recorder = null;
            Toast("error", "Не удалось записать", "Проверьте доступ к микрофону", "warning-circle", "#ff6b6b", ms: 6000);
        }
        finally
        {
            if (recorder == null)
            {
                voiceBusy = false;
                if (recordingPath != null) { try { File.Delete(recordingPath); } catch (Exception) { } recordingPath = null; }
            }
            PushRec();
        }
    }

    static async Task<string> Transcribe(string path)
    {
        string speech = Path.Combine(AppContext.BaseDirectory, "speech");
        var info = new ProcessStartInfo(Path.Combine(speech, "whisper-cli.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = speech };
        foreach (string argument in new[] { "-m", Path.Combine(speech, "ggml-base.bin"), "-f", path, "-l", "ru", "-otxt", "-of", path, "-nt", "-t", "4",
            // A punctuated example steers whisper into writing commas, periods and capitals.
            "--prompt", "Привет! Сегодня я расскажу, как прошёл мой день. Было холодно, но солнечно, и мы гуляли в парке. А ты как?" })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Speech engine did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(4));
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new IOException("Speech engine failed");
            return File.Exists(path + ".txt") ? (await File.ReadAllTextAsync(path + ".txt")).Trim() : "";
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            if (File.Exists(path + ".txt")) File.Delete(path + ".txt");
        }
    }

    // ---------------------------------------------------------------- colour picker & screenshots

    bool picking;
    DateTimeOffset snipUntil;

    async void PickColor()
    {
        if (picking) return;
        picking = true;
        island.Hide();
        await Task.Delay(140);
        Color? color = null;
        try { color = ColorPicker.Pick(); }
        catch (Exception) { }
        finally
        {
            picking = false;
            if (shownMonitor > 0) { island.Show(); island.Send(new { t = "appear" }); }
        }
        if (color is not { } c) return;
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        CopyText(hex, false);
        var colors = (state.Colors ?? []).Where(x => x != hex).Prepend(hex).Take(6).ToList();
        state = state with { Colors = colors };
        Save();
        Patch("colors", colors);
        Toast("color", hex, $"rgb({c.R}, {c.G}, {c.B})", "", hex, [Act("copy:" + hex, null, "copy")], ms: 6000,
            extra: new { recent = colors.Skip(1).Take(5).ToList() });
    }

    void StartSnip()
    {
        try
        {
            snipUntil = DateTimeOffset.Now.AddMinutes(2);
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
        }
        catch (Exception) { Toast("error", "Ножницы недоступны", "", "warning-circle", "#ff6b6b"); }
    }

    async void FullShot()
    {
        if (shownMonitor <= 0) return;
        var screen = Native.Monitor(shownMonitor).Screen;
        island.Hide();
        await Task.Delay(180);
        Bitmap? shot = null;
        try
        {
            shot = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(shot);
            g.CopyFromScreen(screen.Left, screen.Top, 0, 0, new Size(screen.Width, screen.Height));
        }
        catch (Exception) { shot?.Dispose(); shot = null; }
        finally { if (shownMonitor > 0) { island.Show(); island.Send(new { t = "appear" }); } }
        if (shot == null) { Toast("error", "Захват экрана недоступен", "", "warning-circle", "#ff6b6b"); return; }
        ScreenshotReady(shot);
    }

    /// Takes ownership of <paramref name="shot"/>.
    async void ScreenshotReady(Bitmap shot)
    {
        using var _ = shot;
        string name = "Скриншот " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".png";
        string? key = AddShelfBitmap(shot, name);
        try { Clipboard.SetImage(shot); ownSequence = Native.GetClipboardSequenceNumber(); } catch (Exception) { }
        string thumb = DataUrl(shot, 360, true);
        string? png = ClipPng(shot);
        var seen = await AnalyzeShot(shot);
        // Screenshots join the clipboard history too, so their text is searchable later.
        if (png != null) AddClip(new Clip("Картинки", png, Ocr: seen.Text.Length > 0 ? seen.Text : null));
        var actions = new List<object> { Act("tab:shelf", null, "tray"), Act("shelfFolder", null, "folder-open") };
        if (seen.Text.Length > 0) actions.Insert(0, Act("copy:" + seen.Text, "Текст", "text-t"));
        Toast("shot", "Скриншот готов", (key != null ? "В буфере и на полке" : "В буфере") + $" · {shot.Width}×{shot.Height}", "", "#34d399",
            actions.ToArray(), thumb, 6500);
        if (seen.Qr != null) ShowQr(seen.Qr);
    }

    static string? ClipPng(Image image)
    {
        if ((long)image.Width * image.Height > 6_000_000) return null;
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.Length <= 1_200_000 ? Convert.ToBase64String(stream.ToArray()) : null;
    }

    // ---------------------------------------------------------------- shelf

    void PushShelf()
    {
        var items = new List<object>();
        foreach (var key in state.Shelf ?? [])
        {
            string path = Store.ShelfPath(key);
            bool folder = Directory.Exists(path);
            if (!folder && !File.Exists(path)) continue;
            string ext = folder ? "folder" : Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            bool image = ext is "png" or "jpg" or "jpeg" or "bmp" or "gif" or "webp";
            double age = key.StartsWith("ref:") ? 0 : (DateTime.Now - File.GetCreationTime(path)).TotalHours;
            items.Add(new
            {
                age,
                id = key, name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path, ext,
                size = folder ? -1 : new FileInfo(path).Length,
                thumb = image ? IconUrl(path, true, 120) : folder || ext is "exe" or "lnk" ? IconUrl(path, false, 64) : null
            });
        }
        Patch("shelf", items);
    }

    void AddShelfFiles(List<string> files)
    {
        int added = 0;
        foreach (var file in files)
        {
            if ((state.Shelf?.Count ?? 0) >= 12) break;
            try
            {
                if ((state.Shelf ?? []).Any(k => k == "ref:" + file)) continue;
                var info = new FileInfo(file);
                if (Directory.Exists(file) || (info.Exists && info.Length > 200_000_000))
                {
                    state = state with { Shelf = [.. state.Shelf ?? [], "ref:" + file] };
                    added++;
                    continue;
                }
                if (!info.Exists) continue;
                string key = Guid.NewGuid().ToString("N") + "/" + info.Name;
                string target = Store.ShelfPath(key);
                if (target == "") continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                state = state with { Shelf = [.. state.Shelf ?? [], key] };
                added++;
            }
            catch (Exception) { }
        }
        Save(); PushShelf();
        if (added == 0) Toast("error", "Не получилось добавить", "На полке до 12 элементов", "warning-circle", "#ff6b6b");
    }

    string? AddShelfBitmap(Image bitmap, string name)
    {
        if ((state.Shelf?.Count ?? 0) >= 12) return null;
        string key = Guid.NewGuid().ToString("N") + "/" + name;
        string path = Store.ShelfPath(key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bitmap.Save(path, ImageFormat.Png);
            state = state with { Shelf = [.. state.Shelf ?? [], key] };
            Save(); PushShelf();
            return key;
        }
        catch (Exception) { return null; }
    }

    void PasteToShelf()
    {
        try
        {
            if (Clipboard.ContainsFileDropList()) AddShelfFiles(Clipboard.GetFileDropList().Cast<string>().ToList());
            else if (Clipboard.GetImage() is Image image) using (image) AddShelfBitmap(image, "Картинка " + DateTime.Now.ToString("HH.mm.ss") + ".png");
        }
        catch (Exception) { }
    }

    void RemoveShelf(string key, bool push = true)
    {
        if (state.Shelf == null || !state.Shelf.Contains(key)) return;
        state = state with { Shelf = state.Shelf.Where(k => k != key).ToList() };
        string path = Store.ShelfPath(key);
        if (key.StartsWith("ref:")) { if (push) { Save(); PushShelf(); } return; }
        try
        {
            if (File.Exists(path)) File.Delete(path);
            string? folder = Path.GetDirectoryName(path);
            if (key.Contains('/') && folder != null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception) { }
        if (push) { Save(); PushShelf(); }
    }

    void OpenShelf(string key)
    {
        string path = Store.ShelfPath(key);
        if (File.Exists(path) || Directory.Exists(path)) try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception) { }
    }

    void DragShelf(List<string> keys)
    {
        var paths = keys.Where(k => state.Shelf?.Contains(k) == true).Select(Store.ShelfPath).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (paths.Length == 0 || !Native.MouseDown) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        island.DoDragDrop(data, DragDropEffects.Copy);
        island.Send(new { t = "dragDone" });
    }

    // ---------------------------------------------------------------- clipboard

    void CopyText(string text, bool toast = true)
    {
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            ownSequence = Native.GetClipboardSequenceNumber();
            if (toast) island.Send(new { t = "flash", text = "Скопировано" });
        }
        catch (Exception) { if (toast) island.Send(new { t = "flash", text = "Буфер занят" }); }
    }

    void SetHistory(bool on)
    {
        state = state with { History = on };
        Save();
        if (historyItem != null && historyItem.Checked != on) historyItem.Checked = on;
        PushClips();
    }

    void Capture()
    {
        if (Native.GetClipboardSequenceNumber() == ownSequence) return;
        bool snip = snipUntil > DateTimeOffset.Now;
        if (!state.History && !snip) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null || data.GetFormats(false).Any(format => format is
                "ExcludeClipboardContentFromMonitorProcessing" or "CanIncludeInClipboardHistory" or
                "CanUploadToCloudClipboard" or "Clipboard Viewer Ignore" or "Password")) return;
            if (snip && Clipboard.ContainsImage() && Clipboard.GetImage() is Bitmap snipped)
            {
                snipUntil = default;
                ScreenshotReady(snipped);
                return;
            }
            if (!state.History) return;
            var owner = Native.ProcessName(Native.GetClipboardOwner());
            if (new[] { "keepass", "bitwarden", "1password", "enpass", "dashlane", "lastpass" }
                .Any(name => owner.Contains(name, StringComparison.OrdinalIgnoreCase))) return;
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string value &&
                value.Length is > 0 and <= 16000 && !string.IsNullOrWhiteSpace(value))
                AddClip(new(Store.Classify(value), value));
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is Bitmap image)
            {
                string? png;
                using (image) png = ClipPng(image);
                if (png != null && AddClip(new("Картинки", png)) is { Ocr: null } added) AnalyzeClip(added);
            }
        }
        catch (Exception) { }
    }

    /// Inserts (or moves to the top) a history entry; returns the stored clip.
    Clip? AddClip(Clip clip)
    {
        if (!state.History) return null;
        var original = state.Clips.Find(c => c.Kind == clip.Kind && c.Value == clip.Value);
        if (original != null) { state.Clips.Remove(original); clip = original with { Ocr = original.Ocr ?? clip.Ocr }; }
        clip = clip with { At = DateTimeOffset.Now.ToUnixTimeSeconds() };
        state.Clips.Insert(0, clip);
        ExpireClips();
        long total = state.Clips.Sum(c => (long)c.Value.Length);
        while (state.Clips.Count > 80 || total > 8_000_000)
        {
            int index = state.Clips.FindLastIndex(c => !c.Pinned);
            if (index < 0) break;
            total -= state.Clips[index].Value.Length;
            state.Clips.RemoveAt(index);
        }
        noteSave.Stop(); noteSave.Start();
        PushClips();
        return clip;
    }

    /// Copied images: OCR in the background (search), QR → toast with copy / open.
    async void AnalyzeClip(Clip clip)
    {
        Vision.Result seen;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(clip.Value));
            using var bitmap = new Bitmap(stream);
            seen = await Vision.Analyze(bitmap);
        }
        catch (Exception) { return; }
        int index = state.Clips.FindIndex(c => c.Kind == clip.Kind && c.Value == clip.Value);
        if (index >= 0 && seen.Text.Length > 0)
        {
            state.Clips[index] = state.Clips[index] with { Ocr = seen.Text };
            noteSave.Stop(); noteSave.Start();
            PushClips();
        }
        if (seen.Qr != null) ShowQr(seen.Qr);
    }

    nint lastExternal;
    // Keyed by the clip string instance: no rehashing megabytes of base64 on every push.
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, string> clipThumbRefs = new();

    /// Ctrl+Alt+V: the clipboard lives inside the island, on its own tab.
    void ShowClipboard()
    {
        var fg = Native.GetForegroundWindow();
        if (fg != island.Handle && fg != 0) lastExternal = fg;
        island.Send(new { t = "open", tab = "clip" });
        Native.SetForegroundWindow(island.Handle);
        island.FocusPage();
    }

    /// Thumbnail as a small JPEG file served from https://thumbs.island/ — the page caches it,
    /// so a clipboard update sends a few bytes per image instead of a data URL.
    string ClipThumb(string base64)
    {
        if (clipThumbRefs.TryGetValue(base64, out var url)) return url;
        string name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(base64)))[..20] + ".jpg";
        string path = Path.Combine(Store.Folder, "thumbs", name);
        url = "https://thumbs.island/" + name;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = new MemoryStream(Convert.FromBase64String(base64));
                using var image = Image.FromStream(stream);
                double k = Math.Min(1, 240.0 / Math.Max(image.Width, image.Height));
                using var small = new Bitmap(Math.Max(1, (int)(image.Width * k)), Math.Max(1, (int)(image.Height * k)));
                using (var g = Graphics.FromImage(small))
                {
                    g.Clear(Color.FromArgb(28, 28, 30));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, 0, 0, small.Width, small.Height);
                }
                small.Save(path, ImageFormat.Jpeg);
            }
        }
        catch (Exception) { url = ""; }
        clipThumbRefs.AddOrUpdate(base64, url);
        return url;
    }

    /// Old thumbnails whose clips are gone (history keeps at most 7 days).
    static void CleanThumbs()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Store.Folder, "thumbs")))
                if (DateTime.Now - File.GetLastWriteTime(file) > TimeSpan.FromDays(30)) File.Delete(file);
        }
        catch (Exception) { }
    }

    void PushClips()
    {
        var items = state.Clips.Select((c, i) => new
        {
            i, kind = c.Kind, pinned = c.Pinned,
            text = c.Kind == "Картинки" ? null : c.Value.Length > 1200 ? c.Value[..1200] : c.Value,
            image = c.Kind == "Картинки" ? ClipThumb(c.Value) : null,
            ocr = c.Ocr is { Length: > 2000 } o ? o[..2000] : c.Ocr
        }).OrderByDescending(c => c.pinned).ToList();
        Patch("clips", new { items, history = state.History });
    }

    bool SetClipboard(Clip clip)
    {
        try
        {
            if (clip.Kind == "Картинки")
            {
                using var stream = new MemoryStream(Convert.FromBase64String(clip.Value));
                using var image = Image.FromStream(stream);
                Clipboard.SetImage(image);
            }
            else Clipboard.SetText(clip.Value);
            ownSequence = Native.GetClipboardSequenceNumber();
            return true;
        }
        catch (Exception) { island.Send(new { t = "flash", text = "Буфер занят" }); return false; }
    }

    /// Unpinned clips live 7 days. Clips saved before timestamps existed start their week now.
    bool ExpireClips()
    {
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        for (int i = 0; i < state.Clips.Count; i++)
            if (state.Clips[i].At == 0) state.Clips[i] = state.Clips[i] with { At = now };
        return state.Clips.RemoveAll(c => !c.Pinned && now - c.At > 7 * 86400) > 0;
    }

    /// Right click on the pill: put the newest history entry back and paste it into the app you were in.
    async void PasteLast()
    {
        if (state.Clips.Count > 0 && !SetClipboard(state.Clips[0])) return;
        if (lastExternal == 0 || !Native.IsWindow(lastExternal)) return;
        Native.SetForegroundWindow(lastExternal);
        await Task.Delay(90);
        Native.Paste();
    }

    async void OnClip(string type, JsonElement m)
    {
        int index = m.TryGetProperty("i", out var i) ? i.GetInt32() : -1;
        var clip = index >= 0 && index < state.Clips.Count ? state.Clips[index] : null;
        switch (type)
        {
            case "clipHistory": SetHistory(m.GetProperty("on").GetBoolean()); break;
            case "clipClear": state.Clips.RemoveAll(c => !c.Pinned); Save(); PushClips(); break;
            case "clipPin": if (clip != null) { state.Clips[index] = clip with { Pinned = !clip.Pinned }; Save(); PushClips(); } break;
            case "clipRemove": if (clip != null) { state.Clips.RemoveAt(index); Save(); PushClips(); } break;
            case "clipCopy":
                if (clip != null && SetClipboard(clip)) island.Send(new { t = "flash", text = "Скопировано" });
                break;
            case "clipPaste":
                if (clip == null || !SetClipboard(clip)) break;
                island.Send(new { t = "close" });
                if (lastExternal != 0 && Native.IsWindow(lastExternal))
                {
                    Native.SetForegroundWindow(lastExternal);
                    await Task.Delay(90);
                    Native.Paste();
                }
                break;
            case "clipDrag":
                if (clip == null || !Native.MouseDown) break;
                var data = new DataObject();
                if (clip.Kind == "Картинки")
                {
                    try
                    {
                        string folder = Path.Combine(Store.Folder, "drag");
                        Directory.CreateDirectory(folder);
                        foreach (var old in Directory.GetFiles(folder)) try { File.Delete(old); } catch (Exception) { }
                        string file = Path.Combine(folder, "Картинка " + DateTime.Now.ToString("HH.mm.ss") + ".png");
                        File.WriteAllBytes(file, Convert.FromBase64String(clip.Value));
                        data.SetData(DataFormats.FileDrop, new[] { file });
                    }
                    catch (Exception) { break; }
                }
                else data.SetData(DataFormats.UnicodeText, clip.Value);
                island.DoDragDrop(data, DragDropEffects.Copy);
                island.Send(new { t = "dragDone" });
                break;
        }
    }
    // ---------------------------------------------------------------- currency rates (Coinbase, units per 1 USD; fiat + crypto)

    static readonly System.Net.Http.HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static string RatesPath => Path.Combine(Store.Folder, "rates.json");
    bool ratesBusy;

    void PushRates()
    {
        try { if (File.Exists(RatesPath)) island.Send(new { t = "rates", json = File.ReadAllText(RatesPath), at = new DateTimeOffset(File.GetLastWriteTimeUtc(RatesPath)).ToUnixTimeMilliseconds() }); }
        catch (Exception) { }
    }

    async Task RefreshRates(bool force)
    {
        if (ratesBusy) return;
        if (!force && File.Exists(RatesPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(RatesPath) < TimeSpan.FromMinutes(25)) return;
        ratesBusy = true;
        try
        {
            string body = await http.GetStringAsync("https://api.coinbase.com/v2/exchange-rates?currency=USD");
            using var doc = JsonDocument.Parse(body);
            var rates = doc.RootElement.GetProperty("data").GetProperty("rates");
            Directory.CreateDirectory(Store.Folder);
            File.WriteAllText(RatesPath, rates.GetRawText());
            PushRates();
        }
        catch (Exception) { island.Send(new { t = "ratesError" }); }
        finally { ratesBusy = false; }
    }

    // ---------------------------------------------------------------- audio output switch

    void PushAudio()
    {
        try { island.Send(new { t = "audio", list = AudioDevices.List().Select(d => new { id = d.Id, name = d.Name, on = d.Default }).ToList() }); }
        catch (Exception) { island.Send(new { t = "audio", list = Array.Empty<object>() }); }
    }
}

