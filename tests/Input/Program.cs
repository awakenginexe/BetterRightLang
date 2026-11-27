using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using BetterRightLang.Core;
using BetterRightLang.Desktop;

internal static class Program
{
    private static int checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            var engine = new LayoutEngine(Path.Combine(root, "Data/en.txt"), Path.Combine(root, "Data/th.txt"));
            if (Environment.GetEnvironmentVariable("BRL_PRIVATE_DESKTOP_TEST") == "1")
            {
                RawLifecycle(engine);
                Console.WriteLine($"PASS {checks} raw input lifecycle checks.");
                return 0;
            }
            using var fixture = new Form { ShowInTaskbar = false };
            using var edit = new TextBox { Multiline = true, Parent = fixture, Width = 400 };
            using var rich = new RichTextBox { Parent = fixture, Top = 60 };
            // Target only our unshown native controls; no global synthetic input or foreground changes.
            CheckControl(edit, engine);
            CheckControl(rich, engine);
            RangeChecks(edit, engine);
            RangeChecks(rich, engine);
            edit.PasswordChar = '*';
            Check(NativeEdit.Read(edit.Handle) is null, "password edit is excluded");
            edit.PasswordChar = '\0'; edit.ReadOnly = true;
            Check(NativeEdit.Read(edit.Handle) is null, "read-only edit is excluded");
            edit.ReadOnly = false; rich.ReadOnly = true;
            Check(NativeEdit.Read(rich.Handle) is null, "read-only RichEdit is excluded");
            Check(Marshal.SizeOf<Native.Input>() == 40 && Marshal.SizeOf<Native.RawKeyboard>() == 40, "x64 native structures");
            Check(Native.IsSupportedLayout(0x04090409) && Native.IsSupportedLayout(0x041e041e), "US and Kedmanee accepted");
            Check(!Native.IsSupportedLayout(new nint(unchecked((int)0xf0020409))) && !Native.IsSupportedLayout(new nint(unchecked((int)0xf002041e))), "alternate US/Thai layouts rejected");
            Check(DesktopInput.DestinationLayoutId("hello สวัสดี") == 0x041e041e, "mixed selection ends in Thai");
            Check(DesktopInput.DestinationLayoutId("สวัสดี hello!") == 0x04090409, "mixed selection ends in English");
            Check(DesktopInput.DestinationLayoutId("123 !? ") == 0, "neutral selection leaves layout unchanged");
            var keys = EditBatch.Build("สวัสดีพี่เจได", "l;ylfurujg0wf", false, ("", " "))!;
            var presses = keys.Select(i => i.Data.Keyboard).Where(k => (k.Flags & Native.KeyUp) == 0).ToArray();
            Check(keys.All(i => i.Data.Keyboard.Extra == EditBatch.Tag), "every event in the same batch can be vetoed");
            Check(presses.Last().Key == 0x25, "caret restored before the trailing space");
            Check(presses.SkipWhile(k => k.Key != 0xa0).Skip(1).TakeWhile(k => k.Key == 0x27).Count() == 9, "select nine Thai caret stops");
            Check(new string(presses.Where(k => (k.Flags & Native.Unicode) != 0).Select(k => (char)k.Scan).ToArray()) == "l;ylfurujg0wf", "Unicode replacement stays in order");
            Check(EditBatch.Build("a", "b", true)!.Length == 2, "existing user selection is replaced directly");
            Check(EditBatch.Build("a", "b", false) is null, "current-word editing requires verified field edges");
            // The text before the caret has six caret stops and the text after the word has three; each edge gets spare arrow presses.
            var anchored = EditBatch.Build("abc", "ค", false, ("e\u0301 ", " z"), " ")!.Select(i => i.Data.Keyboard)
                .Where(k => (k.Flags & Native.KeyUp) == 0).Select(k => (int)k.Key).ToArray();
            Check(anchored.SequenceEqual(Enumerable.Repeat(0x25, 6 + 4).Concat(new[] { 0x27, 0x27, 0xa0, 0x27, 0x27, 0x27, 0 })
                .Concat(Enumerable.Repeat(0x27, 3 + 4)).Concat(new[] { 0x25, 0x25 })),
                "Chromium reaches both field edges by single arrow presses when the rendered caret advances and Home/End are reserved");
            Check(!EditBatch.Build("abc", "x", false, ("prefix ", ""))!.Any(i => i.Data.Keyboard.Key is 0xa2 or 0x41),
                "corrections never send Ctrl shortcuts such as Select All, which ProseMirror replaces entirely with the next typed character");
            Check(EditBatch.CaretStopAtOrBefore("สวัสดี ้ำสสน ", 7) == 6, "a word starting with a Thai mark begins at the space it joins");
            Check(EditBatch.CaretStopAtOrBefore("hello world", 6) == 6 && EditBatch.CaretStopAtOrBefore("้ำสสน", 0) == 0,
                "a plain word start is already a caret stop");
            Check(EditBatch.Build("a", "\r", false, ("", "")) is null, "generic input never synthesizes Enter");
            Check(EditBatch.NativeMarker().Single().Data.Keyboard.Flags == (Native.Unicode | Native.KeyUp), "native edit uses a harmless key-up marker");
            var firstSelection = Array.FindIndex(keys, k => k.Data.Keyboard.Key == 0x27);
            var interrupted = keys.AsSpan(0, firstSelection + 1);
            Check(EditBatch.EndPartial(interrupted, true).Select(k => k.Data.Keyboard.Key).SequenceEqual(new ushort[] { 0xa0, 0x27, 0x27 }),
                "a partially submitted selection releases Shift and collapses at its end");
            Check(EditBatch.EndPartial(interrupted, false).Single().Data.Keyboard.Flags == Native.KeyUp,
                "interrupted input releases the modifier without moving another field's caret");
            var edgeMove = keys.AsSpan(0, Array.FindIndex(keys, k => k.Data.Keyboard.Key == 0x25) + 1);
            Check(EditBatch.EndPartial(edgeMove, true).Length == 0 && EditBatch.EndPartial(edgeMove, false).Length == 0,
                "an interrupted move to the field edge leaves no key held and no selection");
            Lifecycle(engine);
            Console.WriteLine($"PASS {checks} native input checks.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void CheckControl(TextBoxBase edit, LayoutEngine engine)
    {
        edit.Text = "prefix l;ylfurujg0wf suffix";
        edit.Select(20, 0);
        var snapshot = NativeEdit.Read(edit.Handle);
        Check(snapshot is not null, edit.GetType().Name + " readable");
        var word = LayoutEngine.GetWordBeforeCaret(snapshot!.Text!, snapshot.Start);
        Check(word?.Text == "l;ylfurujg0wf", "manual full word span");
        var converted = engine.Convert(word!.Value.Text);
        Check(converted == "สวัสดีพี่เจได", "full phrase conversion");
        Check(ApplyToFixture(snapshot, word.Value.Start, word.Value.Length, word.Value.Text, converted, () => true, () => true), "targeted replacement");
        Check(edit.Text == "prefix สวัสดีพี่เจได suffix", "prefix and suffix preserved");
        if (edit is not RichTextBox)
        {
            edit.Undo();
            Check(edit.Text == snapshot.Text, "standard EDIT undo restores correction");
        }
        else edit.Text = snapshot.Text;

        edit.Select(7, 13);
        snapshot = NativeEdit.Read(edit.Handle)!;
        Check(ApplyToFixture(snapshot, 7, 13, "l;ylfurujg0wf", converted, () => true, () => true), "selected phrase replacement");
        Check(edit.Text == "prefix สวัสดีพี่เจได suffix", "selection scope preserved");

        edit.Text = "hello "; edit.Select(edit.TextLength, 0);
        snapshot = NativeEdit.Read(edit.Handle)!;
        Check(!ApplyToFixture(snapshot, 0, 5, "stale", "wrong", () => true, () => true), "expected text mismatch rejected");
        Check(!ApplyToFixture(snapshot, 0, 5, "hello", "wrong", () => false, () => true), "focus/context guard rejected");
        edit.AppendText("x");
        Check(!ApplyToFixture(snapshot, 0, 5, "hello", "wrong", () => true, () => true), "stale snapshot rejected");

        edit.Text = "l;ylfu "; edit.Select(edit.TextLength, 0);
        snapshot = NativeEdit.Read(edit.Handle)!;
        var automatic = engine.Suggest("l;ylfu", true);
        Check(automatic == "สวัสดี", "auto boundary suggestion");
        Check(ApplyToFixture(snapshot, 0, 7, "l;ylfu ", automatic + " ", () => true, () => true), "boundary native replacement");
        Check(edit.Text == "สวัสดี " && edit.SelectionStart == edit.TextLength, "typed boundary and caret preserved");

        edit.Text = "";
        var switched = false;
        foreach (var physicalKey in "l;ylfurujg0wf")
        {
            var typed = switched ? engine.Convert(physicalKey.ToString()) : physicalKey.ToString();
            Native.SendMessageTimeout(edit.Handle, Native.EmReplaceSel, 1, typed, 2, 150, out _);
            snapshot = NativeEdit.Read(edit.Handle)!;
            var span = LayoutEngine.GetWordBeforeCaret(snapshot.Text!, snapshot.Start);
            var suggestion = span is null ? null : engine.Suggest(span.Value.Text);
            if (suggestion is null) continue;
            Check(ApplyToFixture(snapshot, span!.Value.Start, span.Value.Length, span.Value.Text, suggestion, () => true, () => true), "partial native replacement");
            switched = true;
        }
        Check(switched && edit.Text == "สวัสดีพี่เจได", "paced partial correction and remaining physical keys");
    }

    private static void Lifecycle(LayoutEngine engine)
    {
        using (var input = new DesktopInput(engine, () => CorrectionMode.Paused, () => { }))
        {
            Check(input.StatusError is null, "paused service starts: " + input.StatusError);
            input.RefreshMode(); Thread.Sleep(80);
            Check(input.StatusError is null, "pause unregisters observation: " + input.StatusError);
            var probe = new NativeWindow();
            probe.CreateHandle(new CreateParams { Parent = new nint(-3) });
            try
            {
                foreach (var binding in new[] { (91, Native.ModControl, 0x14u), (92, Native.ModShift, 8u), (93, Native.ModShift, 0x14u) })
                {
                    var registered = Native.RegisterHotKey(probe.Handle, binding.Item1, binding.Item2 | Native.ModNoRepeat, binding.Item3);
                    Check(registered, "paused service releases hotkey " + binding.Item1);
                    Native.UnregisterHotKey(probe.Handle, binding.Item1);
                }
            }
            finally { probe.DestroyHandle(); }
        }
        using var restarted = new DesktopInput(engine, () => CorrectionMode.Paused, () => { });
        Check(restarted.StatusError is null, "disposed resources can be registered again: " + restarted.StatusError);
    }

    private static bool ApplyToFixture(NativeEdit snapshot, int start, int length, string expected, string replacement,
        Func<bool> current, Func<bool> focused)
    {
        if (!current() || !focused() || snapshot.Text is null || NativeEdit.Read(snapshot.Window) != snapshot ||
            start < 0 || start > snapshot.Text.Length - length || length != expected.Length) return false;
        var selected = snapshot.Start != snapshot.End;
        var trailing = selected ? "" : snapshot.Text[(start + length)..snapshot.Start];
        return snapshot.Commit(expected + trailing, replacement + trailing, selected);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices([Out] Native.RawDevice[]? devices, ref uint count, uint size);

    private static bool KeyboardRegistered()
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<Native.RawDevice>();
        if (GetRegisteredRawInputDevices(null, ref count, size) == uint.MaxValue) throw new System.ComponentModel.Win32Exception();
        if (count == 0) return false;
        var devices = new Native.RawDevice[count];
        if (GetRegisteredRawInputDevices(devices, ref count, size) == uint.MaxValue) throw new System.ComponentModel.Win32Exception();
        foreach (var device in devices) if (device.Page == 1 && device.Usage == 6) return device.Target != 0 && (device.Flags & 0x100) != 0;
        return false;
    }

    private static void RawLifecycle(LayoutEngine engine)
    {
        var mode = CorrectionMode.Auto;
        using (var input = new DesktopInput(engine, () => mode, () => { }))
        {
            Check(KeyboardRegistered(), "Auto registers raw keyboard: " + input.StatusError);
            foreach (var next in new[] { CorrectionMode.Manual, CorrectionMode.Auto, CorrectionMode.Paused, CorrectionMode.Auto, CorrectionMode.Manual, CorrectionMode.Auto })
            {
                mode = next;
                input.RefreshMode();
                var start = Environment.TickCount64;
                while (KeyboardRegistered() != (next == CorrectionMode.Auto) && Environment.TickCount64 - start < 1000) Thread.Sleep(10);
                Check(KeyboardRegistered() == (next == CorrectionMode.Auto), next + " raw keyboard registration: " + input.StatusError);
                Check(input.StatusError?.Contains("Automatic keyboard observation") != true, next + " refresh succeeds: " + input.StatusError);
            }
        }
        Check(!KeyboardRegistered(), "dispose releases raw keyboard");
        using (var restarted = new DesktopInput(engine, () => CorrectionMode.Auto, () => { }))
            Check(KeyboardRegistered(), "restart registers raw keyboard: " + restarted.StatusError);
        Check(!KeyboardRegistered(), "restarted service releases raw keyboard");
    }

    private static void RangeChecks(TextBoxBase edit, LayoutEngine engine)
    {
        edit.MaxLength = int.MaxValue;
        edit.Text = "l;ylf"; edit.Select(edit.TextLength, 0);
        var handle = edit.Handle;
        var before = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false));
        Check(before?.Context == "l;ylf", edit.GetType().Name + " UIA localized context");
        Native.SendMessageTimeout(handle, Native.EmReplaceSel, 1, "u", 2, 150, out _);
        var after = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false));
        Check(after?.Context == "l;ylfu", "UIA inserted character visible");
        Check(OffThread(() => before!.IsAppendOf(after!, false)), "UIA anchored append proof");
        Check(!OffThread(() => after!.IsAppendOf(after, false)), "unchanged/rejected key cannot trigger auto");

        edit.Text = ""; edit.Select(0, 0);
        before = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false, layout: 0x041e041e));
        const string burst = "l;ylfurujg0wf";
        Native.SendMessageTimeout(handle, Native.EmReplaceSel, 1, burst, 2, 150, out _);
        after = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false, layout: 0x04090409));
        Check(OffThread(() => before!.IsAppendOf(after!, false, burst.Length)), "fast phrase after changing layout is proved against the same editor");
        Check(!OffThread(() => before!.IsAppendOf(after!, false)), "burst cannot exceed observed key count");
        Check(!OffThread(() => after!.IsAppendOf(after!, false, burst.Length)), "duplicate unchanged observations cannot prove another edit");
        var burstSpan = LayoutEngine.GetWordBeforeCaret(after!.Context, after.Context.Length)!.Value;
        var burstSuggestion = engine.Suggest(burstSpan.Text);
        var burstNative = NativeEdit.Read(handle)!;
        Check(burstSuggestion == "สวัสดีพี่เจได" && ApplyToFixture(burstNative, burstSpan.Start, burstSpan.Length, burstSpan.Text, burstSuggestion, () => true, () => true), "full fast burst auto plan replaces the verified word");
        Check(edit.Text == "สวัสดีพี่เจได", "full fast burst yields the complete requested phrase");

        edit.Text = "l;ylfu "; edit.Select(6, 0);
        before = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false));
        edit.Select(7, 0);
        after = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false));
        Check(!OffThread(() => before!.IsAppendOf(after!, true)), "moving over existing space is not typing");

        edit.Text = "l;ylfu"; edit.Select(6, 0);
        before = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false));
        Native.SendMessageTimeout(handle, Native.EmReplaceSel, 1, " ", 2, 150, out _);
        after = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false));
        Check(OffThread(() => before!.IsAppendOf(after!, true)), "newly typed boundary is proved");

        edit.Text = "l;ylfu suffix"; edit.Select(2, 0);
        Check(OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false)) is null, "caret inside a word is excluded");

        edit.Text = new string('a', 300) + "l;ylfu"; edit.Select(edit.TextLength, 0);
        before = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false));
        Check(before is not null && !before.HasWordStart(0), "truncated long word cannot be converted");

        edit.Text = new string('a', 70000) + " l;ylf"; edit.Select(edit.TextLength, 0);
        before = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false));
        Check(before is not null && before.Context.Length == 256 && before.HasWordStart(before.Context.Length - 5), "long document uses bounded UIA context");
        Native.SendMessageTimeout(handle, Native.EmReplaceSel, 1, "u", 2, 150, out _);
        after = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false));
        Check(OffThread(() => before!.IsAppendOf(after!, false)), "append proof survives sliding context in long document");
        var large = NativeEdit.Read(handle)!;
        Check(large.Text is null && large.Start == edit.TextLength && large.Start > 65535, "32-bit caret without copying a large native document");
        Check(large.Commit("l;ylfu", "สวัสดี", false) && edit.Text.EndsWith(" สวัสดี", StringComparison.Ordinal), "large native edit changes only verified word");

        edit.Text = "prefix สวัสดีพี่เจได "; edit.Select(edit.TextLength, 0);
        before = OffThread(() => ForegroundEditor.ReadSelection(AutomationElement.FromHandle(handle), false));
        Check(OffThread(() => before!.FindReplacementRange("สวัสดีพี่เจได ", false)?.GetText(100)) == "สวัสดีพี่เจได ", "Thai marks located by text rather than character-unit offsets");
        Check(OffThread(() => before!.FindReplacementRange("prefix", false)) is null, "replacement must end at verified caret");
    }

    private static T OffThread<T>(Func<T> action)
    {
        var task = Task.Run(action);
        var start = Environment.TickCount64;
        while (!task.IsCompleted && Environment.TickCount64 - start < 5000) { Application.DoEvents(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new TimeoutException("Fixture UIA provider did not respond.");
        return task.GetAwaiter().GetResult();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        checks++;
    }
}
