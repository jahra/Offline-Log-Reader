using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace OfflineLogReader.Core;

public sealed class LogFile : IDisposable
{
    private readonly SafeFileHandle handle;
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public Encoding Encoding { get; }
    public int Order { get; }
    public LogFile(string path, Encoding encoding, int order)
    {
        Path = System.IO.Path.GetFullPath(path);
        Encoding = encoding;
        Order = order;
        handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    public string Read(long offset, long length)
    {
        if (length > int.MaxValue) throw new IOException("Jeden záznam je příliš velký (přes 2 GB).");
        byte[] bytes = System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Max(1, length));
        try
        {
            int total = 0;
            while (total < length)
            {
                int n = RandomAccess.Read(handle, bytes.AsSpan(total, (int)length - total), offset + total);
                if (n == 0) throw new IOException("Soubor byl během čtení změněn nebo zkrácen.");
                total += n;
            }
            return Encoding.GetString(bytes, 0, total).TrimEnd('\r', '\n');
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(bytes); }
    }
    public void Dispose() => handle.Dispose();

    internal IEnumerable<string> ReadSegments(long offset, long length, CancellationToken token)
    {
        byte[] bytes = new byte[64 * 1024];
        char[] chars = new char[Encoding.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.GetDecoder();
        long consumed = 0;
        while (consumed < length)
        {
            token.ThrowIfCancellationRequested();
            int n = RandomAccess.Read(handle, bytes.AsSpan(0, (int)Math.Min(bytes.Length, length - consumed)), offset + consumed);
            if (n == 0) throw new IOException("Soubor byl během čtení změněn nebo zkrácen.");
            consumed += n;
            int count = decoder.GetChars(bytes, 0, n, chars, 0, consumed == length);
            yield return new string(chars, 0, count);
        }
    }
}

public sealed class LogEntry
{
    public LogFile File { get; }
    public long Offset { get; }
    public long Length { get; internal set; }
    public int Line { get; }
    public DateTime? Timestamp { get; }
    public string Severity { get; }
    public string Source { get; }
    public string TimeText => Timestamp?.ToString("dd.MM.yyyy HH:mm:ss") ?? "Nerozpoznáno";
    public string Origin => $"{File.Name}:{Line}";
    public string Text => File.Read(Offset, Length);
    public string Preview => PreviewCache.Get(this);
    internal LogEntry(LogFile file, long offset, int line, DateTime? time, string severity, string source)
    { File = file; Offset = offset; Line = line; Timestamp = time; Severity = severity; Source = source; }
}

// A bounded shared cache prevents virtualization from retaining every message ever displayed.
internal static class PreviewCache
{
    private static readonly Dictionary<LogEntry, string> values = new();
    private static readonly Queue<LogEntry> order = new();
    public static string Get(LogEntry entry)
    {
        lock (values)
        {
            if (values.TryGetValue(entry, out var cached)) return cached;
            string text = entry.File.Read(entry.Offset, Math.Min(entry.Length, 2048));
            int newline = text.IndexOf('\n');
            if (newline >= 0) text = text[..newline].TrimEnd('\r');
            text = text.TrimStart('\uFEFF');
            var match = LogParser.Header.Match(text);
            if (match.Success) text = text[match.Length..];
            if (text.Length > 400) text = text[..400] + "…";
            values.Add(entry, text);
            order.Enqueue(entry);
            if (order.Count > 2048) values.Remove(order.Dequeue());
            return text;
        }
    }
}

public readonly record struct WorkProgress(double Fraction, string Message);
public sealed record ParsedLog(LogFile File, List<LogEntry> Entries);

public static partial class LogParser
{
    [GeneratedRegex(@"^(?<date>\d{2}\.\d{2}\.\d{4} \d{1,2}:\d{2}:\d{2}) - \[(?<severity>[^\]\r\n]+)\] - \[(?<source>[^\]\r\n]*)\] - ?", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRegex();
    public static Regex Header { get; } = HeaderRegex();

    public static ParsedLog Parse(string path, Encoding encoding, int fileOrder, IProgress<WorkProgress>? progress, CancellationToken token)
    {
        var file = new LogFile(path, encoding, fileOrder);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            byte[] buffer = new byte[1024 * 1024];
            byte[] prefix = new byte[4096];
            int prefixLength = 0, lineNumber = 1;
            long position = 0, lineStart = 0;
            var entries = new List<LogEntry>();
            var severities = new Dictionary<string, string>(StringComparer.Ordinal);
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            string Intern(Dictionary<string, string> pool, string value)
            {
                if (pool.TryGetValue(value, out var found)) return found;
                pool[value] = value;
                return value;
            }
            void FinishLine()
            {
                string head = encoding.GetString(prefix, 0, prefixLength);
                if (lineStart == 0) head = head.TrimStart('\uFEFF');
                var match = Header.Match(head);
                DateTime time = default;
                bool valid = match.Success && DateTime.TryParseExact(match.Groups["date"].Value, "dd.MM.yyyy H:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
                if (valid || entries.Count == 0)
                {
                    if (entries.Count > 0) entries[^1].Length = lineStart - entries[^1].Offset;
                    entries.Add(new LogEntry(file, lineStart, lineNumber, valid ? time : null,
                        valid ? Intern(severities, match.Groups["severity"].Value) : "UNKNOWN",
                        valid ? Intern(sources, match.Groups["source"].Value) : ""));
                }
                prefixLength = 0;
                lineNumber++;
            }
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                int start = 0;
                while (start < count)
                {
                    int end = Array.IndexOf(buffer, (byte)'\n', start, count - start);
                    int segmentLength = (end < 0 ? count : end) - start;
                    int copy = Math.Min(segmentLength, prefix.Length - prefixLength);
                    buffer.AsSpan(start, copy).CopyTo(prefix.AsSpan(prefixLength));
                    prefixLength += copy;
                    if (end < 0) break;
                    FinishLine();
                    lineStart = position + end + 1;
                    start = end + 1;
                }
                position += count;
                progress?.Report(new WorkProgress(stream.Length == 0 ? 1 : (double)position / stream.Length, $"{file.Name} • {entries.Count:N0} záznamů"));
            }
            if (lineStart < position) FinishLine();
            if (entries.Count > 0) entries[^1].Length = position - entries[^1].Offset;
            token.ThrowIfCancellationRequested();
            return new ParsedLog(file, entries);
        }
        catch { file.Dispose(); throw; }
    }

    public static LogEntry[] Merge(IEnumerable<LogEntry> entries, CancellationToken token)
    {
        var array = entries.ToArray();
        // Bottom-up merge sort allows cancellation even while merging millions of entries.
        var scratch = new LogEntry[array.Length];
        for (long width = 1; width < array.Length; width *= 2)
        {
            for (long start = 0; start < array.Length; start += 2 * width)
            {
                token.ThrowIfCancellationRequested();
                int left = (int)start, mid = (int)Math.Min(start + width, array.Length);
                int right = mid, end = (int)Math.Min(start + 2 * width, array.Length);
                for (int target = left; target < end; target++)
                {
                    if ((target & 8191) == 0) token.ThrowIfCancellationRequested();
                    scratch[target] = left < mid && (right >= end || Compare(array[left], array[right]) <= 0) ? array[left++] : array[right++];
                }
            }
            (array, scratch) = (scratch, array);
        }
        return array;
    }

    private static int Compare(LogEntry a, LogEntry b)
    {
        int result = Nullable.Compare(a.Timestamp, b.Timestamp);
        if (result == 0) result = a.File.Order.CompareTo(b.File.Order);
        return result == 0 ? a.Offset.CompareTo(b.Offset) : result;
    }
}

