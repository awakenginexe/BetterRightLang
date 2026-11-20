using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Windows.Forms;
using BetterRightLang.Core;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace BetterRightLang.Desktop;

internal sealed record WindowState(string Mode, string Language, bool Startup, string? Error, UpdateState Update);
internal sealed record UpdateState(string Version, string Status, string? AvailableVersion = null);

internal sealed class TrayApp : ApplicationContext
{
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "BetterRightLang";
    private readonly LayoutEngine engine;
    private readonly string appData;
    private readonly string settingsFile;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly Control dispatcher = new();
    private readonly Icon appIcon;
    private readonly Dictionary<CorrectionMode, Icon> modeIcons;
    private readonly DesktopInput input;
    private readonly CorrectionHistory history = new();
    private readonly RegisteredWaitHandle wake;
    private volatile SettingsWindow? window;
    private volatile Preferences settings;
    private string? settingError;
    private readonly UpdateManager updater = new(new GithubSource("https://github.com/awakenginexe/BetterRightLang", null, false));
    private UpdateInfo? availableUpdate;
    private string updateStatus;

    internal TrayApp(LayoutEngine engine, string appData, WaitHandle signal, bool inTray)
    {
        this.engine = engine;
        this.appData = appData;
        settingsFile = Path.Combine(appData, "settings.json");
        settings = Preferences.Load(settingsFile);
        updateStatus = updater.IsInstalled ? "idle" : "unavailable";
        engine.Whitelist = new WordWhitelist(settings.Whitelist ?? []);
        appIcon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        modeIcons = Enum.GetValues<CorrectionMode>().ToDictionary(mode => mode,
            mode => LoadModeIcon(mode, SystemInformation.SmallIconSize));
        _ = dispatcher.Handle;
        input = new DesktopInput(engine, () => settings.Mode, () => SetMode(
            settings.Mode == CorrectionMode.Auto ? CorrectionMode.Manual : CorrectionMode.Auto));
        tray = new NotifyIcon { ContextMenuStrip = menu };
        input.StatusChanged += InputStatusChanged;
        input.Corrected += OnCorrected;
        tray.DoubleClick += (_, _) => ShowSettings();
        menu.Opening += (_, _) => RefreshMenu();
        RefreshState();
        tray.Visible = true;
        wake = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
        {
            try { dispatcher.BeginInvoke(ShowSettings); }
            catch (InvalidOperationException) { /* The tray is exiting. */ }
        }, null, Timeout.Infinite, false);
        if (!inTray) dispatcher.BeginInvoke(ShowSettings);
    }

    private void ShowSettings()
    {
        if (window is null || window.IsDisposed)
        {
            window = new SettingsWindow(appData, ReadState, SetMode, SetLanguage, SetStartup,
                () => new(history.NewestFirst(), engine.Whitelist.Words), SetWhitelist, RequestUpdate)
            {
                Icon = appIcon
            };
            window.FormClosed += (_, _) => window = null;
        }
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Show();
        // A hidden startup shortcut can override the first ShowWindow call in this process.
        Native.ShowWindow(window.Handle, 5);
        window.Activate();
    }

    private string T(string en, string th) => settings.Language == "th" ? th : en;
    private string ModeName(CorrectionMode mode) => mode switch
    {
        CorrectionMode.Auto => T("Automatic", "อัตโนมัติ"),
        CorrectionMode.Manual => T("Manual", "แก้ด้วยตนเอง"),
        _ => T("Paused", "หยุดชั่วคราว")
    };

    private void RefreshMenu()
    {
        while (menu.Items.Count > 0) menu.Items[0].Dispose();
        menu.Items.Add(T("Open settings", "เปิดการตั้งค่า"), null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var mode in Enum.GetValues<CorrectionMode>())
        {
            var item = new ToolStripMenuItem(ModeName(mode)) { Checked = settings.Mode == mode };
            item.Click += (_, _) => SetMode(mode);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("Quit BetterRightLang", "ปิด BetterRightLang"), null, (_, _) => ExitThread());
    }

    private void RefreshState()
    {
        tray.Icon = modeIcons[settings.Mode];
        tray.Text = "BetterRightLang · " + ModeName(settings.Mode);
        window?.UpdateState();
    }

    internal static Icon LoadModeIcon(CorrectionMode mode, Size size) =>
        new(Path.Combine(AppContext.BaseDirectory, "Assets", $"tray-{mode.ToString().ToLowerInvariant()}.ico"), size);

    private void Notify(string text) => tray.ShowBalloonTip(3000, "BetterRightLang", text, ToolTipIcon.None);

    private void InputStatusChanged()
    {
        try { dispatcher.BeginInvoke(RefreshState); }
        catch (InvalidOperationException) when (dispatcher.IsDisposed || dispatcher.Disposing) { }
    }

    private void OnCorrected(string original, string replacement, bool automatic)
    {
        history.Record(original, replacement, automatic);
        var notice = CorrectionNotice(original, replacement, settings.Language);
        try
        {
            dispatcher.BeginInvoke(() =>
            {
                window?.UpdateHistory();
                if (notice is not null) Notify(notice);
            });
        }
        catch (InvalidOperationException) when (dispatcher.IsDisposed || dispatcher.Disposing) { }
    }

    internal static string? CorrectionNotice(string original, string replacement, string language)
    {
        if (original.Length != replacement.Length || original == replacement) return null;
        bool toThai = false, toEnglish = false;
        for (var start = 0; start < original.Length;)
        {
            var end = start;
            while (end < original.Length && !char.IsWhiteSpace(original[end])) end++;
            // Shared punctuation follows its word's source layout. Mixed words map their English and Thai letters separately.
            var thaiWord = original.AsSpan(start, end - start).ContainsAnyInRange('\u0e01', '\u0e5b');
            for (var i = start; i < end; i++)
            {
                if (original[i] == replacement[i]) continue;
                if (thaiWord && !char.IsAsciiLetter(original[i])) toEnglish = true;
                else toThai = true;
            }
            start = end + 1;
        }
        var thai = language == "th";
        if (toThai && toEnglish) return thai ? "อังกฤษ ⇄ ไทย" : "English ⇄ Thai";
        if (toEnglish) return thai ? "ไทย → อังกฤษ" : "Thai → English";
        return toThai ? (thai ? "อังกฤษ → ไทย" : "English → Thai") : null;
    }

    private void SetWhitelist(string word, bool add)
    {
        var updated = engine.Whitelist.With(word, add);
        if (updated != engine.Whitelist && Save(settings with { Whitelist = updated.Words.ToArray() }))
            engine.Whitelist = updated;
        RefreshState();
    }

    private WindowState ReadState()
    {
        bool startup = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
            startup = string.Equals(key?.GetValue(StartupName) as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { settingError = e.Message; }
        return new(settings.Mode.ToString(), settings.Language, startup, settingError ?? input.StatusError,
            new(Application.ProductVersion.Split('+')[0], updateStatus, availableUpdate?.TargetFullRelease.Version.ToString()));
    }

    private async void RequestUpdate(bool install)
    {
        if (!updater.IsInstalled || updateStatus is "checking" or "downloading") return;
        var update = availableUpdate;
        if (install && update is null) return;

        SetUpdateStatus(install ? "downloading" : "checking");
        try
        {
            if (install)
            {
                await updater.DownloadUpdatesAsync(update!);
                updater.WaitExitThenApplyUpdates(update!.TargetFullRelease);
                ExitThread();
                return;
            }

            availableUpdate = await updater.CheckForUpdatesAsync();
            SetUpdateStatus(availableUpdate is null ? "current" : "available");
        }
        catch (Exception error)
        {
            Trace.TraceError("BetterRightLang update failed: {0}", error);
            SetUpdateStatus("failed");
        }
    }

    private void SetUpdateStatus(string status)
    {
        updateStatus = status;
        window?.UpdateState();
    }

    private static string StartupCommand => "\"" + Environment.ProcessPath + "\" --tray";

    internal static void SyncStartupOnInstall(bool uninstall)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey, writable: true);
            if (key?.GetValue(StartupName) is not string command) return;
            if (!uninstall) key.SetValue(StartupName, StartupCommand);
            else if (string.Equals(command, StartupCommand, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(StartupName, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            Trace.TraceError("Could not update BetterRightLang startup entry: {0}", e);
        }
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
            if (enabled) key.SetValue(StartupName, StartupCommand);
            else key.DeleteValue(StartupName, throwOnMissingValue: false);
            settingError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { settingError = e.Message; }
        RefreshState();
    }

    private void SetMode(CorrectionMode mode)
    {
        var changed = settings.Mode != mode && Save(settings with { Mode = mode });
        if (changed) input.RefreshMode();
        RefreshState();
        if (changed) Notify(T("Correction mode · ", "โหมดการแก้ไข · ") + ModeName(mode));
    }

    private void SetLanguage(string language)
    {
        Save(settings with { Language = language });
        RefreshState();
    }

    private bool Save(Preferences next)
    {
        try
        {
            next.Save(settingsFile);
            settings = next;
            settingError = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            settingError = e.Message;
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            wake.Unregister(null);
            input.StatusChanged -= InputStatusChanged;
            input.Corrected -= OnCorrected;
            input.Dispose();
            window?.Dispose();
            tray.Visible = false;
            tray.Dispose();
            foreach (var icon in modeIcons.Values) icon.Dispose();
            menu.Dispose();
            appIcon.Dispose();
            dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
