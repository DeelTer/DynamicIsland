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
        using var mutex = new Mutex(true, "Local\\IslandDesktop", out bool first);
        if (!first) return;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new IslandApp());
    }
}
