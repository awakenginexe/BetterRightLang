using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using BetterRightLang.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BetterRightLang.Desktop;

internal sealed class SettingsWindow : Form
{
    private const string Host = "betterrightlang.invalid";
    private const string Page = "https://" + Host + "/index.html";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Opening settings…" };
    private readonly string appData;
    private readonly Func<WindowState> readState;
    private readonly Action<CorrectionMode> setMode;
    private readonly Action<string> setLanguage;
    private readonly Action<bool> setStartup;
    private readonly Func<HistoryState> readHistory;
    private readonly Action<string, bool> setWhitelist;
    private bool historyVisible;
    private bool ready;

    internal SettingsWindow(string appData, Func<WindowState> readState,
        Action<CorrectionMode> setMode, Action<string> setLanguage, Action<bool> setStartup,
        Func<HistoryState> readHistory, Action<string, bool> setWhitelist)
    {
        this.appData = appData;
        this.readState = readState;
        this.setMode = setMode;
        this.setLanguage = setLanguage;
        this.setStartup = setStartup;
        this.readHistory = readHistory;
        this.setWhitelist = setWhitelist;
        Text = "BetterRightLang";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(880, 650);
        MinimumSize = new Size(490, 405);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 248, 243);
        browser.DefaultBackgroundColor = BackColor;
        Controls.Add(browser);
        Controls.Add(status);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(appData, "WebView2"));
            if (IsDisposed || Disposing) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (IsDisposed || Disposing) return;
            var core = browser.CoreWebView2;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "Web"),
                CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != Page;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += (_, args) =>
            {
                if (IsDisposed || Disposing) return;
                if (args.IsSuccess)
                {
                    status.Hide();
                    browser.Show();
                    browser.Focus();
                }
                else status.Text = $"Could not open the local settings page ({args.WebErrorStatus}).";
            };
            core.Navigate(Page);
        }
        catch (Exception error)
        {
            if (IsDisposed || Disposing) return;
            status.Text = "Settings could not open. Check that Microsoft Edge WebView2 Runtime is installed."
                + Environment.NewLine + Environment.NewLine + error.Message
                + Environment.NewLine + Environment.NewLine + "The tray menu also lets you change mode and quit.";
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != Page || args.WebMessageAsJson.Length > WordWhitelist.MaxLength * 6 + 128) return;
        try
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String) return;
            root.TryGetProperty("value", out var value);
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            switch (type.GetString())
            {
                case "ready":
                    ready = true;
                    UpdateState();
                    break;
                case "mode" when text is "Auto" or "Manual" or "Paused":
                    setMode(Enum.Parse<CorrectionMode>(text));
                    break;
                case "language" when text is "en" or "th":
                    setLanguage(text);
                    break;
                case "startup" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    setStartup(value.GetBoolean());
                    break;
                case "history" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    historyVisible = value.GetBoolean();
                    UpdateHistory();
                    break;
                case "whitelistAdd" or "whitelistRemove" when WordWhitelist.IsWord(text):
                    setWhitelist(text!, type.GetString() == "whitelistAdd");
                    break;
            }
        }
        catch (JsonException) { /* Ignore messages without a valid local UI payload. */ }
    }

    internal void UpdateState()
    {
        if (ready && !IsDisposed && !Disposing) Post(new { type = "state", state = readState() });
        UpdateHistory();
    }

    internal void UpdateHistory()
    {
        if (historyVisible && ready && !IsDisposed && !Disposing) Post(new { type = "history", state = readHistory() });
    }

    private void Post(object payload) => browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, Json));

    protected override void Dispose(bool disposing)
    {
        if (disposing) ready = false;
        base.Dispose(disposing);
    }
}
