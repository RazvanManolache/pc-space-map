using System.Text.Json;
using PcSpaceMap.Services;

internal static class ReaderChecks
{
    internal static void Run(string suite, Action<bool, string> check)
    {
        var path = Path.Combine(suite, "reader-profiles.json");
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var serial = "one";
        var probes = 0;
        var tuner = new ReaderTuner(path, root => root[..1] + serial,
            (root, _, _) => Enumerable.Range(0, 32).Select(i => root + i).ToArray(),
            (sample, readers, token) => { probes++; token.ThrowIfCancellationRequested(); return sample[0].StartsWith("B")
                ? readers switch { 1 => .2, 2 => .15, 4 => .05, _ => .1 }
                : readers switch { 1 => .2, 2 => .105, 4 => .1, _ => .15 }; }, () => now);
        var a = tuner.Select("A:first", 8, default, _ => false);
        var count = probes;
        var aAgain = tuner.Select("A:other-folder", 8, default, _ => false);
        check(a.Readers == 2 && a.Calibrated && !a.Reused && aAgain.Reused && probes == count,
            "reader tuning prefers fewer readers within noise and reuses the profile across folders on the same volume");
        var b = tuner.Select("B:folder", 8, default, _ => false);
        check(b.Readers == 4 && tuner.Select("A:folder", 8, default, _ => false).Readers == 2
            && JsonSerializer.Deserialize<Dictionary<string, ReaderProfile>>(File.ReadAllText(path))!.Count == 2,
            "different volumes keep independent persisted reader profiles");
        serial = "replacement";
        check(!tuner.Select("A:folder", 8, default, _ => false).Reused, "replacing a volume invalidates its previous reader profile");
        now = now.AddDays(31);
        check(!tuner.Select("A:folder", 8, default, _ => false).Reused, "expired reader profiles are remeasured");
        var small = new ReaderTuner(Path.Combine(suite, "small-readers.json"), _ => "small", (_, _, _) => ["a", "b"],
            (_, _, _) => throw new Exception("Small scopes must not be benchmarked."));
        check(!small.Select("small", 8, default, _ => false).Calibrated && !File.Exists(Path.Combine(suite, "small-readers.json")),
            "tiny folder samples cannot overwrite drive-wide tuning");
        var cancelFile = Path.Combine(suite, "cancel-reader-profiles.json");
        using var cancelled = new CancellationTokenSource();
        var cancelling = new ReaderTuner(cancelFile, _ => "cancel", (_, _, _) => Enumerable.Repeat("path", 32).ToArray(),
            (_, _, token) => { cancelled.Cancel(); token.ThrowIfCancellationRequested(); return 1; });
        try { cancelling.Select("cancel", 8, cancelled.Token, _ => false); check(false, "reader probe cancellation"); }
        catch (OperationCanceledException) { check(!File.Exists(cancelFile), "cancelled reader probes never persist partial profiles"); }
        var manual = new ScanSettings { AutoTuneReaders = false, Parallelism = 4 }.Scanner();
        check(!manual.AutoTuneReaders && manual.Parallelism == 4, "manual reader settings override automatic tuning");
    }
}
