using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace BetterRightLang.Desktop;

internal sealed class ForegroundEditor
{
    private readonly AutomationElement element;
    private readonly TextPattern pattern;
    private readonly TextPatternRange selection;
    private readonly TextPatternRange contextRange;
    private readonly TextPatternRange followingRange;
    private readonly string followingText;
    private readonly TextPatternRange documentEnd;
    private readonly bool hasLeftBoundary;
    private readonly string? fieldValue;
    internal nint Foreground { get; }
    internal nint Focus { get; }
    internal nint Layout { get; }
    internal string Context { get; }
    internal string SelectedText { get; }

    private ForegroundEditor(AutomationElement element, TextPattern pattern, TextPatternRange selection,
        TextPatternRange contextRange, TextPatternRange followingRange, string followingText,
        nint foreground, nint focus, nint layout, string context, string selectedText, string? fieldValue = null)
    {
        this.element = element; this.pattern = pattern; this.selection = selection; this.contextRange = contextRange;
        this.followingRange = followingRange; this.followingText = followingText;
        this.fieldValue = fieldValue;
        Foreground = foreground; Focus = focus; Layout = layout; Context = context; SelectedText = selectedText;
        documentEnd = pattern.DocumentRange.Clone();
        documentEnd.MoveEndpointByRange(TextPatternRangeEndpoint.Start, documentEnd, TextPatternRangeEndpoint.End);
        if (fieldValue is not null)
        {
            hasLeftBoundary = true;
            return;
        }
        var preceding = contextRange.Clone();
        preceding.MoveEndpointByRange(TextPatternRangeEndpoint.End, contextRange, TextPatternRangeEndpoint.Start);
        var moved = preceding.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
        var left = preceding.GetText(8);
        hasLeftBoundary = moved == 0 || (left.Length > 0 && (char.IsWhiteSpace(left[^1]) ||
            (context.Length > 0 && ((IsThai(left[^1]) && char.IsAsciiLetter(context[0])) ||
                (char.IsAsciiLetter(left[^1]) && IsThai(context[0]))))));
    }

    internal static ForegroundEditor? Capture(bool selected)
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == 0) return null;
        var thread = Native.GetWindowThreadProcessId(foreground, out _);
        var info = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
        if (!Native.GetGUIThreadInfo(thread, ref info) || info.Focus == 0 || Native.IsProtectedEdit(info.Focus)) return null;
        var layout = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(info.Focus, out _));
        if (!Native.IsSupportedLayout(layout)) return null;
        var element = AutomationElement.FocusedElement;
        if (element is null || !IsElementInForeground(element, foreground) || element.Current.IsPassword ||
            !element.Current.IsEnabled || !element.Current.HasKeyboardFocus) return null;
        var result = ReadSelection(element, selected, foreground, info.Focus, layout);
        return result is not null && result.IsCurrent() ? result : null;
    }

    internal static bool IsElementInForeground(AutomationElement element, nint foreground)
    {
        Native.GetWindowThreadProcessId(foreground, out var process);
        if (process == Environment.ProcessId && element.Current.AutomationId != "sample" &&
            element.Current.Name is not ("Practice text" or "ข้อความสำหรับฝึกพิมพ์")) return false;
        AutomationElement? ancestor = element;
        for (var depth = 0; ancestor is not null && depth < 64; depth++, ancestor = TreeWalker.RawViewWalker.GetParent(ancestor))
        {
            if (ancestor.Current.IsPassword) return false;
            var handle = new nint(ancestor.Current.NativeWindowHandle);
            if (handle == foreground || (handle != 0 && Native.IsChild(foreground, handle) && Native.GetAncestor(handle, 2) == foreground)) return true;
        }
        return false;
    }

    internal static ForegroundEditor? ReadSelection(AutomationElement element, bool selected, nint foreground = 0, nint focus = 0, nint layout = 0)
    {
        if (element.Current.IsPassword || !element.Current.IsEnabled) return null;
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var rawPattern)) return null;
        var pattern = (TextPattern)rawPattern;
        var ranges = pattern.GetSelection();
        if (ranges.Length != 1) return null;
        var range = ranges[0];
        var empty = range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0;
        if (selected == empty) return null;
        var readOnly = range.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
        if (readOnly is not false) return null;
        var valuePattern = element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawValue) ? (ValuePattern)rawValue : null;
        if (valuePattern?.Current.IsReadOnly == true) return null;
        var selectedText = range.GetText(4097);
        if (selectedText.Length > 4096) return null;
        TextPatternRange? fieldRange = null;
        if (valuePattern is not null && element.Current.FrameworkId == "Chrome")
        {
            // Chromium character ranges can walk out of a textbox into the surrounding webpage.
            var value = valuePattern.Current.Value;
            if (value.Length <= 4096)
            {
                var left = range.Clone();
                var right = range.Clone();
                if (value.Length != 0)
                {
                    left.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                    left.ExpandToEnclosingUnit(TextUnit.Line);
                    right.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
                    right.ExpandToEnclosingUnit(TextUnit.Line);
                }
                left.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                right.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
                var before = left.GetText(value.Length + 1);
                var after = right.GetText(value.Length + 1);
                if (value == before + selectedText + after)
                {
                    if (empty && after.Length != 0 && !char.IsWhiteSpace(after[0])) return null;
                    return new ForegroundEditor(element, pattern, range.Clone(), left, right, after, foreground, focus, layout, before, selectedText, value);
                }
            }
            fieldRange = pattern.DocumentRange;
            if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, fieldRange, TextPatternRangeEndpoint.Start) < 0 ||
                range.CompareEndpoints(TextPatternRangeEndpoint.End, fieldRange, TextPatternRangeEndpoint.End) > 0) return null;
        }
        if (empty)
        {
            var following = range.Clone();
            following.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
            if (fieldRange is not null && following.CompareEndpoints(TextPatternRangeEndpoint.End, fieldRange, TextPatternRangeEndpoint.End) > 0)
                following.MoveEndpointByRange(TextPatternRangeEndpoint.End, fieldRange, TextPatternRangeEndpoint.End);
            if (following.GetText(8).Any(character => !char.IsWhiteSpace(character))) return null;
        }
        var contextRange = range.Clone();
        contextRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        contextRange.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -256);
        if (fieldRange is not null && contextRange.CompareEndpoints(TextPatternRangeEndpoint.Start, fieldRange, TextPatternRangeEndpoint.Start) < 0)
            contextRange.MoveEndpointByRange(TextPatternRangeEndpoint.Start, fieldRange, TextPatternRangeEndpoint.Start);
        var context = contextRange.GetText(1025);
        if (context.Length > 1024) return null;
        var followingRange = range.Clone();
        followingRange.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
        followingRange.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 32);
        if (fieldRange is not null && followingRange.CompareEndpoints(TextPatternRangeEndpoint.End, fieldRange, TextPatternRangeEndpoint.End) > 0)
            followingRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, fieldRange, TextPatternRangeEndpoint.End);
        var followingText = followingRange.GetText(129);
        if (followingText.Length > 128) return null;
        return new ForegroundEditor(element, pattern, range.Clone(), contextRange, followingRange, followingText,
            foreground, focus, layout, context, selectedText);
    }

    internal bool IsCurrent()
    {
        if (!IsFocused()) return false;
        if (fieldValue is not null && ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value != fieldValue) return false;
        var current = pattern.GetSelection();
        return current.Length == 1 && current[0].CompareEndpoints(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.Start) == 0 &&
            current[0].CompareEndpoints(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.End) == 0 &&
            current[0].GetText(4097) == SelectedText && contextRange.GetText(Context.Length + 1) == Context &&
            selection.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
    }

    internal bool HasWordStart(int start) => start > 0 || hasLeftBoundary;
    private static bool IsThai(char character) => character is >= '\u0e00' and <= '\u0e7f';

    private bool IsFocused()
    {
        if (Native.GetForegroundWindow() != Foreground) return false;
        var thread = Native.GetWindowThreadProcessId(Foreground, out _);
        var info = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
        if (!Native.GetGUIThreadInfo(thread, ref info) || info.Focus != Focus || Native.IsProtectedEdit(Focus) || Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(Focus, out _)) != Layout ||
            !element.Current.HasKeyboardFocus || element.Current.IsPassword || !element.Current.IsEnabled) return false;
        return selection.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false &&
            (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawValue) || !((ValuePattern)rawValue).Current.IsReadOnly);
    }

    internal bool IsAppendOf(ForegroundEditor after, bool boundary, int maximumKeys = 1)
    {
        if (maximumKeys is < 1 or > 128) return false;
        // A layout change leaves the caret and text intact. The input batch checks its own layout at replacement.
        if (Foreground != after.Foreground || Focus != after.Focus ||
            !Automation.Compare(element, after.element) || SelectedText.Length != 0 || after.SelectedText.Length != 0) return false;
        if (fieldValue is not null && after.fieldValue is not null)
        {
            if (followingText != after.followingText || !after.Context.StartsWith(Context, StringComparison.Ordinal)) return false;
            return IsTypedAppend(after.Context[Context.Length..], boundary, maximumKeys);
        }
        if (contextRange.CompareEndpoints(TextPatternRangeEndpoint.Start, after.contextRange, TextPatternRangeEndpoint.Start) > 0) return false;
        var droppedRange = contextRange.Clone();
        droppedRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, after.contextRange, TextPatternRangeEndpoint.Start);
        var dropped = droppedRange.GetText(257);
        if (dropped.Length > maximumKeys * 2 || !Context.StartsWith(dropped, StringComparison.Ordinal)) return false;
        var prefix = Context[dropped.Length..];
        if (!after.Context.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var inserted = after.Context[prefix.Length..];
        if (!IsTypedAppend(inserted, boundary, maximumKeys)) return false;
        var anchoredContext = contextRange.GetText(1025);
        if (anchoredContext != Context && anchoredContext != Context + inserted) return false;
        var suffix = after.selection.Clone();
        if (suffix.CompareEndpoints(TextPatternRangeEndpoint.Start, followingRange, TextPatternRangeEndpoint.End) <= 0)
        {
            suffix.MoveEndpointByRange(TextPatternRangeEndpoint.End, followingRange, TextPatternRangeEndpoint.End);
            if (suffix.GetText(129) == followingText) return true;
        }
        // Some providers keep range offsets fixed; document growth distinguishes insertion from caret movement.
        if (after.followingText != followingText || documentEnd.CompareEndpoints(TextPatternRangeEndpoint.Start, after.documentEnd, TextPatternRangeEndpoint.Start) >= 0) return false;
        var growth = documentEnd.Clone();
        growth.MoveEndpointByRange(TextPatternRangeEndpoint.End, after.documentEnd, TextPatternRangeEndpoint.Start);
        return growth.GetText(257).Length == inserted.Length;
    }

    // A field that now holds only the typed keys needs no match with this snapshot's text. Empty editors such as
    // ChatGPT's message box report a CSS placeholder as their text, so that snapshot never matches the first typed word.
    // The field must still differ from the snapshot: one that rejected every key, such as a full field, keeps its text.
    internal bool IsReplacedByTypedText(ForegroundEditor after, bool boundary, int maximumKeys) =>
        after.fieldValue is not null && after.fieldValue != fieldValue && after.SelectedText.Length == 0 &&
        after.followingText.All(c => c == '\n') && after.fieldValue == after.Context + after.followingText &&
        IsTypedAppend(after.Context, boundary, maximumKeys);

    private static bool IsTypedAppend(string inserted, bool boundary, int maximumKeys) =>
        inserted.Length is > 0 and <= 256 && inserted.Replace("\r\n", "\n", StringComparison.Ordinal).Length <= maximumKeys &&
        char.IsWhiteSpace(inserted[^1]) == boundary && !inserted.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t');

    internal bool HasAppendedText(ForegroundEditor after) =>
        Layout == after.Layout && after.Context.Length > Context.Length && after.Context.StartsWith(Context, StringComparison.Ordinal) &&
        IsAppendOf(after, char.IsWhiteSpace(after.Context[^1]), 128);

    internal bool Replace(int start, int length, string expected, string replacement, bool selected, nint destination, Func<bool> inputUnchanged, bool appended)
    {
        if (length != expected.Length || !selected && (start < 0 || start > Context.Length - length)) return false;
        if (!selected)
        {
            // Replace the character that shares the word's first caret stop too, and type it back unchanged.
            var stop = EditBatch.CaretStopAtOrBefore(Context, start);
            var joined = Context[stop..start];
            (start, length, expected, replacement) = (stop, length + joined.Length, joined + expected, joined + replacement);
        }
        var suffix = selected ? "" : Context[(start + length)..];
        if (!appended && suffix.Any(c => !char.IsWhiteSpace(c))) return false;
        var source = expected + suffix;
        var target = FindReplacementRange(source, selected);
        if (target is null || target.GetText(source.Length + 1) != source) return false;
        var nativeControl = Native.IsNativeEdit(Focus);
        var native = nativeControl ? NativeEdit.Read(Focus) : null;
        if (nativeControl && (native is null || !native.Matches(source, selected))) return false;
        if (!nativeControl && !selected && target.GetAttributeValue(TextPattern.TextFlowDirectionsAttribute) is FlowDirections direction &&
            direction != FlowDirections.Default) return false;
        var keys = native is not null ? EditBatch.NativeMarker() :
            EditBatch.Build(expected, replacement, selected, selected ? null : FieldEdges(start, source, target), suffix);
        bool Validate() => IsCurrent() && (native is null || NativeEdit.Read(Focus) == native) && inputUnchanged();
        return keys is not null && inputUnchanged() && EditBatch.Send(keys, Validate, Foreground, Focus, Layout, destination,
            native is null ? null : () => native.Commit(source, replacement + suffix, selected));
    }

    private (string Before, string After)? FieldEdges(int start, string source, TextPatternRange target)
    {
        if (fieldValue is not null) return (Context[..start], followingText);
        var doc = pattern.DocumentRange;
        if (target.CompareEndpoints(TextPatternRangeEndpoint.Start, doc, TextPatternRangeEndpoint.Start) < 0 ||
            selection.CompareEndpoints(TextPatternRangeEndpoint.End, doc, TextPatternRangeEndpoint.End) > 0) return null;
        var left = doc.Clone();
        left.MoveEndpointByRange(TextPatternRangeEndpoint.End, target, TextPatternRangeEndpoint.Start);
        var right = doc.Clone();
        right.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
        var before = left.GetText(4097);
        var after = right.GetText(4097);
        if (before.Length + after.Length > 4096) return null;
        if (element.Current.FrameworkId == "Chrome" && (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ||
            ((ValuePattern)value).Current.Value != before + source + after)) return null;
        return (before, after);
    }

    internal TextPatternRange? FindReplacementRange(string expected, bool selected)
    {
        if (selected) return selection.GetText(4097) == expected ? selection.Clone() : null;
        if (!Context.EndsWith(expected, StringComparison.Ordinal) || expected.Length == 0) return null;
        if (Context.Length == expected.Length) return contextRange.Clone();

        // Keep the end anchored at the caret. Providers can count a Thai base and marks as one character.
        var low = 1;
        var high = expected.Length;
        var units = high;
        while (low <= high)
        {
            var target = selection.Clone();
            target.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -units);
            var candidate = target.GetText(expected.Length + 1);
            if (candidate == expected) return target;
            if (candidate.Length > expected.Length) high = units - 1;
            else low = units + 1;
            units = (low + high) / 2;
        }
        return null;
    }

}

internal sealed record NativeEdit(nint Window, string? Text, int Length, int Start, int End)
{
    internal static NativeEdit? Read(nint window)
    {
        if (!Native.IsNativeEdit(window) || (Native.GetWindowLong(window, -16) & (0x20 | 0x800)) != 0 ||
            !Native.TryMessage(window, Native.WmGetTextLength, 0, 0, out var count) || count < 0 || count > int.MaxValue ||
            !Native.ReadCaret(window, 150, out var start, out var end) || end > count) return null;
        // The UIA range supplies the verified word in long documents. Keep its 32-bit native caret without copying the document.
        if (count > 65535) return new NativeEdit(window, null, (int)count, start, end);
        var text = new StringBuilder((int)count + 1);
        if (Native.SendMessageTimeout(window, Native.WmGetText, text.Capacity, text, 2, 150, out var copied) == 0 || copied != count) return null;
        return new NativeEdit(window, text.ToString(), (int)count, start, end);
    }

    internal bool Matches(string expected, bool selected) => selected
        ? End - Start == expected.Length && (Text is null || Text.AsSpan(Start, End - Start).SequenceEqual(expected))
        : Start == End && Start >= expected.Length && (Text is null || Text.AsSpan(0, Start).EndsWith(expected, StringComparison.Ordinal));

    internal bool Commit(string source, string replacement, bool selected)
    {
        // This runs at the marker. Bound every cross-process send while subsequent physical input is queued by Windows.
        var deadline = Environment.TickCount64 + 50;
        uint Remaining() => (uint)Math.Clamp(deadline - Environment.TickCount64, 1, 50);
        if (!Matches(source, selected) || Native.IsProtectedEdit(Window) ||
            Native.SendMessageTimeout(Window, Native.WmGetTextLength, 0, 0, 3, Remaining(), out var size) == 0 || size != Length ||
            !Native.ReadCaret(Window, Remaining(), out var currentStart, out var currentEnd) ||
            currentStart != Start || currentEnd != End || Environment.TickCount64 >= deadline) return false;
        var written = false;
        try
        {
            if (!selected)
            {
                var first = Start - source.Length;
                if (Native.SendMessageTimeout(Window, Native.EmSetSel, first, Start, 3, Remaining(), out _) == 0 ||
                    !Native.ReadCaret(Window, Remaining(), out currentStart, out currentEnd) ||
                    currentStart != first || currentEnd != Start) return false;
            }
            written = Environment.TickCount64 < deadline &&
                Native.SendMessageTimeout(Window, Native.EmReplaceSel, 1, replacement, 3, Remaining(), out _) != 0;
            return written;
        }
        finally
        {
            if (!selected && !written &&
                (Environment.TickCount64 >= deadline || Native.SendMessageTimeout(Window, Native.EmSetSel, Start, Start, 3, Remaining(), out _) == 0))
                Native.PostMessage(Window, Native.EmSetSel, Start, Start);
        }
    }
}
