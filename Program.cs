using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Island;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--check"))
        {
            bool ok = Store.Classify("https://example.org") == "Ссылки" && Store.Classify("file:///secret") == "Текст" &&
                Store.Classify("https://example.org\ncomment") == "Текст" && Store.Classify("обычный текст") == "Текст" &&
                Store.ShelfPath("../test") == "" && Store.ShelfPath(Guid.NewGuid().ToString("N") + "/../x") == "" &&
                Store.ShelfPath(Guid.NewGuid().ToString("N") + "/a.pdf") != "";
            Environment.Exit(ok ? 0 : 1);
        }
        if (args.Contains("--audio-check")) // diagnostics: output devices + 1 s of microphone levels
        {
            using var log = new System.IO.StreamWriter(System.IO.Path.Combine(AppContext.BaseDirectory, "audio-check.txt"));
            foreach (var d in AudioDevices.List()) log.WriteLine($"{(d.Default ? "*" : " ")} {d.Name}");
            string wav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "island-mic-check.wav");
            float peak = 0; int frames = 0;
            using (var mic = new Mic(wav)) { mic.Level += v => { peak = Math.Max(peak, v); frames++; }; Thread.Sleep(1000); mic.Stop(); }
            log.WriteLine($"mic frames={frames} peak={peak:F2} wav={new System.IO.FileInfo(wav).Length} bytes");
            System.IO.File.Delete(wav);
            return;
        }
        using var mutex = new Mutex(true, "Local\\IslandDesktop", out bool first);
        if (!first) return;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new IslandApp());
    }
}
