using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace BetterRightLang.Desktop;

internal sealed class CapsLockShortcuts : IDisposable
{
    private readonly nint target;
    private readonly Thread thread;
    private uint threadId;
    private volatile bool toggle, selection;
    private volatile nint localAutoWindow;
    private bool? captured;

    internal CapsLockShortcuts(nint target)
    {
        this.target = target;
        using var ready = new ManualResetEventSlim();
        // The editor thread can wait on UI Automation. Windows requires the shortcut filter to keep pumping.
        thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "BetterRightLang Caps Lock" };
        thread.Start();
        ready.Wait();
    }

    internal void SetBindings(bool modeShortcut, bool selectionShortcut, nint localWindow)
    {
        if (toggle == modeShortcut && selection == selectionShortcut && localAutoWindow == localWindow) return;
        toggle = modeShortcut;
        selection = selectionShortcut;
        localAutoWindow = localWindow;
        Native.PostThreadMessage(threadId, Native.WmRefresh, 0, 0);
    }

    private void Run(ManualResetEventSlim ready)
    {
        threadId = Native.GetCurrentThreadId();
        Native.PeekMessage(out _, 0, 0, 0, 0);
        ready.Set();
        Native.HookProc filter = Filter;
        nint hook = 0;
        try
        {
            while (Native.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id != Native.WmRefresh) continue;
                var enabled = toggle || selection || localAutoWindow != 0;
                if (enabled && hook == 0)
                {
                    captured = Down(0x14) ? false : null;
                    hook = Native.SetWindowsHookEx(13, filter, Native.GetModuleHandle(null), 0);
                    if (hook == 0) Native.PostMessage(target, Native.WmCapsShortcut, 0, Marshal.GetLastWin32Error());
                }
                else if (!enabled && hook != 0 && captured != true)
                {
                    Native.UnhookWindowsHookEx(hook);
                    hook = 0;
                }
            }
        }
        finally
        {
            if (hook != 0) Native.UnhookWindowsHookEx(hook);
            GC.KeepAlive(filter);
        }
    }

    private static bool Down(int key) => Native.GetAsyncKeyState(key) < 0;

    private nint Filter(int code, nint message, nint data)
    {
        if (code < 0) return Native.CallNextHookEx(0, code, message, data);
        var key = Marshal.ReadInt32(data);
        if (key == 0x14)
        {
            if (message is 0x101 or 0x105)
            {
                var swallow = captured == true;
                captured = null;
                if (swallow)
                {
                    if (!toggle && !selection && localAutoWindow == 0) Native.PostThreadMessage(threadId, Native.WmRefresh, 0, 0);
                    return 1;
                }
            }
            else
            {
                // Repeats and key-up belong to the original press, even if Ctrl or Shift is released first.
                captured ??= ClaimPress();
                if (captured == true) return 1;
            }
        }
        var local = localAutoWindow;
        if (local != 0 && message is 0x100 or 0x104 && key != 0xe7 &&
            Native.GetForegroundWindow() == local && (nuint)Marshal.ReadIntPtr(data, 16) != EditBatch.Tag)
            Native.PostMessage(target, Native.WmLocalKey, key, local);
        return Native.CallNextHookEx(0, code, message, data);
    }

    private bool ClaimPress()
    {
        bool ctrl = Down(0x11), shift = Down(0x10);
        if (ctrl == shift || Down(0x12) || Down(0x5b) || Down(0x5c)) return false;
        return (ctrl ? toggle : selection) && Native.PostMessage(target, Native.WmCapsShortcut, ctrl ? 1 : 3, 0);
    }

    public void Dispose()
    {
        Native.PostThreadMessage(threadId, 0x12, 0, 0);
        thread.Join();
    }
}
