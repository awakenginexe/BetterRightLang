using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using BetterRightLang.Core;

namespace BetterRightLang.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var inTray = Array.Exists(args, a => a == "--tray");
        using var openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BetterRightLang.ShowSettings.v1");
        using var instance = new Mutex(false, @"Local\BetterRightLang.Instance.v1");
        bool first;
        try { first = instance.WaitOne(0); }
        catch (AbandonedMutexException) { first = true; }
        if (!first)
        {
            if (!inTray) openSettings.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        try
        {
            var data = Path.Combine(AppContext.BaseDirectory, "Data");
            var engine = new LayoutEngine(Path.Combine(data, "en.txt"), Path.Combine(data, "th.txt"));
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterRightLang");
            using var app = new TrayApp(engine, appData, openSettings, inTray);
            Application.Run(app);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "BetterRightLang", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { instance.ReleaseMutex(); }
    }
}
