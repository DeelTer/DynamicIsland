using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Island;

// Add-on bridge to TikTokChecker (a separate app that serves a tiny API on 127.0.0.1:47821).
// Island polls its event feed and shows toasts; a TikTok/Shorts link copied to the clipboard
// can be sent to TikTokChecker straight from the toast. Nothing happens while TikTokChecker is closed.
sealed partial class IslandApp
{
    const string TtcBase = "http://127.0.0.1:47821";
    static readonly HttpClient ttcHttp = new() { Timeout = TimeSpan.FromSeconds(2) };
    static readonly Regex TtcLink = new(
        @"^https?://(?:(?:www|m|vm|vt)\.)?tiktok\.com/\S+$|^https?://(?:www\.|m\.)?youtube\.com/shorts/[\w-]{11}\S*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    long ttcLast = -1;
    bool ttcOnline;
    string? ttcLastStage;

    void StartTikTokBridge() => Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                using var doc = JsonDocument.Parse(await ttcHttp.GetStringAsync($"{TtcBase}/api/events?since={Math.Max(0, ttcLast)}"));
                var root = doc.RootElement;
                var status = root.GetProperty("status");
                if (ttcLast < 0) ttcLast = status.GetProperty("last_event").GetInt64(); // skip backlog on first contact
                else
                    foreach (var e in root.GetProperty("events").EnumerateArray())
                    {
                        ttcLast = Math.Max(ttcLast, e.GetProperty("id").GetInt64());
                        TtcToast(e);
                    }
                TtcProgress(status);
                ttcOnline = true;
                await Task.Delay(1500);
            }
            catch (Exception)
            {
                ttcOnline = false;
                ttcLast = -1;
                await Task.Delay(6000);
            }
        }
    });

    void TtcToast(JsonElement e)
    {
        string kind = e.GetProperty("kind").GetString() ?? "";
        string title = e.GetProperty("title").GetString() ?? "TikTokChecker";
        string sub = e.GetProperty("sub").GetString() ?? "";
        string? key = e.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        island.BeginInvoke(() =>
        {
            if (kind == "error") Toast("ttc", title, sub, "warning-circle", "#ff453a", [Act("ttc:show", "Открыть")]);
            else if (kind == "done" && key != null) Toast("ttc", title, sub, "check-circle", "#34d399", [Act("ttc:open:" + key, "Открыть", primary: true)]);
            else Toast("ttc", title, sub, "video-camera", "#ff5d8f", [Act("ttc:show", "Открыть", primary: true)], ms: 6000);
        });
    }

    void TtcProgress(JsonElement status)
    {
        // A short toast when the queue starts working, so you know it picked the job up.
        bool running = status.GetProperty("running").GetBoolean();
        string? stage = running ? $"{status.GetProperty("done").GetInt32()}/{status.GetProperty("total").GetInt32()}" : null;
        if (running && ttcLastStage == null)
            island.BeginInvoke(() => Toast("ttc", "TikTokChecker разбирает ролики", "Island сообщит, когда будет готово", "waveform", "#ff5d8f", ms: 3000));
        ttcLastStage = stage;
    }

    // Called for every new text clip.
    void TtcClipboard(string text)
    {
        text = text.Trim();
        if (!ttcOnline || text.Length > 400 || !TtcLink.IsMatch(text)) return;
        Toast("ttc", "Ссылка на ролик", "Разобрать в TikTokChecker?", "video-camera", "#ff5d8f",
            [Act("ttc:add:" + text, "Разобрать", "waveform", primary: true)], ms: 7000);
    }

    bool TtcAction(string id)
    {
        if (!id.StartsWith("ttc:")) return false;
        string body = id[4..];
        if (body.StartsWith("add:")) TtcPost("/api/add", new { text = body[4..], start = true });
        else if (body.StartsWith("open:")) TtcPost("/api/show", new { key = body[5..] });
        else TtcPost("/api/show", new { });
        return true;
    }

    static void TtcPost(string path, object body) => Task.Run(async () =>
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TtcBase + path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-TTC", "1");
            (await ttcHttp.SendAsync(request)).Dispose();
        }
        catch (Exception) { }
    });
}
