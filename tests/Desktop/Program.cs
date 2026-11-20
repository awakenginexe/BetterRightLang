using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using BetterRightLang.Core;
using BetterRightLang.Desktop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var directory = Directory.CreateTempSubdirectory("brl-settings-");
        try
        {
            var file = Path.Combine(directory.FullName, "nested", "settings.json");
            Check(Preferences.Load(file).Mode == CorrectionMode.Auto, "first run uses Auto");
            var chosen = new Preferences(CorrectionMode.Manual, "th");
            chosen.Save(file);
            var restored = Preferences.Load(file);
            Check(restored.Mode == chosen.Mode && restored.Language == chosen.Language && restored.Whitelist.Length == 0, "mode and UI language round trip");
            File.WriteAllText(file, "{\"mode\":\"Manual\",\"language\":\"th\"}");
            Check(Preferences.Load(file).Whitelist.Length == 0 && Preferences.Load(file).Mode == CorrectionMode.Manual, "earlier settings load with an empty whitelist");
            File.WriteAllText(file, "{\"mode\":\"unknown\",\"language\":\"el\"}");
            Check(Preferences.Load(file).Mode == CorrectionMode.Auto, "unsupported setting recovers");
            File.WriteAllText(file, "{");
            Check(Preferences.Load(file).Mode == CorrectionMode.Auto, "interrupted settings recover");
            Console.WriteLine("Passed settings checks.");
            CheckTrayFeedback();
            var bounded = new CorrectionHistory();
            for (var i = 0; i <= CorrectionHistory.Capacity; i++) bounded.Record($"a{i}", $"b{i}", true);
            Check(bounded.NewestFirst().Length == 100 && bounded.NewestFirst()[0].Original == "a100" &&
                bounded.NewestFirst()[^1].Original == "a1", "bounded history drops the oldest correction");
            CheckWebView(directory.FullName, args.FirstOrDefault());
        }
        finally
        {
            // WebView2 can release its disposable test profile after the last control is closed.
            for (var i = 0; i < 40; i++)
            {
                try { directory.Delete(true); break; }
                catch (IOException) { System.Threading.Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(100); }
            }
        }
    }

    private static void CheckTrayFeedback()
    {
        foreach (var mode in Enum.GetValues<CorrectionMode>())
        foreach (var size in new[] { 16, 20, 32, 48 })
        {
            using var icon = TrayApp.LoadModeIcon(mode, new Size(size, size));
            using var pixels = icon.ToBitmap();
            var accents = 0;
            for (var y = 0; y < pixels.Height; y++)
            for (var x = 0; x < pixels.Width; x++)
            {
                var c = pixels.GetPixel(x, y);
                if (c.A < 200) continue;
                var hasAccent = mode switch
                {
                    CorrectionMode.Auto => c.G > 145 && c.G > c.R + 55 && c.G > c.B + 40,
                    CorrectionMode.Manual => c.R > 185 && c.G > 130 && c.R > c.G && c.G > c.B + 80,
                    CorrectionMode.Paused => c.R > 135 && Math.Abs(c.R - c.B) < 15 && Math.Abs(c.R - c.G) < 15,
                    _ => false
                };
                if (hasAccent) accents++;
            }
            Check(pixels.Size == new Size(size, size) && accents > size * size / 12, $"{mode} tray color at {size}px");
        }
        var data = Path.Combine(AppContext.BaseDirectory, "Data");
        var engine = new LayoutEngine(Path.Combine(data, "en.txt"), Path.Combine(data, "th.txt"));
        foreach (var (word, language, notice) in new[]
        {
            ("l;ylfurujg0wf", "en", "English → Thai"), ("แนพพำแะ", "en", "Thai → English"),
            ("2", "th", "อังกฤษ → ไทย"), ("ส/", "th", "ไทย → อังกฤษ"),
            ("helloสวัสดี", "en", "English ⇄ Thai"), ("hello  สวัสดี", "th", "อังกฤษ ⇄ ไทย")
        })
            Check(TrayApp.CorrectionNotice(word, engine.Convert(word), language) == notice, $"direction for {word}");
        Check(TrayApp.CorrectionNotice("hello", "hello", "en") is null, "no notice for an unchanged word");
        Console.WriteLine("Passed per-mode tray icons and localized correction directions.");
    }

    private static void CheckWebView(string folder, string? screenshot)
    {
        var state = new WindowState("Auto", "en", false, null);
        var entries = new CorrectionHistory();
        var whitelist = WordWhitelist.Empty;
        var file = Path.Combine(folder, "history-test-settings.json");
        SettingsWindow? active = null;
        using var window = new SettingsWindow(folder, () => state,
            mode => state = state with { Mode = mode.ToString() },
            lang => state = state with { Language = lang },
            start => state = state with { Startup = start },
            () => new(entries.NewestFirst(), whitelist.Words), (word, add) =>
            {
                whitelist = whitelist.With(word, add);
                new Preferences(CorrectionMode.Auto, "en") { Whitelist = whitelist.Words.ToArray() }.Save(file);
                active!.UpdateHistory();
            });
        active = window;
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-12000, -12000);
        window.ShowInTaskbar = false;
        Exception? failure = null;
        window.Shown += async (_, _) =>
        {
            try
            {
                var browser = window.Controls.OfType<WebView2>().Single();
                var expires = DateTime.UtcNow.AddSeconds(30);
                while ((!browser.Visible || browser.CoreWebView2 is null) && DateTime.UtcNow < expires) await Task.Delay(75);
                Check(browser.CoreWebView2 is not null && browser.Visible, "local WebView2 settings opened");
                var web = browser.CoreWebView2!;
                async Task<string?> Read(string js) => JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync(js));
                async Task Until(Func<Task<bool>> check, string label)
                {
                    for (var i = 0; i < 100; i++)
                    {
                        if (await check()) return;
                        await Task.Delay(25);
                    }
                    throw new InvalidOperationException(label);
                }
                Check(await Read("document.querySelector('#sample').value") == "", "practice starts empty");
                Check(await web.ExecuteScriptAsync("!!document.querySelector('label[for=sample]') && !document.querySelector('#sample').disabled") == "true", "practice field is labeled and editable");
                await web.ExecuteScriptAsync("document.querySelector('#sample').setRangeText('hello')");
                Check(await Read("document.querySelector('#sample').value") == "hello", "practice accepts editing");
                Check(await web.ExecuteScriptAsync("!document.querySelector('#auto-output, #manual-output')") == "true", "no simulated results");
                if (screenshot is not null)
                {
                    using var output = File.Create(screenshot);
                    await web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                }
                foreach (var mode in new[] { "Manual", "Paused", "Auto" })
                {
                    await web.ExecuteScriptAsync($"document.querySelector('input[value={mode}]').click()");
                    await Until(() => Task.FromResult(state.Mode == mode), "mode change " + mode);
                    window.UpdateState();
                }
                await web.ExecuteScriptAsync("document.querySelector('#startup').click()");
                await Until(() => Task.FromResult(state.Startup), "startup switch");
                await web.ExecuteScriptAsync("let lang=document.querySelector('#language'); lang.value='th'; lang.dispatchEvent(new Event('change'))");
                await Until(() => Task.FromResult(state.Language == "th"), "language change");
                window.UpdateState();
                await Until(async () => await Read("document.querySelector('legend').textContent") == "โหมดการแก้ไข", "Thai UI");
                Check(await Read("document.querySelector('#sample').value") == "hello", "language change preserves practice text");
                Check(await Read("document.querySelector('label[for=sample]').textContent") == "ข้อความสำหรับฝึกพิมพ์", "Thai practice label");
                await web.ExecuteScriptAsync("document.querySelector('#history-tab').click()");
                await Until(async () => await Read("document.querySelector('#history-tab').getAttribute('aria-selected')") == "true", "history page opens");
                Check(await Read("document.querySelector('#recent-title').textContent") == "คำที่แก้ไขล่าสุด", "localized history");
                Check(await web.ExecuteScriptAsync("!document.querySelector('#no-history').hidden") == "true", "new session is empty");
                entries.Record("l;ylfu", "สวัสดี", true);
                entries.Record("hello  123\n<x>", "้ำสสน  123\nฉปฮ", false);
                window.UpdateHistory();
                await Until(async () => await web.ExecuteScriptAsync("document.querySelectorAll('#corrections li').length") == "3", "only changed words from actual edits appear");
                Check(await Read("document.querySelector('#corrections bdi').textContent") == "<x>", "original is displayed as text");
                Check(await web.ExecuteScriptAsync("!document.querySelector('#corrections x')") == "true", "word cannot create markup");
                await web.ExecuteScriptAsync("document.querySelector('#corrections [data-word=\"l;ylfu\"]').click()");
                await Until(async () => await Read("document.querySelector('#whitelist bdi')?.textContent") == "l;ylfu", "whitelist original from history");
                Check(await Read("document.querySelector('#corrections [data-word=\"l;ylfu\"]').getAttribute('aria-disabled')") == "true", "saved state is visible");
                var persisted = Preferences.Load(file);
                Check(persisted.Whitelist.SequenceEqual(new[] { "l;ylfu" }) && !File.ReadAllText(file).Contains("hello"),
                    "settings persist the chosen original only");
                await web.ExecuteScriptAsync("let wordBox=document.querySelector('#whitelist-word');wordBox.value='two words';document.querySelector('#whitelist-form').requestSubmit()");
                Check(await web.ExecuteScriptAsync("!wordBox.checkValidity()") == "true", "reject multiple words");
                await web.ExecuteScriptAsync("wordBox.value='ชื่อเล่น';wordBox.dispatchEvent(new Event('input'));document.querySelector('#whitelist-form').requestSubmit()");
                await Until(async () => await web.ExecuteScriptAsync("document.querySelectorAll('#whitelist li').length") == "2", "direct Thai entry");
                await web.ExecuteScriptAsync("document.querySelector('#whitelist [data-word=\"ชื่อเล่น\"]').click()");
                await Until(async () => await web.ExecuteScriptAsync("document.querySelectorAll('#whitelist li').length") == "1", "remove saved word");
                window.ClientSize = new Size(510, 480);
                await Task.Delay(150);
                Check(await web.ExecuteScriptAsync("document.documentElement.scrollWidth <= innerWidth") == "true", "narrow Thai layout fits");
                await web.ExecuteScriptAsync("document.querySelector('#settings-tab').click();document.querySelector('#history-tab').click()");
                await Until(async () => await web.ExecuteScriptAsync("document.querySelectorAll('#corrections li').length") == "3", "return to current session history");
                Check(new CorrectionHistory().NewestFirst().Length == 0 && Preferences.Load(file).Whitelist.SequenceEqual(new[] { "l;ylfu" }),
                    "fresh session starts empty with its saved whitelist");
                state = state with { Error = "Unavailable" };
                window.UpdateState();
                await Until(async () => await Read("document.querySelector('#notice').textContent") == "Unavailable", "status error visible");
                Console.WriteLine("Passed WebView2 settings, Thai display, history, whitelist actions, persistence and narrow layout.");
            }
            catch (Exception e) { failure = e; }
            finally { window.Close(); }
        };
        Application.Run(window);
        if (failure is not null) throw new InvalidOperationException("WebView2 UI test failed", failure);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
