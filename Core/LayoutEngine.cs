using System.Text;

namespace BetterRightLang.Core;

public readonly record struct WordSpan(int Start, int Length, string Text)
{
    public bool Matches(string text) => Start >= 0 && Length >= 0 && Start <= text.Length - Length
        && text.AsSpan(Start, Length).SequenceEqual(Text);
}

public sealed class LayoutEngine
{
    // Windows US and Thai Kedmanee keys, including Shift; space is identical in both layouts.
    private const string EnglishKeys = "`1234567890-=qwertyuiop[]\\asdfghjkl;'zxcvbnm,./~!@#$%^&*()_+QWERTYUIOP{}|ASDFGHJKL:\"ZXCVBNM<>?";
    private const string ThaiKeys = "_ๅ/-ภถุึคตจขชๆไำพะัีรนยบลฃฟหกดเ้่าสวงผปแอิืทมใฝ%+๑๒๓๔ู฿๕๖๗๘๙๐\"ฎฑธํ๊ณฯญฐ,ฅฤฆฏโฌ็๋ษศซ.()ฉฮฺ์?ฒฬฦ";
    private readonly Words english;
    private readonly Words thai;
    private volatile WordWhitelist whitelist = WordWhitelist.Empty;
    public WordWhitelist Whitelist
    {
        get => whitelist;
        set => whitelist = value ?? throw new ArgumentNullException(nameof(value));
    }

    public LayoutEngine(string englishPath, string thaiPath)
    {
        english = new Words(WithAdditions(englishPath));
        thai = new Words(WithAdditions(thaiPath));
    }

    // Words added by hand live beside a generated list, such as th-extra.txt beside th.txt.
    private static string[] WithAdditions(string path)
    {
        var extra = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + "-extra.txt");
        return File.Exists(extra) ? [path, extra] : [path];
    }

    public string? Suggest(WordSpan word, string context, bool atBoundary)
    {
        if (!word.Matches(context)) return null;
        if (whitelist.Words.Count > 0)
        {
            var start = word.Start;
            while (start > 0 && !char.IsWhiteSpace(context[start - 1])) start--;
            if (start < word.Start && whitelist.Protects(context[start..(word.Start + word.Length)], atBoundary)) return null;
        }
        return Suggest(word.Text, atBoundary);
    }

    public string? Suggest(string token, bool atBoundary = false)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length is < 3 or > 128 || token.Any(char.IsWhiteSpace) || whitelist.Protects(token, atBoundary)) return null;
        var suggestion = SuggestWord(token, allowPrefix: !atBoundary && token.Length == 8);
        if (suggestion is not null || !atBoundary) return suggestion;
        var end = token.Length;
        while (end > 0 && ".,!?;:)]}\"".Contains(token[end - 1])) end--;
        if (end < 3 || end == token.Length) return null;
        var word = SuggestWord(token[..end]);
        return word is null ? null : word + token[end..];
    }

    private string? SuggestWord(string token, bool allowPrefix = false)
    {
        if (token.Any(c => c is '@' or '_' or '\\') || token.Contains("://", StringComparison.Ordinal)
            || token.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return null;
        var hasThai = token.Any(IsThai);
        if (hasThai && token.Any(char.IsAsciiLetter)) return null;
        if (!hasThai && !token.Any(char.IsAsciiLetter)) return null;
        var source = hasThai ? thai : english;
        var target = hasThai ? english : thai;
        if (source.Matches(token, prefix: true, compound: hasThai)) return null;
        var converted = Map(token, hasThai);
        return target.Matches(converted, prefix: allowPrefix, compound: !hasThai) ? converted : null;
    }

    public string Convert(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new StringBuilder(text.Length);
        var start = 0;
        while (start < text.Length)
        {
            if (char.IsWhiteSpace(text[start]))
            {
                result.Append(text[start++]);
                continue;
            }
            var end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            var part = text[start..end];
            if (part.Any(IsThai) && part.Any(char.IsAsciiLetter))
            {
                // Mixed-script selections retain neutral punctuation because its intended layout is unknown.
                foreach (var c in part)
                    result.Append(IsThai(c) ? Map(c, true) : char.IsAsciiLetter(c) ? Map(c, false) : c);
            }
            else result.Append(Map(part, part.Any(IsThai)));
            start = end;
        }
        return result.ToString();
    }

    public static WordSpan? GetWordBeforeCaret(string text, int caret)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (caret < 0 || caret > text.Length || (caret < text.Length && !char.IsWhiteSpace(text[caret]))) return null;
        var end = caret;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            if (text[end - 1] is '\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029') return null;
            end--;
        }
        var start = end;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var lastScript = end - 1;
        while (lastScript >= start && !IsThai(text[lastScript]) && !char.IsAsciiLetterOrDigit(text[lastScript])) lastScript--;
        if (lastScript >= start)
        {
            var trailingThai = IsThai(text[lastScript]);
            for (var i = lastScript - 1; i >= start; i--)
            {
                if (trailingThai ? !char.IsAsciiLetterOrDigit(text[i]) : !IsThai(text[i])) continue;
                start = i + 1;
                // A neutral separator between scripts belongs to neither current word with confidence.
                while (start < lastScript && !IsThai(text[start]) && !char.IsAsciiLetterOrDigit(text[start])) start++;
                break;
            }
        }
        return start == end ? null : new WordSpan(start, end - start, text[start..end]);
    }

    private static bool IsThai(char c) => c is >= '\u0e01' and <= '\u0e5b';

    private static char Map(char value, bool fromThai)
    {
        var source = fromThai ? ThaiKeys : EnglishKeys;
        var target = fromThai ? EnglishKeys : ThaiKeys;
        var index = source.IndexOf(value);
        return index >= 0 ? target[index] : value;
    }

    private static string Map(string text, bool fromThai) => new(text.Select(c => Map(c, fromThai)).ToArray());

    private sealed class Words
    {
        private readonly HashSet<string> exact;
        private readonly string[] sorted;
        private readonly int maxLength;

        public Words(string[] paths)
        {
            exact = paths.SelectMany(path => File.ReadLines(path)).Select(line => line.Trim().ToLowerInvariant())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToHashSet(StringComparer.Ordinal);
            if (exact.Count == 0) throw new InvalidDataException($"Dictionary is empty: {paths[0]}");
            sorted = exact.Order(StringComparer.Ordinal).ToArray();
            maxLength = sorted.Max(word => word.Length);
        }

        private bool HasPrefix(string text)
        {
            var index = Array.BinarySearch(sorted, text, StringComparer.Ordinal);
            if (index < 0) index = ~index;
            return index < sorted.Length && sorted[index].StartsWith(text, StringComparison.Ordinal);
        }

        public bool Matches(string text, bool prefix, bool compound)
        {
            text = text.ToLowerInvariant();
            if (exact.Contains(text) || prefix && HasPrefix(text)) return true;
            if (!compound) return false;
            var reachable = new bool[text.Length + 1];
            reachable[0] = true;
            for (var start = 0; start < text.Length; start++)
            {
                if (!reachable[start]) continue;
                if (prefix && HasPrefix(text[start..])) return true;
                for (var end = start + 1; end <= Math.Min(text.Length, start + maxLength); end++)
                    if (exact.Contains(text[start..end])) reachable[end] = true;
            }
            return reachable[text.Length];
        }
    }
}
