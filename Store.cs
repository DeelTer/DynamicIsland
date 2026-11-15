using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Island;

public record Clip(string Kind, string Value, bool Pinned = false, long At = 0);
public record Shortcut(string Path, int Count = 0);
public record State(List<Clip> Clips, List<Shortcut> Apps, List<string> Games, bool History = false,
    string Note = "", List<string>? Shelf = null, string? AnchorMonitor = null, double AnchorX = 0.5,
    bool HoverOpen = true, List<string>? Colors = null, string? Fx = null);

static class Store
{
    public static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandDesktop");
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static State Load()
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(Path.Combine(Folder, "history.json"))) ?? Empty(); }
        catch (Exception) { return Empty(); }
    }

    static State Empty() => new([], [], []);

    public static bool Save(State state)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, "history.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state));
            File.Move(path + ".tmp", path, true);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// Shelf keys: legacy "guid" (PNG) or "guid/filename".
    public static string ShelfPath(string key)
    {
        // "ref:<absolute path>" points at a folder (or a huge file) in place, nothing is copied.
        if (key.StartsWith("ref:")) { string path = key[4..]; return Path.IsPathFullyQualified(path) ? path : ""; }
        var parts = key.Split('/');
        if (!Guid.TryParseExact(parts[0], "N", out _)) return "";
        if (parts.Length == 1) return Path.Combine(Folder, "shelf", parts[0] + ".png");
        if (parts.Length != 2 || parts[1].Length == 0 || parts[1] != Path.GetFileName(parts[1]) || parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "";
        return Path.Combine(Folder, "shelf", parts[0], parts[1]);
    }

    public static string Classify(string value)
    {
        value = value.Trim();
        return !value.Any(char.IsWhiteSpace) && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) ? "Ссылки" : "Текст";
    }
}
