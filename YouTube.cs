using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Island;

/// Right-click on the media card: download the YouTube video (or SoundCloud track) that is playing,
/// via yt-dlp + ffmpeg fetched on demand from their official GitHub releases. Other sources get
/// "find on YouTube" only — DRM services (Spotify, Yandex Music) are deliberately not supported.
sealed partial class IslandApp
{
    static string ToolsDir => Path.Combine(Store.Folder, "tools");
    static string YtDlp => Path.Combine(ToolsDir, "yt-dlp.exe");
    static string Ffmpeg => Path.Combine(ToolsDir, "ffmpeg.exe");
    static bool ToolsReady => File.Exists(YtDlp) && File.Exists(Ffmpeg) && File.Exists(Path.Combine(ToolsDir, "ffprobe.exe"));

    const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip"; // ffmpeg + ffprobe share DLLs: half the size

    static readonly string[] BrowserProcesses = ["msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "browser", "arc"];
    static readonly Regex YoutubeUrl = new(@"^(?:https?://)?(?:www\.|m\.|music\.)?(?:youtube\.com/(?:watch\?|shorts/|live/)|youtu\.be/)\S+", RegexOptions.IgnoreCase);
    static readonly Regex SoundcloudUrl = new(@"^(?:https?://)?(?:www\.|m\.)?soundcloud\.com/[^/\s]+/[^/\s?]+", RegexOptions.IgnoreCase);

    Process? ytJob;
    object? ytProgress;
    string ytTitle = "";
    bool toolsBusy;

    [DllImport("user32")] static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint window, StringBuilder text, int max);
    [DllImport("user32")] static extern bool IsWindowVisible(nint window);
    delegate bool EnumProc(nint window, nint data);

    void MenuState(object state) => island.Send(new { t = "mediaMenu", data = state });

    async void MediaMenu()
    {
        if (ytJob != null) { MenuState(new { state = "busy", title = ytTitle }); return; }
        if (toolsBusy) { MenuState(new { state = "installing" }); return; }
        var session = mediaSession;
        string title = "", artist = "", source = session?.SourceAppUserModelId ?? "";
        try
        {
            var props = session == null ? null : await session.TryGetMediaPropertiesAsync();
            title = props?.Title ?? ""; artist = props?.Artist ?? "";
        }
        catch (Exception) { }
        string query = (artist + " " + title).Trim();
        bool browser = BrowserProcesses.Any(b => source.Contains(b, StringComparison.OrdinalIgnoreCase));
        if (!browser || title.Length == 0) { MenuState(new { state = "search", query }); return; }

        MenuState(new { state = "loading" });
        var (url, youtubeTab) = await Task.Run(() => FindMediaUrl(title));
        bool searched = false;
        if (url == null)
        {
            // The YouTube tab is not the active one in its window: find the video by its title.
            if (!youtubeTab) { MenuState(new { state = "search", query }); return; }
            url = "ytsearch1:" + query;
            searched = true;
        }
        if (!ToolsReady) { MenuState(new { state = "install" }); return; }
        UpdateYtDlpWeekly();
        var info = await Task.Run(() => Probe(url));
        if (info == null) { MenuState(new { state = "error", message = "Не удалось получить видео. Попробуйте ещё раз." }); return; }
        MenuState(new { state = "ready", info.Value.Title, info.Value.Url, info.Value.Heights, searched, audioOnly = info.Value.Heights.Count == 0 });
    }

    /// Address bar of browser windows (active tab only), preferring the window whose title matches the track.
    static (string? Url, bool YoutubeTab) FindMediaUrl(string mediaTitle)
    {
        var windows = new List<(nint Window, string Title)>();
        EnumWindows((w, _) =>
        {
            if (!IsWindowVisible(w)) return true;
            var text = new StringBuilder(512);
            if (GetWindowText(w, text, 512) == 0) return true;
            string process = Native.ProcessName(w);
            if (BrowserProcesses.Any(b => process.Equals(b, StringComparison.OrdinalIgnoreCase))) windows.Add((w, text.ToString()));
            return true;
        }, 0);
        bool youtubeTab = windows.Any(w => w.Title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) || w.Title.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase));
        string key = mediaTitle.Length > 24 ? mediaTitle[..24] : mediaTitle;
        foreach (var (window, _) in windows.OrderByDescending(w => w.Title.Contains(key, StringComparison.OrdinalIgnoreCase)).Take(4))
        {
            string? address;
            try { address = Uia.AddressBar(window)?.Trim(); } catch (Exception) { continue; }
            if (string.IsNullOrEmpty(address)) continue;
            if (YoutubeUrl.IsMatch(address) || SoundcloudUrl.IsMatch(address))
                return (address.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? address : "https://" + address, true);
        }
        return (null, youtubeTab);
    }

    internal static string UrlProbe()
    {
        var lines = new List<string>();
        EnumWindows((w, _) =>
        {
            if (!IsWindowVisible(w)) return true;
            var text = new StringBuilder(512);
            if (GetWindowText(w, text, 512) == 0) return true;
            string process = Native.ProcessName(w);
            if (!BrowserProcesses.Any(b => process.Equals(b, StringComparison.OrdinalIgnoreCase))) return true;
            var watch = Stopwatch.StartNew();
            string? address = null;
            try { address = Uia.AddressBar(w); } catch (Exception e) { address = "!" + e.GetType().Name; }
            string host = address == null ? "(none)" : Regex.Match(address, @"^(?:https?://)?([^/\s]+)").Groups[1].Value;
            lines.Add($"{process}: host={host} youtube={(address != null && YoutubeUrl.IsMatch(address))} {watch.ElapsedMilliseconds} ms");
            return true;
        }, 0);
        return string.Join("\n", lines);
    }

    record struct VideoInfo(string Title, string Url, List<int> Heights);

    static ProcessStartInfo YtStart(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(YtDlp)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = ToolsDir
        };
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in args) info.ArgumentList.Add(a);
        return info;
    }

    static VideoInfo? Probe(string url)
    {
        try
        {
            using var p = Process.Start(YtStart(["-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8", url]))!;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { p.Kill(true); return null; }
            using var doc = JsonDocument.Parse(output.Result);
            var root = doc.RootElement;
            if (root.TryGetProperty("entries", out var entries)) // ytsearch1: wraps the hit in a playlist
            {
                if (entries.GetArrayLength() == 0) return null;
                root = entries[0];
            }
            var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
            if (root.TryGetProperty("formats", out var formats))
                foreach (var f in formats.EnumerateArray())
                    if (f.TryGetProperty("vcodec", out var v) && v.GetString() is { } codec && codec != "none" &&
                        f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() >= 144)
                        heights.Add(StandardHeight(h.GetInt32()));
            return new VideoInfo(root.GetProperty("title").GetString() ?? "", root.GetProperty("webpage_url").GetString() ?? url, heights.Take(7).ToList());
        }
        catch (Exception) { return null; }
    }

    // Vertical / odd sizes (e.g. 1080×1350) are bucketed to the usual ladder.
    static int StandardHeight(int h) => new[] { 4320, 2160, 1440, 1080, 720, 480, 360, 240, 144 }.FirstOrDefault(s => h >= s * 0.9, 144);

    void UpdateYtDlpWeekly()
    {
        if (DateTime.Now - File.GetLastWriteTime(YtDlp) < TimeSpan.FromDays(7)) return;
        Task.Run(() =>
        {
            try { using var p = Process.Start(YtStart(["-U"])); p?.WaitForExit(120000); File.SetLastWriteTime(YtDlp, DateTime.Now); }
            catch (Exception) { }
        });
    }

    async void InstallTools()
    {
        if (toolsBusy || ToolsReady) return;
        toolsBusy = true;
        MenuState(new { state = "installing" });
        try
        {
            Directory.CreateDirectory(ToolsDir);
            if (!File.Exists(YtDlp)) await Fetch(YtDlpUrl, YtDlp, "yt-dlp");
            if (!File.Exists(Ffmpeg) || !File.Exists(Path.Combine(ToolsDir, "ffprobe.exe")))
            {
                string zip = Path.Combine(ToolsDir, "ffmpeg.zip");
                await Fetch(FfmpegUrl, zip, "ffmpeg");
                ytProgress = new { name = "Распаковка ffmpeg", bytes = 0, speed = 0, count = 1, pct = (double?)null };
                PushDownloads();
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(zip);
                    foreach (var entry in archive.Entries)
                        if (entry.FullName.Contains("/bin/") && (entry.Name is "ffmpeg.exe" or "ffprobe.exe" || entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                            entry.ExtractToFile(Path.Combine(ToolsDir, entry.Name), true);
                });
                File.Delete(zip);
            }
            Toast("dl", "yt-dlp и ffmpeg установлены", "Нажмите ПКМ по карточке видео ещё раз", "check-circle", "#34d399");
        }
        catch (Exception)
        {
            Toast("error", "Не удалось скачать инструменты", "Проверьте интернет и попробуйте снова", "warning-circle", "#ff6b6b", ms: 6000);
        }
        finally { toolsBusy = false; ytProgress = null; PushDownloads(); }
    }

    async Task Fetch(string url, string target, string label)
    {
        using var response = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0, done = 0, lastBytes = 0;
        var watch = Stopwatch.StartNew();
        await using (var input = await response.Content.ReadAsStreamAsync())
        await using (var output = File.Create(target + ".part"))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (watch.ElapsedMilliseconds >= 400)
                {
                    double speed = (done - lastBytes) / watch.Elapsed.TotalSeconds;
                    lastBytes = done; watch.Restart();
                    ytProgress = new { name = "Установка " + label, bytes = done, speed = Math.Round(speed), count = 1, pct = total > 0 ? Math.Round(done * 100.0 / total, 1) : (double?)null };
                    PushDownloads();
                }
            }
        }
        File.Move(target + ".part", target, true);
    }

    void StartYt(string url, string quality)
    {
        if (ytJob != null || !ToolsReady || downloadsFolder == null) return;
        // Only ever hand yt-dlp a real YouTube / SoundCloud page URL (never an option-like string).
        if (!(YoutubeUrl.IsMatch(url) || SoundcloudUrl.IsMatch(url)) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        bool audio = quality == "audio";
        var args = new List<string> { "--no-playlist", "--newline", "--progress", "--no-warnings", "--encoding", "utf-8", "--windows-filenames", "--no-mtime",
            "--ffmpeg-location", ToolsDir, "-P", downloadsFolder, "-P", "temp:" + Path.Combine(ToolsDir, "tmp"),
            "--progress-template", "download:[ISL]%(progress._percent_str)s|%(progress.speed)s|%(info.title)s",
            "--print", "after_move:[ISLDONE]%(filepath)s" };
        if (audio) args.AddRange(["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0", "--embed-metadata", "-o", "%(title).150B.%(ext)s"]);
        else
        {
            int h = int.TryParse(quality, out var q) ? q : 1080;
            // Prefer H.264 + AAC where it exists (plays everywhere); 1440p+ only exists as VP9/AV1.
            args.AddRange(["-f", $"bv*[height<={h}]+ba/b[height<={h}]", "-S", $"res:{h},vcodec:avc1,acodec:m4a",
                "--merge-output-format", "mp4", "-o", "%(title).150B [%(height)sp].%(ext)s"]);
        }
        args.Add(url);
        try
        {
            var p = Process.Start(YtStart(args))!;
            ytJob = p;
            ytTitle = "Видео";
            ytProgress = new { name = ytTitle, bytes = 0, speed = 0, count = 1, pct = (double?)0 };
            PushDownloads();
            string? finalPath = null, lastError = null;
            int stream = 1;
            double lastPct = 0;
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && e.Data.Contains("ERROR")) lastError = e.Data; };
            p.OutputDataReceived += (_, e) =>
            {
                string? line = e.Data;
                if (line == null) return;
                if (line.StartsWith("[ISLDONE]")) { finalPath = line[9..].Trim(); return; }
                int at = line.IndexOf("[ISL]", StringComparison.Ordinal);
                if (at < 0) { if (line.Contains("[Merger]") || line.Contains("[ExtractAudio]")) island.BeginInvoke(() => { ytProgress = new { name = "Склеиваю…", bytes = 0, speed = 0, count = 1, pct = (double?)100 }; PushDownloads(); }); return; }
                var parts = line[(at + 5)..].Split('|', 3);
                if (parts.Length < 3) return;
                double.TryParse(parts[0].Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pct);
                double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double speed);
                if (pct + 50 < lastPct) stream++; // video finished, the audio stream starts from 0 %
                lastPct = pct;
                string title = parts[2].Trim();
                island.BeginInvoke(() =>
                {
                    ytTitle = title;
                    ytProgress = new { name = title, bytes = 0, speed = Math.Round(speed), count = 1, pct = (double?)Math.Round(pct, 1), part = !audio && stream > 1 ? "звук" : null };
                    PushDownloads();
                });
            };
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => island.BeginInvoke(() =>
            {
                bool cancelled = ytJob == null;
                ytJob = null; ytProgress = null; PushDownloads();
                if (finalPath != null && File.Exists(finalPath)) DownloadFinished(finalPath);
                else if (!cancelled) Toast("error", "Не удалось скачать", Shorten(lastError ?? "yt-dlp завершился с ошибкой"), "warning-circle", "#ff6b6b", ms: 7000);
                p.Dispose();
            });
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch (Exception) { ytJob = null; ytProgress = null; PushDownloads(); }
    }

    static string Shorten(string text) => (text = text.Replace("ERROR:", "").Trim()).Length > 110 ? text[..110] + "…" : text;

    void CancelYt()
    {
        var p = ytJob;
        ytJob = null;
        try { p?.Kill(true); } catch (Exception) { }
        Toast("dl", "Загрузка отменена", ytTitle, "x-circle", "#8e8e93");
    }

    static void SearchYoutube(string query) =>
        Shell("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query));
}
