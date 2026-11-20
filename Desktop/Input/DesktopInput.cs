using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using BetterRightLang.Core;

namespace BetterRightLang.Desktop;

public sealed class DesktopInput : IDisposable
{
    private readonly LayoutEngine engine;
    private readonly Func<CorrectionMode> getMode;
    private readonly Action toggleMode;
    private readonly SynchronizationContext? caller;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new(false);
    private InputWindow? window;
    private string? statusError;
    private bool disposed;
    public event Action? StatusChanged;
    public event Action<string, string, bool>? Corrected;
    public string? StatusError
    {
        get => Volatile.Read(ref statusError);
        private set
        {
            if (Interlocked.Exchange(ref statusError, value) != value) StatusChanged?.Invoke();
        }
    }

    public DesktopInput(LayoutEngine engine, Func<CorrectionMode> getMode, Action toggleMode)
    {
        this.engine = engine; this.getMode = getMode; this.toggleMode = toggleMode;
        caller = SynchronizationContext.Current;
        thread = new Thread(Run) { IsBackground = true, Name = "BetterRightLang input" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(3000)) StatusError = "Input service did not start within three seconds.";
    }

    public void RefreshMode()
    {
        if (!disposed && window is { } active) Native.PostMessage(active.Handle, Native.WmRefresh, 0, 0);
    }

    private void Run()
    {
        try
        {
            using var input = new InputWindow(this);
            window = input;
            ready.Set();
            Application.Run();
        }
        catch (Exception exception) { StatusError = "Input service unavailable: " + exception.Message; ready.Set(); }
        finally { window = null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (window is { } active) Native.PostMessage(active.Handle, 0x10, 0, 0);
        if (Thread.CurrentThread != thread) thread.Join(3000);
        if (!thread.IsAlive) ready.Dispose();
    }

    internal static uint DestinationLayoutId(string replacement)
    {
        for (var i = replacement.Length - 1; i >= 0; i--)
        {
            if (char.IsAsciiLetter(replacement[i])) return 0x04090409;
            if (replacement[i] is >= '\u0e00' and <= '\u0e7f' && char.IsLetter(replacement[i])) return 0x041e041e;
        }
        return 0;
    }

    private sealed class InputWindow : NativeWindow, IDisposable
    {
        private readonly DesktopInput owner;
        private readonly CapsLockShortcuts capsShortcuts;
        private bool rawInput, wordHotkey, selectionHotkey, toggleHotkey;
        private nint foregroundHook, focusHook;
        private readonly Native.WinEventCallback focusChanged;
        private Pending? pending;
        private ForegroundEditor? typingBaseline;
        private int remainingBaselineAttempts;
        private CorrectionMode mode;
        private sealed record Pending(nint Foreground, nint Focus, nint Layout, long Time, int Action, bool Boundary, ForegroundEditor? Snapshot, int KeyCount = 1);

        internal InputWindow(DesktopInput owner)
        {
            this.owner = owner;
            focusChanged = (_, _, _, _, _, _, _) => Native.PostMessage(Handle, Native.WmRefresh + 1, 0, 0);
            CreateHandle(new CreateParams { Caption = "BetterRightLang input", Parent = new nint(-3) });
            capsShortcuts = new CapsLockShortcuts(Handle);
            Refresh();
        }

        protected override void WndProc(ref Message message)
        {
            try
            {
                switch (message.Msg)
                {
                    case Native.WmInput: ObserveRawInput(message.LParam); break;
                    case Native.WmLocalKey:
                        if (!rawInput && message.LParam == Native.GetForegroundWindow()) ObserveKey((int)message.WParam);
                        break;
                    case Native.WmHotkey:
                        if (message.WParam == 2) QueueManual(2);
                        break;
                    case Native.WmCapsShortcut:
                        if (message.WParam == 0)
                        {
                            owner.StatusError = $"Caps Lock shortcuts are unavailable (Windows error {message.LParam}).";
                            break;
                        }
                        if ((int)message.WParam == 1)
                        {
                            pending = null;
                            if (owner.caller is { } context) context.Post(_ => ToggleMode(), null);
                            else ToggleMode();
                        }
                        else if (message.WParam == 3 && selectionHotkey) QueueManual(3);
                        break;
                    case Native.WmRefresh: Refresh(); break;
                    case Native.WmRefresh + 1: UpdateHotkeys(); break;
                    case Native.WmTimer:
                        if (message.WParam == 1) ApplyPending();
                        else if (message.WParam == 2) { Native.KillTimer(Handle, 2); UpdateHotkeys(); }
                        else if (message.WParam == 3)
                        {
                            Native.KillTimer(Handle, 3);
                            if (pending is null && mode == CorrectionMode.Auto)
                            {
                                typingBaseline = ForegroundEditor.Capture(false);
                                if (typingBaseline is null && --remainingBaselineAttempts > 0) Native.SetTimer(Handle, 3, 60, 0);
                            }
                        }
                        break;
                    case 0x10: Dispose(); Application.ExitThread(); return;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                pending = null;
                Native.KillTimer(Handle, 1);
                // Providers disappear during focus changes; leave the user's text untouched.
                if (exception is not System.Windows.Automation.ElementNotAvailableException &&
                    exception is not InvalidOperationException && exception is not COMException)
                    owner.StatusError = "Input operation unavailable: " + exception.Message;
            }
            base.WndProc(ref message);
        }

        private void Error(string message) => owner.StatusError = message + " (Windows error " + Marshal.GetLastWin32Error() + ").";

        private void ToggleMode()
        {
            if (!owner.disposed && owner.getMode() != CorrectionMode.Paused) owner.toggleMode();
        }

        private void Refresh()
        {
            mode = owner.getMode();
            owner.StatusError = null;
            pending = null;
            typingBaseline = null;
            Native.KillTimer(Handle, 1);
            Native.KillTimer(Handle, 2);
            Native.KillTimer(Handle, 3);
            var enabled = mode != CorrectionMode.Paused;
            if (enabled && !toggleHotkey)
            {
                toggleHotkey = Native.RegisterHotKey(Handle, 1, Native.ModControl | Native.ModNoRepeat, 0x14);
                if (!toggleHotkey) Error("Ctrl+CapsLock is already in use or unavailable");
            }
            if (!enabled && toggleHotkey) { Native.UnregisterHotKey(Handle, 1); toggleHotkey = false; }
            if (enabled && foregroundHook == 0)
            {
                foregroundHook = Native.SetWinEventHook(3, 3, 0, focusChanged, 0, 0, 0);
                focusHook = Native.SetWinEventHook(0x8005, 0x8005, 0, focusChanged, 0, 0, 0);
                if (foregroundHook == 0 || focusHook == 0) Error("Foreground observation is unavailable");
            }
            if (!enabled) ReleaseHooks();
            UpdateHotkeys();
            // Focus and accessibility can settle after Windows reports the initial foreground window.
            if (enabled) Native.SetTimer(Handle, 2, 180, 0);
        }

        private bool SetRawInput(bool enable)
        {
            if (enable != rawInput)
            {
                var devices = new[] { new Native.RawDevice { Page = 1, Usage = 6, Flags = enable ? Native.RidevInputSink : Native.RidevRemove, Target = enable ? Handle : 0 } };
                if (Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<Native.RawDevice>())) rawInput = enable;
                else Error("Automatic keyboard observation could not be " + (enable ? "enabled" : "disabled"));
            }
            return rawInput == enable;
        }

        private void UpdateHotkeys()
        {
            if (owner.getMode() != mode) { Refresh(); return; }
            var local = UpdateKeyboardObservation();
            var eligible = false;
            try { eligible = mode != CorrectionMode.Paused && IsEligibleForeground(); }
            catch (Exception exception) when (exception is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException or COMException) { }
            SetHotkey(ref wordHotkey, 2, Native.ModShift, 8, eligible);
            SetHotkey(ref selectionHotkey, 3, Native.ModShift, 0x14, eligible);
            capsShortcuts.SetBindings(toggleHotkey, selectionHotkey, local);
            if (mode == CorrectionMode.Auto)
            {
                typingBaseline = ForegroundEditor.Capture(false);
                if (eligible && typingBaseline is null) ScheduleBaseline();
            }
        }

        private nint UpdateKeyboardObservation()
        {
            nint local = 0;
            if (mode == CorrectionMode.Auto)
            {
                var foreground = Native.GetForegroundWindow();
                if (foreground != 0 && Native.GetWindowThreadProcessId(foreground, out var process) != 0 && process == Environment.ProcessId)
                    local = foreground;
            }
            // Raw input in our own foreground process can bypass its low-level shortcut filter.
            SetRawInput(mode == CorrectionMode.Auto && local == 0);
            capsShortcuts.SetBindings(toggleHotkey, mode != CorrectionMode.Paused && selectionHotkey, local);
            return local;
        }

        private void ScheduleBaseline()
        {
            // A selection change or layout switch can temporarily invalidate the provider's caret.
            remainingBaselineAttempts = 4;
            Native.SetTimer(Handle, 3, 30, 0);
        }

        private bool IsEligibleForeground()
        {
            var foreground = Native.GetForegroundWindow();
            if (foreground == 0) return false;
            var thread = Native.GetWindowThreadProcessId(foreground, out _);
            var info = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
            if (!Native.GetGUIThreadInfo(thread, ref info) || info.Focus == 0 || Native.IsProtectedEdit(info.Focus) || !Native.IsSupportedLayout(Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(info.Focus, out _)))) return false;
            // A standard edit HWND is identifiable before its accessibility provider finishes initializing.
            if (Native.IsNativeEdit(info.Focus)) return true;
            var element = System.Windows.Automation.AutomationElement.FocusedElement;
            if (element is null || !ForegroundEditor.IsElementInForeground(element, foreground) || !element.Current.HasKeyboardFocus ||
                element.Current.IsPassword || !element.Current.IsEnabled ||
                !element.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var raw)) return false;
            var ranges = ((System.Windows.Automation.TextPattern)raw).GetSelection();
            return ranges.Length == 1 && ranges[0].GetAttributeValue(System.Windows.Automation.TextPattern.IsReadOnlyAttribute) is false;
        }

        private void SetHotkey(ref bool registered, int id, uint modifiers, uint key, bool enable)
        {
            if (enable == registered) return;
            if (!enable) { Native.UnregisterHotKey(Handle, id); registered = false; }
            else if (Native.RegisterHotKey(Handle, id, modifiers | Native.ModNoRepeat, key)) registered = true;
            else Error(id == 2 ? "Shift+Backspace is already in use or unavailable" : "Shift+CapsLock is already in use or unavailable");
        }

        private void ObserveRawInput(nint handle)
        {
            if (!rawInput) return;
            var size = (uint)Marshal.SizeOf<Native.RawKeyboard>();
            if (Native.GetRawInputData(handle, 0x10000003, out var input, ref size, (uint)(IntPtr.Size == 8 ? 24 : 16)) == uint.MaxValue ||
                input.Type != 1 || (input.Flags & 1) != 0 || input.VKey == 0xe7 || input.Extra == EditBatch.Tag) return;
            ObserveKey(input.VKey);
        }

        private void ObserveKey(int key)
        {
            if (mode != CorrectionMode.Auto || owner.getMode() != mode || pending is { Action: 2 or 3 }) return;
            var boundary = key is 0x20 or 0x0d or 0x09;
            if (!(boundary || key is >= 0x30 and <= 0x5a or >= 0x60 and <= 0x6f or >= 0xba and <= 0xe2))
            {
                pending = null;
                typingBaseline = null;
                Native.KillTimer(Handle, 1);
                ScheduleBaseline();
                return;
            }
            if ((Native.GetAsyncKeyState(0x11) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x12) & 0x8000) != 0 ||
                (Native.GetAsyncKeyState(0x5b) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x5c) & 0x8000) != 0) return;
            var foreground = Native.GetForegroundWindow();
            if (foreground == 0) return;
            var thread = Native.GetWindowThreadProcessId(foreground, out _);
            var info = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
            if (!Native.GetGUIThreadInfo(thread, ref info) || info.Focus == 0) return;
            var layout = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(info.Focus, out _));
            if (!Native.IsSupportedLayout(layout)) return;
            if (pending is { Action: 0 } batch && batch.Foreground == foreground && batch.Focus == info.Focus && batch.Layout == layout && batch.KeyCount < 128)
                pending = batch with { Time = Environment.TickCount64, Boundary = boundary, KeyCount = batch.KeyCount + 1 };
            else pending = new Pending(foreground, info.Focus, layout, Environment.TickCount64, 0, boundary, typingBaseline);
            Native.SetTimer(Handle, 1, 45, 0);
        }

        private void QueueManual(int action)
        {
            if (owner.getMode() == CorrectionMode.Paused) return;
            var snapshot = ForegroundEditor.Capture(action == 3);
            if (snapshot is null) return;
            pending = new Pending(snapshot.Foreground, snapshot.Focus, snapshot.Layout, Environment.TickCount64, action, false, snapshot);
            Native.SetTimer(Handle, 1, 20, 0);
        }

        private void ApplyPending()
        {
            if (pending is not { } edit) { Native.KillTimer(Handle, 1); return; }
            var age = Environment.TickCount64 - edit.Time;
            if (age > 2000 || owner.getMode() == CorrectionMode.Paused || (edit.Action == 0 && owner.getMode() != CorrectionMode.Auto) ||
                Native.GetForegroundWindow() != edit.Foreground)
            { pending = null; Native.KillTimer(Handle, 1); return; }
            if (Native.ModifiersDown()) return;
            var inputTick = Native.LastInputTick();
            var whitelist = owner.engine.Whitelist;
            bool InputUnchanged() => inputTick.HasValue && Native.LastInputTick() == inputTick && owner.getMode() == mode &&
                (edit.Action != 0 || owner.engine.Whitelist == whitelist) &&
                !Native.PeekMessage(out _, Handle, Native.WmInput, Native.WmInput, 0) &&
                !Native.PeekMessage(out _, Handle, Native.WmLocalKey, Native.WmLocalKey, 0);
            void Retry()
            {
                if (pending is not null || age >= 1000 || owner.getMode() != mode) return;
                pending = edit;
                Native.SetTimer(Handle, 1, 35, 0);
            }
            var snapshot = edit.Snapshot;
            pending = null;
            Native.KillTimer(Handle, 1);
            if (edit.Action == 0)
            {
                var after = ForegroundEditor.Capture(false);
                if (!InputUnchanged()) { Retry(); return; }
                // Notepad can report the old or an unavailable caret just after a burst of key events.
                if (snapshot is not null && age < 250 && (after is null || after.Context == snapshot.Context))
                {
                    pending = edit;
                    Native.SetTimer(Handle, 1, 30, 0);
                    return;
                }
                typingBaseline = after;
                if (after is null) ScheduleBaseline();
                if (snapshot is null || after is null || !snapshot.IsAppendOf(after, edit.Boundary, edit.KeyCount)) return;
                snapshot = after;
            }
            if (snapshot is null) return;
            var wordContext = snapshot;
            if (edit.Action == 2 && !snapshot.IsCurrent())
            {
                var after = ForegroundEditor.Capture(false);
                if (after is null) { Retry(); return; }
                if (!snapshot.HasAppendedText(after)) return;
                snapshot = after;
            }
            if (snapshot.Focus != edit.Focus || snapshot.Layout != edit.Layout || !snapshot.IsCurrent())
            {
                if (edit.Action == 2) Retry();
                return;
            }
            string original, replacement;
            int start, length;
            if (edit.Action == 3)
            {
                original = snapshot.SelectedText; replacement = owner.engine.Convert(original); start = 0; length = original.Length;
            }
            else
            {
                var caret = wordContext.Context.Length;
                if (edit.Action == 0 && edit.Boundary && caret > 0 && snapshot.Context[caret - 1] is '\r' or '\n')
                {
                    caret--;
                    if (caret > 0 && snapshot.Context[caret] == '\n' && snapshot.Context[caret - 1] == '\r') caret--;
                }
                var span = LayoutEngine.GetWordBeforeCaret(wordContext.Context, caret);
                if (span is null || !span.Value.Matches(snapshot.Context) || !wordContext.HasWordStart(span.Value.Start)) return;
                if (snapshot != wordContext && span.Value.Start + span.Value.Length == caret)
                {
                    var end = caret;
                    while (end < snapshot.Context.Length && !char.IsWhiteSpace(snapshot.Context[end])) end++;
                    var grown = LayoutEngine.GetWordBeforeCaret(snapshot.Context, end);
                    if (grown?.Start == span.Value.Start) span = grown;
                }
                original = span.Value.Text; start = span.Value.Start; length = span.Value.Length;
                replacement = edit.Action == 2 ? owner.engine.Convert(original) : owner.engine.Suggest(span.Value, snapshot.Context, edit.Boundary) ?? original;
            }
            if (original == replacement) return;
            var destination = DestinationLayoutId(replacement);
            var layout = destination == 0 ? 0 : FindLoadedLayout(destination);
            if (destination != 0 && layout == 0 && edit.Action == 0)
            {
                owner.StatusError = "Automatic correction needs the " + (destination == 0x041e041e ? "Thai Kedmanee" : "English (United States) US") + " keyboard. Add it in Windows language settings.";
                return;
            }
            Native.GetWindowThreadProcessId(snapshot.Foreground, out var foregroundProcess);
            // With our own WebView2 in front, raw keyboard registration can bypass our low-level edit hook.
            var suspendRaw = rawInput && foregroundProcess == Environment.ProcessId;
            if (suspendRaw && !SetRawInput(false)) return;
            bool replaced;
            try { replaced = snapshot.Replace(start, length, original, replacement, edit.Action == 3, layout, InputUnchanged, snapshot != wordContext); }
            finally { if (suspendRaw) UpdateKeyboardObservation(); }
            if (replaced)
            {
                owner.Corrected?.Invoke(original, replacement, edit.Action == 0);
                if (layout == 0 && destination != 0) owner.StatusError = "Text corrected; its destination keyboard is unavailable. Add the US English or Thai Kedmanee keyboard in Windows language settings.";
                typingBaseline = null;
                if (mode == CorrectionMode.Auto) ScheduleBaseline();
            }
            else if (edit.Action == 2 || edit.Action == 0 && !InputUnchanged()) Retry();
        }

        private static nint FindLoadedLayout(uint destination)
        {
            var layouts = new nint[32];
            var count = GetKeyboardLayoutList(layouts.Length, layouts);
            for (var i = 0; i < count; i++)
                if ((uint)(long)layouts[i] == destination) return layouts[i];
            return 0;
        }

        [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] nint[] layouts);

        private void ReleaseHooks()
        {
            if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
            if (focusHook != 0) Native.UnhookWinEvent(focusHook);
            foregroundHook = focusHook = 0;
        }

        public void Dispose()
        {
            if (Handle == 0) return;
            pending = null;
            Native.KillTimer(Handle, 1);
            ReleaseHooks();
            if (toggleHotkey) Native.UnregisterHotKey(Handle, 1);
            if (wordHotkey) Native.UnregisterHotKey(Handle, 2);
            if (selectionHotkey) Native.UnregisterHotKey(Handle, 3);
            capsShortcuts.Dispose();
            SetRawInput(false);
            DestroyHandle();
        }
    }
}
