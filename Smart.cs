using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace Island;

/// Image understanding: text (Windows OCR, offline) and QR codes (ZXing).
static class Vision
{
    public record Result(string Text, string? Qr);

    static Windows.Media.Ocr.OcrEngine? engine;

    static Windows.Media.Ocr.OcrEngine? Engine()
    {
        if (engine != null) return engine;
        // Russian also reads Latin; fall back to whatever the user profile offers.
        var ru = new Windows.Globalization.Language("ru");
        engine = Windows.Media.Ocr.OcrEngine.IsLanguageSupported(ru)
            ? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(ru)
            : Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        return engine;
    }

    /// Runs off the UI thread. Never throws; empty text when nothing is found.
    public static async Task<Result> Analyze(Bitmap source)
    {
        Bitmap bitmap;
        lock (source) bitmap = new Bitmap(source);
        using (bitmap)
        {
            string? qr = null;
            try { qr = ReadQr(bitmap); } catch (Exception) { }
            string text = "";
            try { text = await Ocr(bitmap); } catch (Exception) { }
            return new Result(text, qr);
        }
    }

    static string? ReadQr(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels;
        try
        {
            pixels = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally { bitmap.UnlockBits(data); }
        var luminance = new ZXing.RGBLuminanceSource(pixels, bitmap.Width, bitmap.Height, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new ZXing.BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = { TryHarder = true, PossibleFormats = [ZXing.BarcodeFormat.QR_CODE] }
        };
        return reader.Decode(luminance)?.Text;
    }

    static async Task<string> Ocr(Bitmap bitmap)
    {
        var ocr = Engine();
        if (ocr == null) return "";
        // Small UI text reads far better upscaled; huge screenshots are capped at the engine limit.
        double scale = Math.Min(2.0, (double)Windows.Media.Ocr.OcrEngine.MaxImageDimension / Math.Max(bitmap.Width, bitmap.Height));
        if (Math.Max(bitmap.Width, bitmap.Height) > 1400) scale = Math.Min(scale, 1.0);
        using var scaled = Math.Abs(scale - 1) < 0.01 ? new Bitmap(bitmap) : new Bitmap(bitmap, (int)(bitmap.Width * scale), (int)(bitmap.Height * scale));
        using var stream = new MemoryStream();
        scaled.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        using var soft = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        var result = await ocr.RecognizeAsync(soft);
        return string.Join("\n", result.Lines.Select(l => l.Text)).Trim();
    }
}

sealed partial class IslandApp
{
    // ---------------------------------------------------------------- downloads

    FileSystemWatcher? downloadWatcher;
    readonly Dictionary<string, (long Size, DateTime At, double Speed)> downloads = new(StringComparer.OrdinalIgnoreCase);
    string? downloadsFolder;
    string lastDlSignature = "";

    static readonly string[] PartialExtensions = [".crdownload", ".part", ".partial", ".download"];

    void StartDownloadWatch()
    {
        try
        {
            downloadsFolder = Native.KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"));
            if (downloadsFolder == null || !Directory.Exists(downloadsFolder)) return;
            downloadWatcher = new FileSystemWatcher(downloadsFolder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
            downloadWatcher.Created += (_, e) => island.BeginInvoke(() => OnDownloadFile(e.FullPath));
            downloadWatcher.Renamed += (_, e) => island.BeginInvoke(() => OnDownloadRenamed(e.OldFullPath, e.FullPath));
            downloadWatcher.EnableRaisingEvents = true;
        }
        catch (Exception) { }
    }

    static bool IsPartial(string path) => PartialExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    void OnDownloadFile(string path)
    {
        if (IsPartial(path) && !downloads.ContainsKey(path)) downloads[path] = (0, DateTime.UtcNow, 0);
    }

    void OnDownloadRenamed(string from, string to)
    {
        if (downloads.Remove(from)) { }
        if (IsPartial(to)) { downloads[to] = (0, DateTime.UtcNow, 0); return; }
        // Partial file renamed to its final name → finished.
        if (IsPartial(from) && File.Exists(to)) DownloadFinished(to);
        PushDownloads();
    }

    /// Called from Tick every 500 ms while something downloads.
    void PollDownloads()
    {
        if (downloads.Count == 0) return;
        foreach (var path in downloads.Keys.ToList())
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                downloads.Remove(path);
                // Firefox deletes the .part and writes the final file next to it.
                string final = path[..^Path.GetExtension(path).Length];
                if (File.Exists(final) && (DateTime.Now - File.GetLastWriteTime(final)).TotalSeconds < 10) DownloadFinished(final);
                continue;
            }
            var (size, at, speed) = downloads[path];
            var now = DateTime.UtcNow;
            double dt = (now - at).TotalSeconds;
            if (dt >= 0.4)
            {
                double instant = Math.Max(0, info.Length - size) / dt;
                downloads[path] = (info.Length, now, size == 0 ? instant : speed * 0.6 + instant * 0.4);
            }
        }
        PushDownloads();
    }

    void PushDownloads()
    {
        var active = downloads.Where(d => d.Value.Size > 0).ToList();
        object? payload = null;
        if (active.Count > 0)
        {
            var main = active.OrderByDescending(d => d.Value.Speed).First();
            string name = Path.GetFileNameWithoutExtension(main.Key);
            if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase)) name = "Загрузка";
            payload = new { name, bytes = active.Sum(d => d.Value.Size), speed = Math.Round(active.Sum(d => d.Value.Speed)), count = active.Count };
        }
        string signature = payload == null ? "" : System.Text.Json.JsonSerializer.Serialize(payload);
        if (signature == lastDlSignature) return;
        lastDlSignature = signature;
        Patch("dl", payload);
    }

    void DownloadFinished(string path)
    {
        if (!File.Exists(path)) return;
        string name = Path.GetFileName(path);
        var size = new FileInfo(path).Length;
        string sub = size >= 1 << 20 ? $"{size / 1048576.0:0.#} МБ" : $"{Math.Max(1, size / 1024)} КБ";
        string? thumb = null;
        try { thumb = IconUrl(path, Regex.IsMatch(name, @"\.(png|jpe?g|gif|webp|bmp)$", RegexOptions.IgnoreCase), 96); } catch (Exception) { }
        Toast("dl", name, "Загружено · " + sub, "download-simple", "#4aa3ff",
            [Act("open:" + path, "Открыть", null, true), Act("reveal:" + path, null, "folder-open"), Act("shelfAdd:" + path, null, "tray")], thumb, 6500);
    }

    // ---------------------------------------------------------------- messenger notifications

    static readonly (string Match, string App, string Process, string Color)[] Messengers =
    [
        ("telegram", "Telegram", "Telegram", "#2aabee"),
        ("discord", "Discord", "Discord", "#5865f2"),
        ("whatsapp", "WhatsApp", "WhatsApp", "#25d366"),
    ];

    FileSystemWatcher? notifyWatcher;
    System.Windows.Forms.Timer? notifyDebounce;
    long notifyLastId = -1;
    DateTime notifyBurstAt;
    int notifyBurst;

    static string NotifyDb => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    void StartNotifyWatch()
    {
        if (!File.Exists(NotifyDb)) return;
        notifyLastId = QueryNotifications(-1).LastId; // never replay old notifications
        notifyDebounce = new System.Windows.Forms.Timer { Interval = 350 };
        notifyDebounce.Tick += (_, _) => { notifyDebounce.Stop(); CheckNotifications(); };
        try
        {
            notifyWatcher = new FileSystemWatcher(Path.GetDirectoryName(NotifyDb)!, "wpndatabase.db*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
            notifyWatcher.Changed += (_, _) => island.BeginInvoke(() => { notifyDebounce.Stop(); notifyDebounce.Start(); });
            notifyWatcher.EnableRaisingEvents = true;
        }
        catch (Exception) { }
    }

    record Incoming(string App, string Process, string Color, string Title, string Body, string? Avatar);

    static (long LastId, List<Incoming> Items) QueryNotifications(long after)
    {
        var items = new List<Incoming>();
        long last = after;
        try
        {
            using var db = new SqliteConnection($"Data Source={NotifyDb};Mode=ReadOnly;Cache=Shared");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = after < 0
                ? "SELECT IFNULL(MAX(Id),0) FROM Notification"
                : "SELECT n.Id, h.PrimaryId, n.Payload FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId WHERE n.Type = 'toast' AND n.Id > $after ORDER BY n.Id LIMIT 20";
            if (after < 0) return (Convert.ToInt64(cmd.ExecuteScalar()), items);
            cmd.Parameters.AddWithValue("$after", after);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                last = Math.Max(last, reader.GetInt64(0));
                string app = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var messenger = Messengers.FirstOrDefault(m => app.Contains(m.Match, StringComparison.OrdinalIgnoreCase));
                if (messenger.App == null || reader.IsDBNull(2)) continue;
                string xml = reader.GetValue(2) is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : reader.GetString(2);
                try
                {
                    var toast = XDocument.Parse(xml);
                    var texts = toast.Descendants("text").Select(t => t.Value.Trim()).Where(t => t.Length > 0).ToList();
                    if (texts.Count == 0) continue;
                    string? image = toast.Descendants("image").Select(i => (string?)i.Attribute("src")).FirstOrDefault(s => s != null);
                    items.Add(new Incoming(messenger.App, messenger.Process, messenger.Color, texts[0], string.Join(" · ", texts.Skip(1)), image));
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return (last, items);
    }

    internal static string NotifyProbe()
    {
        long last = QueryNotifications(-1).LastId;
        var (_, items) = QueryNotifications(Math.Max(0, last - 400));
        return $"lastId={last} messenger toasts in last 400: {items.Count}\n" +
            string.Join("\n", items.TakeLast(5).Select(i => $"{i.App}: title {i.Title.Length} chars, body {i.Body.Length} chars, avatar {(i.Avatar == null ? "none" : i.Avatar.Split(':')[0])}"));
    }

    async void CheckNotifications()
    {
        if (notifyLastId < 0 || !state.Notify) return;
        long after = notifyLastId;
        var (last, items) = await Task.Run(() => QueryNotifications(after));
        notifyLastId = Math.Max(notifyLastId, last);
        // Stay out of the way: nothing while hidden (games / fullscreen), dictating or picking.
        if (items.Count == 0 || shownMonitor <= 0 || recorder != null || picking) return;
        var now = DateTime.UtcNow;
        notifyBurst = (now - notifyBurstAt).TotalSeconds < 6 ? notifyBurst + items.Count : items.Count;
        notifyBurstAt = now;
        var item = items[^1];
        string title = item.Title, body = state.NotifyText ? item.Body : "Новое сообщение";
        if (notifyBurst > 1 && items.Select(i => i.Title).Distinct().Count() > 1) { title = item.App; body = $"{notifyBurst} новых сообщения"; }
        string? avatar = null;
        try
        {
            string? path = item.Avatar;
            if (path != null && Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
            if (path != null && File.Exists(path)) { using var img = Image.FromFile(path); avatar = DataUrl(img, 72); }
        }
        catch (Exception) { }
        Toast("msg", title, body, "chat-circle-dots", item.Color, [Act("app:" + item.Process)], avatar, 4500, new { app = item.App });
    }

    static void FocusApp(string process)
    {
        foreach (var p in Process.GetProcessesByName(process))
        {
            var window = p.MainWindowHandle;
            if (window == 0) continue;
            Native.ShowWindow(window, Native.IsIconic(window) ? 9 : 5);
            Native.SetForegroundWindow(window);
            return;
        }
    }

    // ---------------------------------------------------------------- image analysis results

    /// Screenshots: OCR + QR before the toast so it can offer "copy text" / "open link".
    async Task<Vision.Result> AnalyzeShot(Bitmap shot)
    {
        var work = Vision.Analyze(shot);
        var done = await Task.WhenAny(work, Task.Delay(2500));
        return done == work ? work.Result : new Vision.Result("", null);
    }

    void ShowQr(string qr)
    {
        bool link = Uri.TryCreate(qr, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var actions = new List<object> { Act("copy:" + qr, null, "copy") };
        if (link) actions.Insert(0, Act("url:" + qr, null, "arrow-square-out", true));
        Toast("qr", link ? uri!.Host : "QR-код", qr.Length > 120 ? qr[..120] + "…" : qr, "qr-code", "#ffd60a", actions.ToArray(), ms: 9000);
    }
}
