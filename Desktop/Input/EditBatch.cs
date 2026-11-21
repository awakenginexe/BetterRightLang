using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace BetterRightLang.Desktop;

internal static class EditBatch
{
    internal static readonly uint Tag = (uint)Random.Shared.Next(1, int.MaxValue);

    private static Native.Input Key(ushort key, ushort scan, uint flags, uint tag) => new()
    {
        Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { Key = key, Scan = scan, Flags = flags, Extra = tag } }
    };

    internal static Native.Input[]? Build(string word, string replacement, bool selected,
        (string Before, string After)? field = null)
    {
        if (word.Length == 0 || replacement.Length == 0 || word == replacement) return null;
        if (!selected && field is null) return null;
        foreach (var c in replacement) if (char.IsControl(c)) return null;

        var input = new List<Native.Input>();
        void Add(ushort key, ushort scan, uint flags) => input.Add(Key(key, scan, flags, Tag));
        void Arrow(ushort key, ushort scan, int count)
        {
            for (var i = 0; i < count; i++) { Add(key, scan, 1); Add(key, scan, 1 | Native.KeyUp); }
        }
        void FieldEdge(bool end)
        {
            // Search fields can reserve Home and End for their results. Collapse Select All to the field edge.
            Add(0xa2, 0x1d, 0);
            Add(0x41, 0x1e, 0);
            Add(0x41, 0x1e, Native.KeyUp);
            Add(0xa2, 0x1d, Native.KeyUp);
            Arrow(end ? (ushort)0x27 : (ushort)0x25, end ? (ushort)0x4d : (ushort)0x4b, 1);
        }
        if (!selected && field is { } edges)
        {
            // Chromium can report the old caret while a character ahead of our batch is still reaching the renderer.
            FieldEdge(false);
            Arrow(0x27, 0x4d, StringInfo.ParseCombiningCharacters(edges.Before).Length);
            Add(0xa0, 0x2a, 0);
            Arrow(0x27, 0x4d, StringInfo.ParseCombiningCharacters(word).Length);
            Add(0xa0, 0x2a, Native.KeyUp);
        }
        foreach (var c in replacement)
        {
            Add(0, c, Native.Unicode);
            Add(0, c, Native.Unicode | Native.KeyUp);
        }
        if (!selected && field is { } end)
        {
            FieldEdge(true);
            Arrow(0x25, 0x4b, StringInfo.ParseCombiningCharacters(end.After).Length);
        }
        return input.ToArray();
    }

    // The queue lives only between the first accepted edit event and the last replayed key in the same field.
    internal static bool Send(Native.Input[] input, Func<bool> validate, nint foreground, nint focus, nint sourceLayout, nint destinationLayout,
        Func<bool>? nativeEdit = null)
    {
        using var ready = new ManualResetEventSlim();
        using var listening = new ManualResetEventSlim();
        using var complete = new ManualResetEventSlim();
        var probeTag = Tag ^ 0x80000000u;
        var replayTag = Tag ^ 0x40000000u;
        const uint flush = 0x8008;
        var queued = new List<Native.Input>();
        var originalThread = Native.GetWindowThreadProcessId(focus, out var originalProcess);
        uint threadId = 0;
        bool installed = false, changed = false, accepted = false;
        bool holding = false;
        int received = 0, replaying = 0;
        void EndOrReplay()
        {
            if (queued.Count != 0) Native.PostThreadMessage(threadId, flush, 0, 0);
            else { holding = false; complete.Set(); }
        }
        void Replay()
        {
            if (queued.Count == 0) return;
            var batch = queued.ToArray();
            queued.Clear();
            replaying = batch.Length;
            var sent = Native.HasFocus(foreground, focus, 0) ? (int)Native.SendInput((uint)batch.Length, batch, Marshal.SizeOf<Native.Input>()) : 0;
            if (sent == batch.Length) return;
            // Focus can move before replay. Deliver outstanding messages to their original control.
            if (Native.GetWindowThreadProcessId(focus, out var process) == originalThread && process == originalProcess)
                for (var i = sent; i < batch.Length; i++) PostKey(focus, batch[i].Data.Keyboard);
            replaying -= batch.Length - sent;
            if (replaying == 0) EndOrReplay();
        }
        Native.HookProc keys = (code, message, data) =>
        {
            if (code >= 0)
            {
                var extra = (nuint)Marshal.ReadIntPtr(data, 16);
                if (extra == probeTag) { listening.Set(); return 1; }
                if (extra == replayTag)
                {
                    if (replaying > 0 && --replaying == 0) EndOrReplay();
                }
                else if (extra == Tag)
                {
                    if (received == 0)
                    {
                        accepted = !changed && Native.HasFocus(foreground, focus, sourceLayout) && !Native.ModifiersDown();
                        if (accepted && nativeEdit is not null) accepted = nativeEdit();
                        if (accepted && destinationLayout != 0 && destinationLayout != sourceLayout)
                        {
                            var posted = Native.PostMessage(focus, 0x50, 0, destinationLayout);
                            if (nativeEdit is null && !posted) accepted = false;
                        }
                        holding = accepted && nativeEdit is null;
                    }
                    if (++received == input.Length) EndOrReplay();
                    if (!accepted || nativeEdit is not null) return 1;
                }
                else if (holding && Native.HasFocus(foreground, focus, 0))
                {
                    queued.Add(CopyKey(data, replayTag));
                    return 1;
                }
                else changed = true;
            }
            return Native.CallNextHookEx(0, code, message, data);
        };
        Native.HookProc mouse = (code, message, data) =>
        {
            if (code >= 0 && message != 0x200) changed = true;
            return Native.CallNextHookEx(0, code, message, data);
        };
        var observer = new Thread(() =>
        {
            threadId = Native.GetCurrentThreadId();
            Native.PeekMessage(out _, 0, 0, 0, 0);
            var module = Native.GetModuleHandle(null);
            var keyboardHook = Native.SetWindowsHookEx(13, keys, module, 0);
            var mouseHook = Native.SetWindowsHookEx(14, mouse, module, 0);
            try
            {
                installed = keyboardHook != 0 && mouseHook != 0;
                ready.Set();
                if (installed) while (Native.GetMessage(out var message, 0, 0, 0) > 0)
                    if (message.Id == flush) Replay();
            }
            finally
            {
                if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook);
                if (keyboardHook != 0) Native.UnhookWindowsHookEx(keyboardHook);
                if (accepted && received > 0 && received < input.Length)
                {
                    var cleanup = EndPartial(input.AsSpan(0, received), Native.HasFocus(foreground, focus, 0));
                    if (cleanup.Length != 0) Native.SendInput((uint)cleanup.Length, cleanup, Marshal.SizeOf<Native.Input>());
                }
                holding = false;
                if (queued.Count != 0) Replay();
            }
        }) { IsBackground = true, Name = "BetterRightLang edit" };
        observer.Start();
        ready.Wait();
        try
        {
            if (!installed || !validate() || Native.ModifiersDown()) return false;
            // A successful hook registration does not guarantee delivery. Test it with a harmless key-up first.
            if (nativeEdit is null && (Native.SendInput(1, NativeMarker(probeTag), Marshal.SizeOf<Native.Input>()) != 1 || !listening.Wait(500))) return false;
            var count = (int)Native.SendInput((uint)input.Length, input, Marshal.SizeOf<Native.Input>());
            while (count > 0 && count < input.Length && Native.HasFocus(foreground, focus, 0))
            {
                var tail = input[count..];
                var next = (int)Native.SendInput((uint)tail.Length, tail, Marshal.SizeOf<Native.Input>());
                if (next == 0) break;
                count += next;
            }
            if (count != input.Length) return false;
            return complete.Wait(2000) && accepted;
        }
        finally
        {
            if (installed) Native.PostThreadMessage(threadId, 0x12, 0, 0);
            observer.Join();
            GC.KeepAlive(keys);
            GC.KeepAlive(mouse);
        }
    }

    private static Native.Input CopyKey(nint data, uint tag)
    {
        var key = (ushort)Marshal.ReadInt32(data);
        var scan = (ushort)Marshal.ReadInt32(data, 4);
        var hookFlags = Marshal.ReadInt32(data, 8);
        var flags = (uint)(hookFlags & 1) | ((hookFlags & 0x80) != 0 ? Native.KeyUp : 0);
        if (key == 0xe7) { key = 0; flags |= Native.Unicode; }
        return Key(key, scan, flags, tag);
    }

    private static void PostKey(nint focus, Native.KeyboardInput key)
    {
        var up = (key.Flags & Native.KeyUp) != 0;
        if ((key.Flags & Native.Unicode) != 0)
        {
            if (!up) Native.PostMessage(focus, 0x102, key.Scan, 1);
            return;
        }
        var flags = 1u | (uint)key.Scan << 16 | (key.Flags & 1) << 24 | (up ? 0xc0000000u : 0);
        Native.PostMessage(focus, up ? 0x101 : 0x100, key.Key, unchecked((nint)(int)flags));
    }

    internal static Native.Input[] EndPartial(ReadOnlySpan<Native.Input> sent, bool sameField)
    {
        bool shift = false, control = false, selectAllKey = false, selecting = false;
        foreach (var item in sent)
        {
            var key = item.Data.Keyboard;
            var down = (key.Flags & Native.KeyUp) == 0;
            if (key.Key == 0xa0) shift = down;
            if (key.Key == 0xa2) control = down;
            if (key.Key == 0x41)
            {
                selectAllKey = down;
                if (down && control) selecting = true;
            }
            if (down && key.Key is 0x25 or 0x27) selecting = shift;
            if (down && (key.Flags & Native.Unicode) != 0) selecting = false;
        }
        var cleanup = new List<Native.Input>();
        if (selectAllKey) cleanup.Add(Key(0x41, 0x1e, Native.KeyUp, Tag));
        if (shift) cleanup.Add(Key(0xa0, 0x2a, Native.KeyUp, Tag));
        if (control) cleanup.Add(Key(0xa2, 0x1d, Native.KeyUp, Tag));
        if (selecting && sameField)
        {
            cleanup.Add(Key(0x27, 0x4d, 1, Tag));
            cleanup.Add(Key(0x27, 0x4d, 1 | Native.KeyUp, Tag));
        }
        return cleanup.ToArray();
    }

    // A swallowed key-up orders one native edit relative to the user's actual keyboard stream.
    internal static Native.Input[] NativeMarker(uint? tag = null) => new[] { Key(0, 0, Native.Unicode | Native.KeyUp, tag ?? Tag) };
}
