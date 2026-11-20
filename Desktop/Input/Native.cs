using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterRightLang.Desktop;

internal static class Native
{
    internal const int WmHotkey = 0x312, WmInput = 0xff, WmTimer = 0x113, WmRefresh = 0x8001;
    internal const int WmCapsShortcut = 0x8003, WmLocalKey = 0x8004;
    internal const int EmGetSel = 0xb0, EmSetSel = 0xb1, EmReplaceSel = 0xc2;
    internal const int WmGetTextLength = 0xe, WmGetText = 0xd;
    internal const uint KeyUp = 2, Unicode = 4;
    internal const uint ModShift = 4, ModControl = 2, ModNoRepeat = 0x4000;
    internal const uint RidevRemove = 0x1, RidevInputSink = 0x100;

    [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct RawDevice
    {
        internal ushort Page, Usage;
        internal uint Flags;
        internal nint Target;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct RawKeyboard
    {
        internal uint Type, Size;
        internal nint Device, WParam;
        internal ushort MakeCode, Flags, Reserved, VKey;
        internal uint Message, Extra;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
    {
        internal ushort Key, Scan;
        internal uint Flags, Time;
        internal nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal nuint Extra;
    }

    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    {
        public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private;
    }
    internal delegate nint HookProc(int code, nint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] internal static extern nint SetWindowsHookEx(int type, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] internal static extern int GetMessage(out Message message, nint window, uint first, uint last);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] internal static extern bool PeekMessage(out Message message, nint window, uint first, uint last, uint remove);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] internal static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? module);
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size, Tick; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    internal static uint? LastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Tick : null;
    }
    internal static bool HasFocus(nint foreground, nint focus, nint layout)
    {
        if (GetForegroundWindow() != foreground) return false;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info) && info.Focus == focus &&
            (layout == 0 || GetKeyboardLayout(GetWindowThreadProcessId(focus, out _)) == layout);
    }
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, StringBuilder text, int length);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] internal static extern uint GetRawInputData(nint input, uint command, out RawKeyboard data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);
    [DllImport("user32.dll")] internal static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint window, int message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint window, int message, nint wParam, string text, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint window, int message, nint wParam, StringBuilder text, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendGetSel(nint window, int message, out int start, out int end, uint flags, uint timeout, out nint result);
    internal static bool ReadCaret(nint window, uint timeout, out int start, out int end) =>
        SendGetSel(window, EmGetSel, out start, out end, 3, timeout, out _) != 0 && start >= 0 && end >= start;

    internal static bool TryMessage(nint window, int message, nint wParam, nint lParam, out nint result) =>
        SendMessageTimeout(window, message, wParam, lParam, 2, 150, out result) != 0;

    internal static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    internal static bool IsSupportedLayout(nint layout) => (uint)(long)layout is 0x04090409 or 0x041e041e;

    internal delegate void WinEventCallback(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);

    internal static bool IsNativeEdit(nint window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
            name.ToString().StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) ||
            name.ToString().StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase) ||
            name.ToString().StartsWith("WindowsForms10.RichEdit", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsProtectedEdit(nint window) => IsNativeEdit(window) && (GetWindowLong(window, -16) & (0x20 | 0x800)) != 0;
}
