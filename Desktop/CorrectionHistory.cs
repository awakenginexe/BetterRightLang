using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterRightLang.Desktop;

internal sealed record Correction(string Original, string Replacement, bool Automatic);
internal sealed record HistoryState(Correction[] Entries, IReadOnlyList<string> Whitelist);

internal sealed class CorrectionHistory
{
    internal const int Capacity = 100;
    private readonly Queue<Correction> entries = new();
    internal Correction[] NewestFirst()
    {
        lock (entries) return entries.Reverse().ToArray();
    }

    internal void Record(string original, string replacement, bool automatic)
    {
        // Layout conversion preserves whitespace, including in a multi-word manual selection.
        var before = original.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var after = replacement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (before.Length != after.Length) return;
        lock (entries)
        {
            for (var i = 0; i < before.Length; i++)
            {
                if (before[i] == after[i]) continue;
                if (entries.Count == Capacity) entries.Dequeue();
                entries.Enqueue(new(before[i], after[i], automatic));
            }
        }
    }
}
