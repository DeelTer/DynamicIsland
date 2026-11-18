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
        if (args.Contains("--vision-check")) // diagnostics: OCR + QR on a generated image
        {
            using var image = new System.Drawing.Bitmap(900, 360);
            using (var g = System.Drawing.Graphics.FromImage(image))
            {
                g.Clear(System.Drawing.Color.White);
                using var font = new System.Drawing.Font("Segoe UI", 22);
                g.DrawString("Привет, мир! Счёт №1234 от 18.11.2026", font, System.Drawing.Brushes.Black, 20, 30);
                g.DrawString("Island screenshot OCR test", font, System.Drawing.Brushes.Black, 20, 80);
                var writer = new ZXing.BarcodeWriterPixelData { Format = ZXing.BarcodeFormat.QR_CODE, Options = new ZXing.Common.EncodingOptions { Width = 200, Height = 200, Margin = 1 } };
                var qr = writer.Write("https://github.com/");
                using var qrBitmap = new System.Drawing.Bitmap(qr.Width, qr.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var bits = qrBitmap.LockBits(new System.Drawing.Rectangle(0, 0, qr.Width, qr.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                System.Runtime.InteropServices.Marshal.Copy(qr.Pixels, 0, bits.Scan0, qr.Pixels.Length);
                qrBitmap.UnlockBits(bits);
                g.DrawImage(qrBitmap, 660, 140);
            }
            var result = Vision.Analyze(image).GetAwaiter().GetResult();
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "vision-check.txt"), $"QR: {result.Qr}\nOCR:\n{result.Text}\n");
            return;
        }
        if (args.Contains("--url-check")) // diagnostics: can the browser address bar be read? (host only)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "url-check.txt"), IslandApp.UrlProbe());
            return;
        }
        if (args.Contains("--notify-check")) // diagnostics: parse the last messenger notifications (no content printed)
        {
            var report = IslandApp.NotifyProbe();
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "notify-check.txt"), report);
            return;
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
