using System.Diagnostics;
using System.Text;
using OfflineLogReader.Core;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
string root = Path.Combine(Path.GetTempPath(), "OfflineLogReader-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var opened = new List<LogFile>();
int checks = 0;
void Assert(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    checks++; Console.WriteLine("PASS: " + description);
}
ParsedLog Parse(string name, string text, Encoding? encoding = null, int order = 0)
{
    encoding ??= new UTF8Encoding(false);
    string path = Path.Combine(root, name);
    File.WriteAllText(path, text, encoding);
    var parsed = LogParser.Parse(path, encoding, order, null, CancellationToken.None);
    opened.Add(parsed.File);
    return parsed;
}
QueryOptions Q(string search = "", Exclusion[]? exclusions = null, string[]? severity = null, string[]? source = null, DateTime? from = null, DateTime? to = null, bool matchCase = false)
    => new(from, to, severity ?? [], source ?? [], exclusions ?? [], search, matchCase);
LogEntry[] Query(LogEntry[] entries, QueryOptions query) => LogQuery.Run(entries, query, null, CancellationToken.None);
try
{
    var a = Parse("App-26-09-09.txt", "03.09.2026 21:49:33 - [INFO] - [R-FP] - Starting publish\r\n03.09.2026 21:49:35 - [ERROR] - [R-D-5] - Failed\r\nSystem.Exception: Žluťoučký kůň\r\n   at Worker.Run()\r\n03.09.2026 21:49:37 - [WARNING] - [H] - Retry", new UTF8Encoding(true));
    Assert(a.Entries.Count == 3, "Multiline stack trace becomes one record; final line needs no newline");
    Assert(a.Entries[1].Text.Contains("Worker.Run()") && a.Entries[1].Text.Contains("Žluťoučký"), "UTF-8 and complete multiline text preserved");
    Assert(a.Entries[0].Timestamp == new DateTime(2026, 9, 3, 21, 49, 33), "BOM supported; date taken from content, not filename");
    Assert(a.Entries[2].Line == 5, "Physical line number retained");
    var b = Parse("second.log", "03.09.2026 21:49:35 - [INFO] - [Other] - equal time\n02.09.2026 01:00:00 - [DEBUG] - [Other] - earlier\n", order: 1);
    var merged = LogParser.Merge(a.Entries.Concat(b.Entries), CancellationToken.None);
    Assert(merged[0] == b.Entries[1] && merged[2] == a.Entries[1] && merged[3] == b.Entries[0], "Chronological merge sorts unordered files with stable ties");
    Assert(Query(merged, Q(severity: ["WARNING", "ERROR"])).Length == 2, "Multiple severities use OR");
    Assert(Query(merged, Q(severity: ["WARNING", "ERROR"], source: ["R-D-5"])).Single() == a.Entries[1], "Different filter categories use AND");
    Assert(Query(merged, Q(search: "worker.run")).Single() == a.Entries[1], "Search includes stack traces and ignores case by default");
    Assert(Query(merged, Q(search: "worker.run", matchCase: true)).Length == 0, "Case-sensitive search");
    Assert(Query(merged, Q(exclusions: [new("publish", false), new("Worker.Run", false)])).Length == 3, "Any matching exclusion hides the entire record");
    Assert(Query(merged, Q(exclusions: [new("Failed", false, false)])).Length == 5, "Disabled exclusion ignored");
    Assert(Query(merged, Q(from: a.Entries[1].Timestamp, to: a.Entries[1].Timestamp)).Length == 2, "Date boundaries are inclusive");
    var unknown = Parse("unknown.log", "preamble\nmore preamble\n03.09.2026 00:00:00 - [INFO] - [] - valid\n");
    Assert(unknown.Entries.Count == 2 && unknown.Entries[0].Timestamp == null && unknown.Entries[0].Text.Contains("more preamble"), "Unrecognized preamble retained");
    var legacy = Parse("legacy.log", "03.09.2026 00:00:00 - [INFO] - [Žluťoučký] - Příliš žluťoučký kůň\n", Encoding.GetEncoding(1250));
    Assert(legacy.Entries[0].Source == "Žluťoučký" && legacy.Entries[0].Text.Contains("Příliš"), "Windows-1250 supported");
    var large = Parse("boundary.log", "03.09.2026 00:00:00 - [INFO] - [A] - " + new string('x', 1024 * 1024 - 44) + "\n03.09.2026 00:00:01 - [ERROR] - [B] - boundary\n");
    Assert(large.Entries.Count == 2 && large.Entries[1].Text.EndsWith("boundary"), "Header crossing the read buffer boundary parsed correctly");
    var huge = Parse("huge-record.log", "03.09.2026 00:00:00 - [INFO] - [A] - " + new string('a', 65500) + "Žluťoučký" + new string('x', 1024 * 1024) + " needle-at-end");
    Assert(Query(huge.Entries.ToArray(), Q(search: "Žluťoučký")).Length == 1, "Streaming search preserves UTF-8 and matches across chunks");
    Assert(Query(huge.Entries.ToArray(), Q(search: "Žluťoučký", exclusions: [new("needle-at-end", false)])).Length == 0, "Streaming search checks exclusions after an early search match");
    Assert(Parse("empty.log", "").Entries.Count == 0, "Empty file supported");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { LogParser.Parse(a.File.Path, Encoding.UTF8, 0, null, canceled.Token); throw new Exception("Missing cancellation"); }
    catch (OperationCanceledException) { Assert(true, "Loading cancellation"); }
    try { LogQuery.Run(merged, Q(), null, canceled.Token); throw new Exception("Missing cancellation"); }
    catch (OperationCanceledException) { Assert(true, "Filtering cancellation"); }
    try { LogParser.Merge(merged, canceled.Token); throw new Exception("Missing cancellation"); }
    catch (OperationCanceledException) { Assert(true, "Sorting cancellation"); }
    var snapshot = Query(merged, Q(search: "Failed"));
    Query(merged, Q(search: "Retry"));
    Assert(snapshot.Length == 1 && snapshot[0] == a.Entries[1], "Search result snapshot survives subsequent queries");
    var morning = Parse("morning.log", "07.09.2026 0:00:00 - [INFO] - [R-J-4] - Job started\n07.09.2026 9:59:59 - [ERROR] - [R-D-5] - Failed\n   at Worker.Run()\n07.09.2026 10:00:00 - [WARNING] - [H] - Retry\n07.09.2026 09:00:00 - [INFO] - [H] - Padded hour\n");
    Assert(morning.Entries.Count == 4 && morning.Entries.All(x => x.Timestamp.HasValue), "Single-digit morning hours and padded hours each start a record");
    Assert(morning.Entries[0].Text.EndsWith("Job started") && !morning.Entries[0].Text.Contains("Failed"), "Midnight detail does not swallow subsequent morning records");
    Assert(morning.Entries[1].Timestamp == new DateTime(2026, 9, 7, 9, 59, 59) && morning.Entries[1].Text.EndsWith("Worker.Run()"), "Morning exception retains only its own stack trace");
    Assert(Query(morning.Entries.ToArray(), Q(severity: ["ERROR"], source: ["R-D-5"], from: new DateTime(2026, 9, 7), to: new DateTime(2026, 9, 7, 9, 59, 59))).Single() == morning.Entries[1], "Severity, source and date filters include morning records");
    Console.WriteLine($"All {checks} checks passed.");

    if (args.Length == 2 && args[0] == "--inspect")
    {
        var parsed = LogParser.Parse(args[1], Encoding.UTF8, 0, null, CancellationToken.None);
        opened.Add(parsed.File);
        var records = parsed.Entries.ToArray();
        Console.WriteLine($"INSPECT entries={records.Length}, unknown={records.Count(x => x.Timestamp == null)}, firstRecordBytes={records.FirstOrDefault()?.Length}");
        Console.WriteLine($"INSPECT morning={records.Count(x => x.Timestamp?.Hour < 10)}");
        foreach (var group in records.GroupBy(x => x.Severity))
        {
            var selected = Query(records, Q(severity: [group.Key]));
            Assert(selected.Length == group.Count(), $"Actual log severity {group.Key}: {selected.Length} records");
        }
        var morningErrors = Query(records, Q(severity: ["ERROR"], to: new DateTime(2026, 9, 7, 9, 59, 59)));
        Assert(morningErrors.All(x => x.Timestamp?.Hour < 10 && x.Severity == "ERROR"), $"Actual log morning ERROR filter: {morningErrors.Length} records");
    }

    if (args.Length == 2 && args[0] == "--benchmark-mb")
    {
        int megabytes = int.Parse(args[1]);
        string path = Path.Combine(root, "benchmark.log");
        byte[] record = Encoding.UTF8.GetBytes("03.09.2026 21:49:33 - [INFO] - [R-D-5] - Download completed " + new string('x', 380) + "\n");
        byte[] error = Encoding.UTF8.GetBytes("03.09.2026 21:49:34 - [ERROR] - [R-D-5] - Download failed\n   at Worker.Run()\n");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
        {
            long target = (long)megabytes * 1024 * 1024;
            int i = 0;
            while (stream.Position < target) stream.Write(++i % 1000 == 0 ? error : record);
        }
        var watch = Stopwatch.StartNew();
        var parsed = LogParser.Parse(path, Encoding.UTF8, 0, null, CancellationToken.None);
        opened.Add(parsed.File);
        Console.WriteLine($"BENCH {megabytes} MiB, {parsed.Entries.Count:N0} entries: index {watch.Elapsed.TotalSeconds:F2}s");
        watch.Restart();
        var sorted = LogParser.Merge(parsed.Entries, CancellationToken.None);
        Console.WriteLine($"BENCH sort {watch.Elapsed.TotalSeconds:F2}s");
        watch.Restart();
        var filtered = Query(sorted, Q(severity: ["ERROR"]));
        Console.WriteLine($"BENCH severity {watch.Elapsed.TotalSeconds:F2}s, {filtered.Length:N0} matches");
        watch.Restart();
        var found = Query(sorted, Q(search: "Worker.Run"));
        Console.WriteLine($"BENCH full-text {watch.Elapsed.TotalSeconds:F2}s, {found.Length:N0} matches");
        Assert(found.Length == filtered.Length && found.Length > 0, "Large-file full-text results match metadata filter");
        Console.WriteLine($"BENCH peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024:N0} MiB; managed {GC.GetTotalMemory(false) / 1024 / 1024:N0} MiB");
    }
}
finally
{
    foreach (var file in opened) file.Dispose();
    // Only this run's newly created, uniquely named test directory is removed.
    Directory.Delete(root, true);
}

