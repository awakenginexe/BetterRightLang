namespace BetterRightLang.Core;

public sealed class WordWhitelist
{
    public const int MaxLength = 4096;
    private const string TrailingPunctuation = ".,!?;:)]}\"";
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private readonly string[] sorted;
    public static WordWhitelist Empty { get; } = new([]);
    public IReadOnlyList<string> Words { get; }

    public WordWhitelist(IEnumerable<string> words)
    {
        sorted = words.Where(IsWord).Distinct(Comparer).Order(Comparer).ToArray();
        Words = Array.AsReadOnly(sorted);
    }

    public static bool IsWord(string? word) => word is { Length: > 0 and <= MaxLength } &&
        !word.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    public WordWhitelist With(string word, bool add)
    {
        if (!IsWord(word) || (Array.BinarySearch(sorted, word, Comparer) >= 0) == add) return this;
        return new WordWhitelist(add ? sorted.Append(word) : sorted.Where(w => !Comparer.Equals(w, word)));
    }

    internal bool Protects(string token, bool atBoundary)
    {
        if (sorted.Length == 0) return false;
        var index = Array.BinarySearch(sorted, token, Comparer);
        if (index >= 0 || !atBoundary && ~index < sorted.Length &&
            sorted[~index].StartsWith(token, StringComparison.OrdinalIgnoreCase)) return true;
        for (var end = token.Length - 1; end > 0 && TrailingPunctuation.Contains(token[end]); end--)
            if (Array.BinarySearch(sorted, token[..end], Comparer) >= 0) return true;
        return false;
    }
}
