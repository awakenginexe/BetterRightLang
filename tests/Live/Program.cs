using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

internal static class Program
{
    const string Raw = "l;ylfurujg0wf", Thai = "สวัสดีพี่เจได";
    static readonly string Settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterRightLang", "settings.json");
    static string tag = "BRL-Live-" + Guid.NewGuid().ToString("N"), directory = "", executable = "", kind = "", initialMode = "", publishedApp = "";
    static nint window, previousWindow, initialLayout;
    static uint processId;
    static AutomationElement? editor;
    static bool initialCaps, capsKnown, ownedWindow, overlapCase, earlyOverlap, burstOverlap, proseMirror, closeWindow = true, cleanupInput = true;
    // A real ProseMirror editor, pinned on unpkg. Like ChatGPT's message box, it keeps its own Select All state.
    const string ProseMirrorPage = "<style>.ProseMirror{white-space:pre-wrap;word-wrap:break-word}</style>" +
        "<script type='importmap'>{\"imports\":{" +
        "\"orderedmap\":\"https://unpkg.com/orderedmap@2.1.1/dist/index.js\"," +
        "\"w3c-keyname\":\"https://unpkg.com/w3c-keyname@2.2.8/index.js\"," +
        "\"prosemirror-model\":\"https://unpkg.com/prosemirror-model@1.19.4/dist/index.js\"," +
        "\"prosemirror-transform\":\"https://unpkg.com/prosemirror-transform@1.8.0/dist/index.js\"," +
        "\"prosemirror-state\":\"https://unpkg.com/prosemirror-state@1.4.3/dist/index.js\"," +
        "\"prosemirror-view\":\"https://unpkg.com/prosemirror-view@1.32.7/dist/index.js\"," +
        "\"prosemirror-keymap\":\"https://unpkg.com/prosemirror-keymap@1.2.2/dist/index.js\"," +
        "\"prosemirror-commands\":\"https://unpkg.com/prosemirror-commands@1.5.2/dist/index.js\"," +
        "\"prosemirror-schema-basic\":\"https://unpkg.com/prosemirror-schema-basic@1.2.2/dist/index.js\"}}</script>" +
        "<script type='module'>import{schema}from'prosemirror-schema-basic';import{EditorState}from'prosemirror-state';import{EditorView}from'prosemirror-view';" +
        "import{keymap}from'prosemirror-keymap';import{baseKeymap}from'prosemirror-commands';" +
        "const view=new EditorView(document.getElementById('mount'),{state:EditorState.create({schema,plugins:[keymap(baseKeymap)]}),attributes:{id:'owned',role:'textbox','aria-multiline':'true','aria-label':'Disposable BetterRightLang live test'}});" +
        "const offset=p=>view.state.doc.textBetween(0,p,'\\n').length;" +
        "Object.defineProperties(view.dom,{value:{get:()=>view.state.doc.textBetween(0,view.state.doc.content.size,'\\n')}," +
        "selectionStart:{get:()=>offset(view.state.selection.from)},selectionEnd:{get:()=>offset(view.state.selection.to)}});";
    static ClientWebSocket? cdp;
    static int requestId;
    static uint launchedProcess;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("Opt-in real foreground input; leave keyboard/mouse alone during run.\n" +
                "dotnet run --project tests/Live -c Release -- --run --target edge|chrome|brave-local|brave-google|notepad|practice --app PATH [--case all|auto|manual|quick-search|caps|whitelist|overlap|overlap-early|overlap-burst|overlap-auto] [--editor textarea|prosemirror]\n" +
                "--app starts that published executable with --tray if absent; an existing process must match its exact path.\n" +
                "--expect-app PATH requires that exact executable already running. --self-check does not open apps or send input.");
            return 0;
        }
        if (args.Contains("--self-check"))
        {
            Require(Marshal.SizeOf<Input>() == 40 && Raw.Length == 13, "native input structure / fixture");
            Require(PhysicalKeys(Raw).Length == Raw.Length && Uri.UnescapeDataString(Uri.EscapeDataString(Thai)) == Thai, "physical-key/title codec");
            Console.WriteLine("PASS harness self-check (no GUI/input)."); return 0;
        }
        if (!args.Contains("--run")) { Console.Error.WriteLine("Refusing foreground input without --run."); return 2; }
        string Option(string name, string fallback = "") => Array.IndexOf(args, name) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        kind = Option("--target", "edge");
        var cases = Option("--case", "all");
        var quickSearch = cases == "quick-search";
        earlyOverlap = cases == "overlap-early";
        burstOverlap = cases == "overlap-burst";
        overlapCase = cases is "overlap" or "overlap-auto" || earlyOverlap || burstOverlap;
        var editorKind = Option("--editor", "textarea");
        proseMirror = editorKind == "prosemirror";
        int failures = 0;
        try
        {
            Require(kind is "edge" or "chrome" or "brave-local" or "brave-google" or "notepad" or "practice", "unknown --target");
            Require(cases is "all" or "auto" or "manual" or "caps" or "whitelist" || quickSearch || overlapCase, "unknown --case");
            Require(!quickSearch || kind is "edge" or "chrome" or "brave-local", "quick-search requires a local Chromium field");
            Require(!overlapCase || kind is "edge" or "chrome" or "brave-local" or "brave-google", "overlap requires an isolated Chromium editor");
            Require(editorKind is "textarea" or "prosemirror", "unknown --editor");
            Require(!proseMirror || kind is "edge" or "chrome" or "brave-local" && !quickSearch && !overlapCase, "prosemirror requires a local Chromium target and a non-overlap, non-search case");
            var app = Path.GetFullPath(Option("--expect-app", Option("--app")));
            publishedApp = app;
            Require(File.Exists(app), "supply exact published app executable with --app or --expect-app");
            var apps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app));
            Require(apps.All(p => string.Equals(p.MainModule?.FileName, app, StringComparison.OrdinalIgnoreCase)), "a different app binary is already running; stop it yourself first");
            if (apps.Length == 0)
            {
                Require(!args.Contains("--expect-app"), "expected app is not running");
                Process.Start(new ProcessStartInfo(app) { ArgumentList = { "--tray" }, UseShellExecute = false });
                Thread.Sleep(1500);
            }
            Require(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app)).Any(p => string.Equals(p.MainModule?.FileName, app, StringComparison.OrdinalIgnoreCase)), "published app did not stay running");
            initialMode = Mode(); Require(initialMode is "Auto" or "Manual", "start app in Auto or Manual; Paused cannot be toggled through its shortcuts");
            previousWindow = Native.GetForegroundWindow();
            Require(!ModifiersDown(), "release modifiers before running");
            CreateTarget(quickSearch);
            Require(TakeFocus(window), "cannot activate owned editor");
            Wait(() => Native.GetForegroundWindow() == window, "owned editor foreground");
            if (kind == "brave-google")
            {
                ConnectGoogle();
                Wait(() => Evaluate("document.readyState").GetString() == "complete", "Google initial page load", 15000);
                Thread.Sleep(1200);
                FocusGoogle();
                var document = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,
                    new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document), new PropertyCondition(AutomationElement.NameProperty, "Google")));
                editor = document?.FindFirst(TreeScope.Descendants,
                    new AndCondition(new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)),
                        new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "Search"), new PropertyCondition(AutomationElement.NameProperty, "ค้นหา"))));
            }
            if (kind == "notepad")
            {
                var root = AutomationElement.FromHandle(window);
                editor = root.FindFirst(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
                Require(editor is not null, "Notepad editable control unavailable");
                editor!.SetFocus();
            }
            if (kind == "practice")
            {
                Native.SetThreadDpiAwarenessContext(-4);
                Wait(() =>
                {
                    editor = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,
                        new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                            new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "sample"),
                                new PropertyCondition(AutomationElement.NameProperty, "Practice text"),
                                new PropertyCondition(AutomationElement.NameProperty, "ข้อความสำหรับฝึกพิมพ์"))));
                    return editor is not null;
                }, "published app practice field", 15000);
                if (ReadEditorValue() != "")
                {
                    closeWindow = false;
                    throw new InvalidOperationException("practice field is nonempty; preserving existing text and settings window");
                }
                cleanupInput = true;
                Require(TakeFocus(window), "cannot activate the initialized practice window");
                ClickPracticeField();
            }
            Wait(() => IsEditorFocused(), "owned editable field focus");
            initialLayout = Layout();
            initialCaps = Caps();
            capsKnown = true;
            Diagnostics("ready");
            SetCaps(false);
            if (cases is "all" or "auto")
            {
                // Notepad can drop zero-delay SendInput bursts after prior edits even with the tray app closed.
                Run("Auto fast", () => Auto(0));
                Run("Auto paced", () => Auto(170));
                Run("Auto continuous", () => Auto(80));
                Run("Auto keeps text before the word", () =>
                {
                    EnsureMode("Auto"); Reset(); SetLayout(new nint(0x04090409)); Thread.Sleep(250);
                    Type("prefix " + Raw, 90);
                    Wait(() => Value() == "prefix " + Thai && (uint)(long)Layout() == 0x041e041e, "earlier text kept and Thai HKL", 5000);
                });
                Run("Auto switches both ways, including a word that starts with a Thai mark", () =>
                {
                    // On the Thai keyboard, hello starts with the tone mark ้, which joins the space before it.
                    EnsureMode("Auto"); Reset(); SetLayout(new nint(0x04090409)); Thread.Sleep(250);
                    Type("8e;jk hello", 90);
                    Wait(() => Value() == "คำว่า hello" && (uint)(long)Layout() == 0x04090409, "คำว่า, then hello typed on the Thai keyboard, and US HKL", 5000);
                });
                Run("Auto Thai keyboard to English", () =>
                {
                    Reset(); SetLayout(new nint(0x041e041e)); Thread.Sleep(250);
                    Type("correct", 90);
                    Wait(() => Value() == "correct" && (uint)(long)Layout() == 0x04090409, "English word and US keyboard");
                });
            }
            if (cases is "caps" or "all")
                Run("Left and right Ctrl+CapsLock keep Caps Lock off and on", CheckCapsShortcut);
            else if (quickSearch) Run("Manual correction in a search field that consumes Home and End", QuickSearch);
            else if (cases == "whitelist") Run("Whitelisted full word and manual override", Whitelisted);
            else if (!overlapCase) Run("Ctrl+CapsLock real registration", () => ToggleMode(Mode() == "Auto" ? "Manual" : "Auto"));
            else if (earlyOverlap || burstOverlap || cases == "overlap") Run("Manual correction with overlapping typing", Overlap);
            else Run("Auto correction with continuing typing", AutoOverlap);
            if (cases is "all" or "manual")
            {
                Run("Manual current word / adjacent text", () => Manual(false));
                Run("Manual selection / adjacent text", () => Manual(true));
                Run("Manual selection switch back", Reverse);
                Run("Manual Thai current word / adjacent text", ReverseCurrentWord);
            }
            Console.WriteLine($"RESULT {kind}: {failures} failed. Published app={app}. App remains running.");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e.Message); Diagnostics("failure"); return 1; }
        finally
        {
            if (ownedWindow && cleanupInput && Native.GetForegroundWindow() == window)
            {
                try
                {
                    if (kind == "brave-google") FocusGoogle();
                    try { if (Mode() != initialMode) ToggleMode(initialMode); }
                    finally
                    {
                        Reset();
                        if (kind == "notepad") Chord(0x11, 0x53);
                        if (initialLayout != 0) SetLayout(initialLayout);
                        if (capsKnown) SetCaps(initialCaps);
                    }
                    CloseOwnedWindow();
                    if (previousWindow != 0) TakeFocus(previousWindow);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("Input cleanup incomplete: " + e.Message + "; " + directory);
                    if (kind != "notepad") CloseOwnedWindow();
                }
            }
            else if (ownedWindow)
            {
                if (kind != "notepad") CloseOwnedWindow();
                else Console.Error.WriteLine("Focus changed: no further input sent; owned Notepad left open: " + directory);
            }
            cdp?.Dispose();
            if (directory.Length != 0 && (kind == "practice" || !ownedWindow || !Native.IsWindow(window) || kind == "notepad" && !OwnedIdentity()))
            {
                try
                {
                    Require(Path.GetFileName(directory) == tag && Path.GetDirectoryName(Path.GetFullPath(directory)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), "temporary cleanup path escaped owned directory");
                    for (int attempt = 0; ; attempt++)
                    {
                        try { Directory.Delete(directory, true); break; }
                        catch (IOException) when (attempt < 12) { Thread.Sleep(150); }
                    }
                }
                catch (IOException) { Console.Error.WriteLine("Owned temporary files remain: " + directory); }
                catch (UnauthorizedAccessException) { Console.Error.WriteLine("Owned temporary files remain: " + directory); }
            }
        }
        void Run(string name, Action action)
        {
            try { if (kind == "brave-google") FocusGoogle(); action(); Console.WriteLine("PASS " + name + "; value=" + Value() + "; HKL=" + ((long)Layout()).ToString("x")); }
            catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + e.Message); Diagnostics(name); }
        }
    }

    static void CreateTarget(bool quickSearch)
    {
        directory = Path.Combine(Path.GetTempPath(), tag); Directory.CreateDirectory(directory);
        var existing = Windows().ToHashSet();
        if (kind == "practice")
        {
            executable = publishedApp;
            cleanupInput = false;
            launchedProcess = (uint)Process.GetProcessesByName(Path.GetFileNameWithoutExtension(publishedApp))
                .Single(p => string.Equals(p.MainModule?.FileName, publishedApp, StringComparison.OrdinalIgnoreCase)).Id;
            var settings = existing.Where(h => Title(h) == "BetterRightLang" && Native.GetWindowThreadProcessId(h, out var pid) != 0 && pid == launchedProcess).ToArray();
            Require(settings.Length <= 1, "multiple settings windows found");
            if (settings.Length == 1)
            {
                window = settings[0]; processId = launchedProcess; ownedWindow = true; closeWindow = false;
                Console.WriteLine("TARGET existing settings window; only blank practice field permitted; window will remain open.");
                return;
            }
            Process.Start(new ProcessStartInfo(publishedApp) { UseShellExecute = false });
        }
        else if (kind == "notepad")
        {
            executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
            var file = Path.Combine(directory, tag + ".txt"); File.WriteAllText(file, "");
            Process.Start(new ProcessStartInfo(executable) { ArgumentList = { file }, UseShellExecute = true });
        }
        else
        {
            string relative = kind is "brave-google" or "brave-local" ? @"BraveSoftware\Brave-Browser\Application\brave.exe" : kind == "edge" ? @"Microsoft\Edge\Application\msedge.exe" : @"Google\Chrome\Application\chrome.exe";
            executable = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }
                .Select(root => Path.Combine(root, relative)).FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("installed browser not found");
            var html = Path.Combine(directory, "editor.html");
            var field = quickSearch ? "<input id='owned' autofocus>" : "<textarea id='owned' autofocus rows='12' cols='70'></textarea>";
            var script =
                "const e=document.getElementById('owned');let held='',caps=0,capsState='';function report(){document.title='" + tag + "|'+(document.hasFocus()&&document.activeElement===e?'focus':'blur')+'|'+encodeURIComponent(e.value)+'|'+e.selectionStart+'|'+e.selectionEnd+'|caps-'+caps+'-'+capsState+(held?'|held-'+held:'');}" +
                "e.addEventListener('keydown',k=>{if(k.key==='Control'){caps++;capsState=k.getModifierState('CapsLock')?'on':'off';report();}});" +
                (quickSearch ? "e.addEventListener('keydown',k=>{if(['Home','End','ArrowUp','ArrowDown'].includes(k.key))k.preventDefault();});" : "") +
                "function stall(event){held=event;report();document.title+='|hold-'+event;const until=performance.now()+1500;while(performance.now()<until){}}" +
                "function hold(event){if(" + (overlapCase && !earlyOverlap ? "true" : "false") + "&&!held&&e.selectionStart===7&&((e.value==='prefix l;ylfurujg0wf suffix'&&e.selectionEnd===20)||(e.value==='prefix l;ylfu suffix'&&e.selectionEnd===13)))stall(event);}" +
                (earlyOverlap ? "e.addEventListener('keydown',k=>{if(!held&&k.code==='KeyX'&&e.selectionStart===20&&e.selectionEnd===20&&e.value==='prefix l;ylfurujg0wf suffix')stall('keydown');});" : "") +
                "document.addEventListener('selectionchange',()=>hold('selectionchange'));e.addEventListener('beforeinput',()=>hold('beforeinput'));['input','select','keyup','focus','blur'].forEach(k=>e.addEventListener(k,report));document.addEventListener('selectionchange',report);window.addEventListener('blur',report);window.addEventListener('focus',report);setInterval(report,50);e.focus();report();";
            File.WriteAllText(html, "<!doctype html><meta charset='utf-8'><title>" + tag + "</title>" + (proseMirror
                ? "<p>Disposable ProseMirror editor</p><div id='mount'></div>" + ProseMirrorPage + "{" + script + "}</script>"
                : "<label for='owned'>Disposable BetterRightLang live test</label><br>" + field + "<script>" + script + "</script>"));
            var start = new ProcessStartInfo(executable) { ArgumentList = { "--user-data-dir=" + Path.Combine(directory, "profile"), "--no-first-run", "--no-default-browser-check" }, UseShellExecute = false };
            if (kind == "brave-google")
            {
                start.ArgumentList.Add("--remote-debugging-port=0");
                start.ArgumentList.Add("--new-window");
                start.ArgumentList.Add("https://www.google.com/");
            }
            else start.ArgumentList.Add("--app=" + new Uri(html).AbsoluteUri);
            launchedProcess = (uint)Process.Start(start)!.Id;
        }
        Wait(() =>
        {
            var matches = Windows().Where(h => (kind == "notepad" || !existing.Contains(h)) && (kind is "brave-google" or "practice" ? Native.GetWindowThreadProcessId(h, out var pid) != 0 && pid == launchedProcess && (kind == "practice" ? Title(h) == "BetterRightLang" : Title(h).Contains("Google", StringComparison.Ordinal)) : kind == "notepad" ? Title(h).Contains(tag, StringComparison.Ordinal) : Title(h).StartsWith(tag + "|", StringComparison.Ordinal))).ToArray();
            if (matches.Length != 1) return false;
            window = matches[0]; Native.GetWindowThreadProcessId(window, out processId);
            var owner = Process.GetProcessById((int)processId);
            if (kind == "notepad") { if (!owner.ProcessName.Equals("Notepad", StringComparison.OrdinalIgnoreCase)) return false; executable = owner.MainModule!.FileName!; }
            return string.Equals(owner.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
        }, "new uniquely identified " + kind + " window", 15000);
        ownedWindow = true;
        Console.WriteLine($"TARGET hwnd=0x{window:x}, pid={processId}, title tag={tag}, temp={directory}");
    }

    static void Auto(int pace)
    {
        EnsureMode("Auto"); Reset(); SetLayout(new nint(0x04090409)); Thread.Sleep(400);
        if (pace != 170) Type(Raw, pace);
        else
        {
            Type(Raw[..6], pace);
            Wait(() => Value() == "สวัสดี" && (uint)(long)Layout() == 0x041e041e, "sixth key corrects the prefix and switches the focused keyboard", 4000);
            Type(Raw[6..], pace);
        }
        Wait(() => Value() == Thai && (uint)(long)Layout() == 0x041e041e, "actual Auto value and Thai HKL", 5000);
    }
    static void Whitelisted()
    {
        using var prefs = JsonDocument.Parse(File.ReadAllText(Settings));
        Require(prefs.RootElement.TryGetProperty("whitelist", out var saved) && saved.EnumerateArray().Any(w =>
            string.Equals(w.GetString(), Raw, StringComparison.OrdinalIgnoreCase)), "whitelist the fixture's original spelling first");
        EnsureMode("Auto"); Reset(); SetLayout(new nint(0x04090409)); Thread.Sleep(400);
        Type(Raw + " ", 155); Thread.Sleep(450);
        Require(Value() == Raw + " " && (uint)(long)Layout() == 0x04090409, "Auto preserves every character and the original layout through a boundary");
        Chord(0x10, 0x08);
        Wait(() => Value() == Thai + " " && (uint)(long)Layout() == 0x041e041e, "manual shortcut overrides whitelist and preserves trailing space");
    }

    static void QuickSearch()
    {
        EnsureMode("Manual"); Reset(); SetLayout(new nint(0x041e041e));
        Type("amane", 35);
        Wait(() => Value() == "ฟทฟืำ", "Thai source in the search field");
        Chord(0x10, 0x08);
        Wait(() => Value() == "amane" && (uint)(long)Layout() == 0x04090409, "replace ฟทฟืำ with amane");

        Reset(); Type("prefix  suffix", 25);
        for (int i = 0; i < " suffix".Length; i++) Key(0x25);
        SetLayout(new nint(0x041e041e)); Type("amane", 35);
        Wait(() => Value() == "prefix ฟทฟืำ suffix", "Thai source between adjacent text");
        Chord(0x10, 0x08);
        Wait(() => Value() == "prefix amane suffix" && (uint)(long)Layout() == 0x04090409, "preserve both sides of the search term");
        Type("x", 0);
        Wait(() => Value() == "prefix amanex suffix", "continued typing starts immediately after amane");
    }

    static void Manual(bool selection)
    {
        EnsureMode("Manual"); Reset(); SetLayout(new nint(0x04090409));
        Type("prefix " + Raw + " suffix", 20);
        Wait(() => Value() == "prefix " + Raw + " suffix", "manual mode must preserve source typing");
        Key(0x24);
        for (int i = 0; i < 7; i++) Key(0x27);
        if (selection)
        {
            var keys = new List<Input> { Event(0x10, false) };
            for (int i = 0; i < Raw.Length; i++) { keys.Add(Event(0x27, false)); keys.Add(Event(0x27, true)); }
            keys.Add(Event(0x10, true));
            Send(keys.ToArray());
        }
        else for (int i = 0; i < Raw.Length; i++) Key(0x27);
        Thread.Sleep(500);
        if (kind == "brave-google")
        {
            var range = GoogleField();
            Console.WriteLine($"Before manual shortcut: DOM selection {range.GetProperty("start")}..{range.GetProperty("end")}");
            Require(range.GetProperty("start").GetInt32() == (selection ? 7 : 20) && range.GetProperty("end").GetInt32() == 20,
                "Google test caret/selection setup");
        }
        var checkCaps = selection && kind is ("edge" or "chrome" or "brave-local" or "brave-google");
        var caps = checkCaps && Caps();
        if (selection) Chord(0x10, 0x14); else Chord(0x10, 0x08);
        Wait(() => Value() == "prefix " + Thai + " suffix" && (uint)(long)Layout() == 0x041e041e, "manual hotkey, protected adjacent text, and Thai HKL");
        Require(!checkCaps || Caps() == caps, "selection shortcut changed Caps Lock");
    }
    static void Overlap()
    {
        EnsureMode("Manual"); Reset(); SetLayout(new nint(0x04090409));
        var extra = burstOverlap ? "wmp" : "x";
        var converted = burstOverlap ? "ไทย" : "ป";
        Type("prefix " + Raw + " suffix", 20);
        Wait(() => Value() == "prefix " + Raw + " suffix", "overlap source text");
        Key(0x24);
        for (int i = 0; i < 7 + Raw.Length; i++) Key(0x27);
        Thread.Sleep(300);
        string held;
        if (earlyOverlap)
        {
            var focusedControl = FocusedControl();
            Chord(0x10, 0x08);
            Require(Native.GetForegroundWindow() == window && OwnedIdentity() && FocusedControl() == focusedControl, "owned editor changed before early typing");
            SendEvents(new[] { Event(0x58, false), Event(0x58, true) });
            if (kind != "brave-google") Wait(() => Title(window).Contains("|held-keydown"), "renderer held the incoming key before insertion");
            held = kind == "brave-google" ? "" : "keydown";
        }
        else
        {
            var sender = StartOverlapTypist(extra, 2500);
            Chord(0x10, 0x08);
            held = FinishOverlapTypist(sender);
        }
        Wait(() => { var v = Value(); return v == "prefix " + Thai + extra + " suffix" || v == "prefix " + Thai + converted + " suffix"; }, "correction preserves typed letters in order and surrounding text", 6000);
        CheckOverlapCaret(("prefix " + Thai).Length + extra.Length, held);
    }
    static void CheckOverlapCaret(int expected, string held)
    {
        int start, end;
        if (kind == "brave-google")
        {
            var field = GoogleField(); start = field.GetProperty("start").GetInt32(); end = field.GetProperty("end").GetInt32();
        }
        else
        {
            var fields = Title(window).Split('|'); start = int.Parse(fields[3]); end = int.Parse(fields[4]);
        }
        Require(start == end && start == expected, "caret must follow typed letters with no selection");
        Console.WriteLine($"Overlap hold={((held.Length == 0) ? "timing unverified" : held)}; caret={start}..{end}; value={Value()}");
    }
    static Task<string> StartOverlapTypist(string text, int delay)
    {
        var focusedControl = FocusedControl();
        return Task.Run(() =>
        {
            var timer = Stopwatch.StartNew();
            string marker = "";
            while (timer.ElapsedMilliseconds < (kind == "brave-google" ? 40 : delay))
            {
                var title = Title(window);
                if (kind != "brave-google" && title.Contains("|hold-", StringComparison.Ordinal)) { marker = title.Split("|hold-")[^1]; break; }
                Thread.Sleep(2);
            }
            if (text is "x" or "wmp") Thread.Sleep(80);
            if (marker.Length != 0 && !Title(window).EndsWith("|hold-" + marker, StringComparison.Ordinal)) marker = "";
            bool CommandModifierDown() => new[] { 0x11, 0x12, 0x5b, 0x5c }.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0);
            var modifiers = Stopwatch.StartNew();
            while (CommandModifierDown() && modifiers.ElapsedMilliseconds < 120) Thread.Sleep(2);
            Require(!CommandModifierDown() &&
                Native.GetForegroundWindow() == window && OwnedIdentity() && FocusedControl() == focusedControl, "owned focused control or modifiers changed; typist stopped");
            SendEvents(PhysicalKeys(text).SelectMany(k => new[] { Event(k, false), Event(k, true) }).ToArray());
            return marker;
        });
    }
    static string FinishOverlapTypist(Task<string> sender)
    {
        Require(sender.Wait(7000), "overlapping typist timed out");
        string held = sender.Result;
        Console.WriteLine(held.Length == 0 ? "Independent typing sent; overlap timing not verified." : "Independent typing sent during " + held + " hold.");
        Require(kind == "brave-google" || held.Length != 0, "local controlled overlap was not observed");
        return held;
    }
    static void AutoOverlap()
    {
        EnsureMode("Manual"); Reset(); SetLayout(new nint(0x04090409));
        Type("prefix  suffix", 20);
        Wait(() => Value() == "prefix  suffix", "auto overlap surrounding text");
        Key(0x23);
        for (int i = 0; i < 7; i++) Key(0x25);
        EnsureMode("Auto");
        Type(Raw[..5], 100);
        var sender = StartOverlapTypist(Raw[6..], 3000);
        Type(Raw[5..6], 0);
        var held = FinishOverlapTypist(sender);
        Wait(() => Value() == "prefix " + Thai + " suffix" && (uint)(long)Layout() == 0x041e041e, "auto correction preserves continuing typing and surrounding text", 6000);
        CheckOverlapCaret(("prefix " + Thai).Length, held);
    }
    static void Reverse()
    {
        EnsureMode("Manual"); Reset(); SetLayout(new nint(0x04090409)); Type(Raw, 20); Thread.Sleep(400);
        Chord(0x10, 0x08);
        Wait(() => Value() == Thai && (uint)(long)Layout() == 0x041e041e, "forward phrase / Thai HKL");
        Chord(0x11, 0x41); Thread.Sleep(400); Chord(0x10, 0x14);
        Wait(() => Value() == Raw && (uint)(long)Layout() == 0x04090409, "reverse phrase / English HKL");
    }
    static void ReverseCurrentWord()
    {
        EnsureMode("Manual"); Reset(); SetLayout(new nint(0x04090409));
        Type("prefix " + Raw + " suffix", 20);
        Wait(() => Value() == "prefix " + Raw + " suffix", "reverse source text");
        Key(0x23);
        for (int i = 0; i < 7; i++) Key(0x25);
        Chord(0x10, 0x08);
        Wait(() => Value() == "prefix " + Thai + " suffix", "Thai current word and adjacent text");
        Chord(0x10, 0x08);
        Wait(() => Value() == "prefix " + Raw + " suffix" && (uint)(long)Layout() == 0x04090409, "reverse Thai current word with combining marks and adjacent text");
    }
    static void Reset() { Guard(); Chord(0x11, 0x41); Key(0x2e); Wait(() => Value() == "", "empty owned editor"); }
    static void EnsureMode(string wanted) { if (Mode() != wanted) ToggleMode(wanted); }
    static void ToggleMode(string wanted)
    {
        var browser = kind is "brave-local" or "brave-google" or "edge" or "chrome";
        var caps = browser && Caps();
        if (kind == "brave-google") FocusGoogle();
        Chord(0x11, 0x14);
        Wait(() => Mode() == wanted, "Ctrl+CapsLock registered and mode persisted as " + wanted);
        Thread.Sleep(400);
        Require(!browser || Caps() == caps, "mode shortcut changed Caps Lock");
    }
    static void CheckCapsShortcut()
    {
        SetLayout(new nint(0x04090409));
        bool TypesUppercase()
        {
            Reset(); Key(0x41);
            Wait(() => Value() is "A" or "a", "letter case in focused editor");
            return Value() == "A";
        }
        Input Scan(ushort scan, bool up, bool right = false) => new() { Type = 1,
            Data = new InputUnion { Keyboard = new KeyboardInput { Scan = scan, Flags = 8u | (up ? 2u : 0u) | (right ? 1u : 0u) } } };
        foreach (var on in new[] { false, true })
        {
            if (TypesUppercase() != on) Key(0x14);
            Require(TypesUppercase() == on, "plain Caps Lock changes the typed letter case");
            Reset();
            var wanted = Mode() == "Auto" ? "Manual" : "Auto";
            Send(new[] { Scan(0x1d, false, on), Scan(0x3a, false) });
            try
            {
                Wait(() => Mode() == wanted, "scan-code mode shortcut while held");
                SendEvents(new[] { Scan(0x3a, false), Scan(0x1d, true, on), Scan(0x3a, false) });
            }
            finally
            {
                SendEvents(new[] { Scan(0x3a, true) });
                if (Native.GetAsyncKeyState(on ? 0xa3 : 0xa2) < 0) SendEvents(new[] { Scan(0x1d, true, on) });
            }
            Thread.Sleep(300);
            Require(Mode() == wanted, "held Caps Lock switched the mode more than once");
            Require(TypesUppercase() == on, "Ctrl+Caps Lock changed the focused field's letter case");
        }
        if (TypesUppercase()) Key(0x14);
    }
    static string Mode()
    {
        if (!File.Exists(Settings)) return "Auto";
        using var stream = new FileStream(Settings, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var json = JsonDocument.Parse(stream); return json.RootElement.GetProperty("mode").GetString()!;
    }
    static string Value()
    {
        Guard();
        if (kind == "brave-google") return GoogleField().GetProperty("value").GetString()!;
        if (kind is not "notepad" and not "practice") return Uri.UnescapeDataString(Title(window).Split('|')[2]);
        return ReadEditorValue();
    }
    static string ReadEditorValue()
    {
        if (editor!.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
        if (kind == "notepad" && editor.TryGetCurrentPattern(TextPattern.Pattern, out var text)) return ((TextPattern)text).DocumentRange.GetText(-1).TrimEnd('\r', '\n');
        throw new InvalidOperationException("exact editable field value cannot be read");
    }
    static bool IsEditorFocused()
    {
        if (kind == "brave-google") return cdp is not null && GoogleField().GetProperty("focus").GetBoolean();
        if (kind == "practice") return editor is not null && editor.Current.HasKeyboardFocus;
        if (kind != "notepad") return Title(window).StartsWith(tag + "|focus|", StringComparison.Ordinal);
        return editor is not null && editor.Current.HasKeyboardFocus && editor.Current.ProcessId == processId;
    }
    static void ClickPracticeField()
    {
        var point = editor!.GetClickablePoint();
        var hit = AutomationElement.FromPoint(point);
        var location = new ScreenPoint { X = (int)point.X, Y = (int)point.Y };
        bool IsOwnedPoint() => OwnedIdentity() && Native.GetForegroundWindow() == window &&
            Native.IsChild(window, Native.WindowFromPoint(location));
        Require(Automation.Compare(hit, editor) && IsOwnedPoint(), "practice field must be visible under the pointer target");
        Require(Native.GetCursorPos(out var previous), "unable to get pointer position");
        try
        {
            Require(Native.SetCursorPos(location.X, location.Y) && IsOwnedPoint() &&
                Native.GetCursorPos(out var current) && current.Equals(location), "practice click position changed");
            var click = new[] { 0x2u, 0x4u }.Select(flags => new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = flags } } }).ToArray();
            Require(Native.SendInput(2, click, Marshal.SizeOf<Input>()) == 2, "practice field click failed");
            Wait(() => editor.Current.HasKeyboardFocus, "clicked practice field focused");
        }
        finally
        {
            if (Native.GetCursorPos(out var current) && current.Equals(location)) Native.SetCursorPos(previous.X, previous.Y);
        }
    }
    static void Guard()
    {
        Require(Native.GetForegroundWindow() == window && Native.IsWindow(window), "foreground changed; input aborted");
        Require(OwnedIdentity(), "owned hwnd/PID/executable/title changed");
        if (kind == "brave-google" && !IsEditorFocused()) FocusGoogle();
        Require(IsEditorFocused(), "owned edit focus changed");
    }
    static bool OwnedIdentity()
    {
        if (!ownedWindow || !Native.IsWindow(window) || !(kind == "practice" ? Title(window) == "BetterRightLang" : kind == "brave-google" ? Title(window).Contains("Google", StringComparison.Ordinal) : Title(window).Contains(tag, StringComparison.Ordinal))) return false;
        Native.GetWindowThreadProcessId(window, out var current);
        return current == processId && string.Equals(Process.GetProcessById((int)current).MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
    }
    static bool TakeFocus(nint target)
    {
        Native.PeekMessage(out _, 0, 0, 0, 0);
        Native.ShowWindowAsync(target, 9);
        if (Native.GetForegroundWindow() == target) return true;
        Native.SetForegroundWindow(target);
        if (Native.GetForegroundWindow() == target) return true;
        var other = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var mine = Native.GetCurrentThreadId();
        var targetThread = Native.GetWindowThreadProcessId(target, out _);
        bool foregroundAttached = other != 0 && mine != other && Native.AttachThreadInput(mine, other, true);
        bool targetAttached = targetThread != mine && targetThread != other && Native.AttachThreadInput(mine, targetThread, true);
        try
        {
            for (int i = 0; i < 10; i++)
            {
                Native.BringWindowToTop(target);
                Native.SetForegroundWindow(target);
                if (Native.GetForegroundWindow() == target) break;
                Thread.Sleep(60);
            }
        }
        finally { if (targetAttached) Native.AttachThreadInput(mine, targetThread, false); if (foregroundAttached) Native.AttachThreadInput(mine, other, false); }
        return Native.GetForegroundWindow() == target;
    }
    static void CloseOwnedWindow()
    {
        if (!closeWindow || !OwnedIdentity()) return;
        if (kind == "notepad")
        {
            Chord(0x11, 0x57);
            Wait(() => !OwnedIdentity(), "close disposable Notepad tab");
            return;
        }
        Native.PostMessage(window, 0x10, 0, 0);
        try { Wait(() => !Native.IsWindow(window), "owned window close", 5000); }
        catch (Exception e) { Console.Error.WriteLine("Cleanup incomplete: " + e.Message + "; " + directory); }
    }
    static void Diagnostics(string reason)
    {
        try
        {
            if (!OwnedIdentity()) return;
            var title = Title(window);
            Console.WriteLine($"DIAGNOSTIC {reason}: foreground={Native.GetForegroundWindow() == window}; HKL={(long)Layout():x}; title={title}");
            if (kind == "brave-google" && cdp is not null) Console.WriteLine("DIAGNOSTIC Google field=" + GoogleField());
            if (Native.GetForegroundWindow() == window)
            {
                var focused = AutomationElement.FocusedElement;
                var root = focused;
                while (root is not null && root.Current.NativeWindowHandle != (int)window) root = TreeWalker.ControlViewWalker.GetParent(root);
                if (root is not null) Console.WriteLine($"DIAGNOSTIC owned UIA focus control={focused.Current.ControlType.ProgrammaticName}; class={focused.Current.ClassName}");
            }
            var field = editor;
            if (field is null && kind != "notepad")
                field = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,
                    new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "owned"),
                        new PropertyCondition(AutomationElement.NameProperty, "Disposable BetterRightLang live test")));
            if (field is null) { Console.WriteLine("DIAGNOSTIC owned-field UIA unavailable (browser accessibility not forced)."); return; }
            var hasText = field.TryGetCurrentPattern(TextPattern.Pattern, out var text);
            var hasValue = field.TryGetCurrentPattern(ValuePattern.Pattern, out var value);
            Console.WriteLine($"DIAGNOSTIC owned-field control={field.Current.ControlType.ProgrammaticName}; focus={field.Current.HasKeyboardFocus}; TextPattern={hasText}; ValuePattern={hasValue}");
            if (hasValue) Console.WriteLine("DIAGNOSTIC owned-field value=" + ((ValuePattern)value).Current.Value);
            else if (hasText && kind == "notepad")
            {
                var pattern = (TextPattern)text;
                Console.WriteLine("DIAGNOSTIC owned-field value=" + pattern.DocumentRange.GetText(300));
                Console.WriteLine("DIAGNOSTIC owned-field selection=" + string.Join("|", pattern.GetSelection().Select(r => r.GetText(100))));
            }
        }
        catch (Exception e) { Console.WriteLine("DIAGNOSTIC unavailable: " + e.Message); }
    }
    static void ConnectGoogle()
    {
        var portFile = Path.Combine(directory, "profile", "DevToolsActivePort");
        Wait(() => File.Exists(portFile), "owned Brave debugging endpoint", 15000);
        var port = int.Parse(File.ReadAllLines(portFile)[0]);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        string socket = "";
        Wait(() =>
        {
            using var targets = JsonDocument.Parse(http.GetStringAsync($"http://127.0.0.1:{port}/json/list").GetAwaiter().GetResult());
            var pages = targets.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() == "page" && IsGoogleUrl(t.GetProperty("url").GetString()!)).ToArray();
            if (pages.Length != 1) return false;
            socket = pages[0].GetProperty("webSocketDebuggerUrl").GetString()!; return true;
        }, "one isolated Google page", 15000);
        var endpoint = new Uri(socket); Require(endpoint.Host is "127.0.0.1" or "localhost" && endpoint.Port == port, "CDP endpoint must stay on owned loopback port");
        cdp = new ClientWebSocket(); cdp.ConnectAsync(endpoint, CancellationToken.None).GetAwaiter().GetResult();
        Require(GoogleField().GetProperty("value").GetString() == "", "Google field must initially be blank");
    }
    static bool IsGoogleUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host is "www.google.com" or "google.com" && uri.AbsolutePath == "/";
    static void FocusGoogle()
    {
        Require(OwnedIdentity() && Native.GetForegroundWindow() == window, "owned Google window must remain foreground before refocusing its field");
        GoogleField();
        Command("Page.bringToFront", new { });
        for (int attempt = 0; attempt < 12; attempt++)
        {
            Require(OwnedIdentity() && Native.GetForegroundWindow() == window, "foreground changed while focusing Google field");
            var promotion = Evaluate("(()=>{const ds=[...document.querySelectorAll('[role=dialog],[aria-modal=true]')].filter(d=>d.getClientRects().length);if(!ds.length)return 'none';for(const d of ds){const t=d.innerText||'';if(!(t.includes('เลือก Chrome เบราว์เซอร์จาก Google')||t.includes('Choose Chrome, the browser from Google')||t.includes('Switch to Chrome')))continue;const bs=[...d.querySelectorAll('button,[role=button]')].filter(b=>['ไม่ใช้ Chrome','No thanks',\"Don't use Chrome\"].includes(b.innerText.trim()));if(bs.length===1){bs[0].click();return 'declined Chrome promotion'}}return 'unrecognized dialog'})()").GetString();
            Require(promotion != "unrecognized dialog", "unrecognized Google dialog; no input sent");
            if (promotion == "declined Chrome promotion") { Console.WriteLine("Declined Google Chrome promotion in disposable profile."); Thread.Sleep(200); }
            Evaluate("(()=>{const e=[...document.querySelectorAll('textarea[name=q],input[name=q]')].find(e=>e.getClientRects().length);if(!e)throw Error('visible Google query field missing');e.focus();return true})()");
            if (GoogleField().GetProperty("focus").GetBoolean()) return;
            Thread.Sleep(150);
        }
        throw new InvalidOperationException("Google textbox could not regain document focus");
    }
    static JsonElement GoogleField()
    {
        var result = Evaluate("(()=>{const e=[...document.querySelectorAll('textarea[name=q],input[name=q]')].find(e=>e.getClientRects().length),a=document.activeElement;return {url:location.href,value:e?.value,focus:document.hasFocus()&&a===e,documentFocus:document.hasFocus(),activeTag:a?.tagName,activeId:a?.id,activeRole:a?.getAttribute('role'),start:e?.selectionStart,end:e?.selectionEnd}})()");
        Require(IsGoogleUrl(result.GetProperty("url").GetString()!), "Google page navigated away; no input permitted");
        Require(result.TryGetProperty("value", out _), "visible Google query field unavailable");
        return result;
    }
    static JsonElement Evaluate(string expression)
    {
        var result = Command("Runtime.evaluate", new { expression, returnByValue = true });
        Require(!result.TryGetProperty("exceptionDetails", out _), "Google field evaluation failed");
        return result.GetProperty("result").GetProperty("value").Clone();
    }
    static JsonElement Command(string method, object parameters)
    {
        using var timeout = new CancellationTokenSource(4000);
        int id = ++requestId;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        cdp!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
        while (true)
        {
            using var message = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult received;
            do { received = cdp.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).GetAwaiter().GetResult(); message.Write(buffer, 0, received.Count); } while (!received.EndOfMessage);
            using var json = JsonDocument.Parse(message.ToArray());
            if (!json.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
            return json.RootElement.GetProperty("result").Clone();
        }
    }
    static nint FocusedControl()
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        var thread = Native.GetWindowThreadProcessId(window, out _);
        Require(thread != 0 && Native.GetGUIThreadInfo(thread, ref info) && info.Focus != 0 &&
            (info.Focus == window || Native.IsChild(window, info.Focus)), "keyboard focus must belong to the owned window");
        return info.Focus;
    }
    static nint Layout() => Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(FocusedControl(), out _));
    static void SetLayout(nint layout)
    {
        Guard(); Native.PostMessage(FocusedControl(), 0x50, 0, layout);
        Wait(() => (uint)(long)Layout() == (uint)(long)layout, "requested HKL " + ((long)layout).ToString("x")); Thread.Sleep(150);
    }
    static bool Caps()
    {
        // GetKeyState uses the test thread's input queue. Ask the actual focused browser about a harmless test key.
        if (kind is "edge" or "chrome" or "brave-local")
        {
            string Read() => Title(window).Split('|').FirstOrDefault(s => s.StartsWith("caps-")) ?? "";
            var previous = Read();
            for (var attempt = 0; attempt < 10 && Read() == previous; attempt++)
            {
                Key(0x11);
                Thread.Sleep(100);
            }
            Require(Read() != previous, "Caps Lock state from the focused browser");
            return Read().EndsWith("-on", StringComparison.Ordinal);
        }
        if (kind == "brave-google")
        {
            Guard();
            Evaluate("(()=>{const e=document.activeElement;delete e.dataset.brlCaps;function read(k){if(k.key!=='Control')return;e.removeEventListener('keydown',read,true);e.dataset.brlCaps=k.getModifierState('CapsLock')?'on':'off'}e.addEventListener('keydown',read,true);return true})()");
            Key(0x11);
            string? state = null;
            Wait(() => (state = Evaluate("document.activeElement.dataset.brlCaps || ''").GetString()) is "on" or "off", "Caps Lock state from the Google field");
            return state == "on";
        }
        Native.PeekMessage(out _, 0, 0, 0, 0);
        return (Native.GetKeyState(0x14) & 1) != 0;
    }
    static void SetCaps(bool wanted)
    {
        if (Caps() == wanted) return;
        Key(0x14);
        Wait(() => Caps() == wanted, "Caps Lock in requested state");
    }
    static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0);
    static ushort[] PhysicalKeys(string text) => text.Select(c => c switch { ';' => (ushort)0xba, ' ' => (ushort)0x20, >= 'a' and <= 'z' => (ushort)char.ToUpperInvariant(c), >= '0' and <= '9' => (ushort)c, _ => throw new ArgumentException("fixture contains unsupported physical key") }).ToArray();
    static void Type(string text, int pace)
    {
        var keys = PhysicalKeys(text);
        if (pace == 0) { Send(keys.SelectMany(k => new[] { Event(k, false), Event(k, true) }).ToArray()); return; }
        foreach (var key in keys) { Key(key); Thread.Sleep(pace); }
    }
    static void Key(ushort key) => Send(new[] { Event(key, false), Event(key, true) });
    static void Chord(ushort modifier, ushort key)
    {
        Send(new[] { Event(modifier, false) });
        try
        {
            Thread.Sleep(30);
            Guard();
            SendEvents(new[] { Event(key, false), Event(key, true) });
            Thread.Sleep(30);
        }
        finally { SendEvents(new[] { Event(modifier, true) }); }
    }
    static Input Event(ushort key, bool up) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput {
        Key = key, Scan = (ushort)Native.MapVirtualKey(key, 0), Flags = (up ? 2u : 0u) | (key is >= 0x21 and <= 0x28 or 0x2e ? 1u : 0u) } } };
    static void Send(Input[] inputs)
    {
        var timer = Stopwatch.StartNew();
        while (ModifiersDown() && timer.ElapsedMilliseconds < 500) Thread.Sleep(5);
        Require(!ModifiersDown(), "keyboard modifiers busy; input aborted");
        Guard(); SendEvents(inputs);
    }
    static void SendEvents(Input[] inputs) => Require(Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length, "SendInput incomplete");
    static string Title(nint h) { var text = new StringBuilder(4096); Native.GetWindowText(h, text, text.Capacity); return text.ToString(); }
    static List<nint> Windows() { var result = new List<nint>(); Native.EnumWindows((h, _) => { if (Native.IsWindowVisible(h)) result.Add(h); return true; }, 0); return result; }
    static void Wait(Func<bool> predicate, string reason, int timeout = 4000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeout) { if (predicate()) return; Thread.Sleep(30); }
        throw new InvalidOperationException("timeout: " + reason);
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Message { public nint Window; public uint Kind; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] struct ScreenPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    static class Native
    {
        [DllImport("user32.dll")] internal static extern uint MapVirtualKey(uint vk, uint map);
        internal delegate bool EnumCallback(nint window, nint state);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, nint state);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool IsChild(nint parent, nint child);
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(ScreenPoint point);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out ScreenPoint point);
        [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool BringWindowToTop(nint window);
        [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(nint window, int command);
        [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message message, nint window, uint first, uint last, uint remove);
        [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint origin, uint target, bool attach);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder text, int length);
        [DllImport("user32.dll")] internal static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    }
}


