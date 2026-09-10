namespace OfflineLogReader.Core;

public sealed record Exclusion(string Text, bool MatchCase, bool Enabled = true);
public sealed record QueryOptions(DateTime? From, DateTime? To, string[] Severities, string[] Sources, Exclusion[] Exclusions, string Search = "", bool MatchCase = false);

public static class LogQuery
{
    public static LogEntry[] Run(IReadOnlyList<LogEntry> entries, QueryOptions query, IProgress<WorkProgress>? progress, CancellationToken token)
    {
        var severity = query.Severities.ToHashSet(StringComparer.Ordinal);
        var source = query.Sources.ToHashSet(StringComparer.Ordinal);
        var exclusions = query.Exclusions.Where(x => x.Enabled && x.Text.Length > 0).ToArray();
        var output = new List<LogEntry>();
        for (int i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if ((i & 4095) == 0) progress?.Report(new WorkProgress((double)i / entries.Count, $"Prohledáno {i:N0} / {entries.Count:N0}"));
            var entry = entries[i];
            if (query.From.HasValue && (!entry.Timestamp.HasValue || entry.Timestamp < query.From)) continue;
            if (query.To.HasValue && (!entry.Timestamp.HasValue || entry.Timestamp > query.To)) continue;
            if (severity.Count > 0 && !severity.Contains(entry.Severity)) continue;
            if (source.Count > 0 && !source.Contains(entry.Source)) continue;
            if (exclusions.Length > 0 || query.Search.Length > 0)
            {
                if (!MatchesText(entry, query, exclusions, token)) continue;
            }
            output.Add(entry);
        }
        token.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static bool MatchesText(LogEntry entry, QueryOptions query, Exclusion[] exclusions, CancellationToken token)
    {
        if (entry.Length <= 1024 * 1024)
        {
            string text = entry.Text;
            return !exclusions.Any(x => text.Contains(x.Text, x.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                && (query.Search.Length == 0 || text.Contains(query.Search, query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
        }
        // Malformed logs can contain a single enormous record. Search them in bounded chunks,
        // retaining enough overlap to find matches crossing byte/character chunk boundaries.
        int overlap = Math.Max(query.Search.Length, exclusions.Select(x => x.Text.Length).DefaultIfEmpty(0).Max()) - 1;
        string tail = "";
        bool found = query.Search.Length == 0;
        foreach (string segment in entry.File.ReadSegments(entry.Offset, entry.Length, token))
        {
            string text = tail + segment;
            if (exclusions.Any(x => text.Contains(x.Text, x.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))) return false;
            found |= query.Search.Length > 0 && text.Contains(query.Search, query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            if (found && exclusions.Length == 0) return true;
            tail = overlap > 0 ? text[^Math.Min(overlap, text.Length)..] : "";
        }
        return found;
    }
}

