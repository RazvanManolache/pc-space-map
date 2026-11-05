using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PcSpaceMap.Services;

internal sealed record ReaderSelection(int Readers, bool Reused, bool Calibrated, IReadOnlyDictionary<int, double>? ProbeSeconds = null);
internal sealed record ReaderProfile(string Volume, string Mode, int Readers, DateTime MeasuredUtc, Dictionary<int, double> Seconds);

internal sealed class ReaderTuner
{
    private const string Mode = "directory-batches-v1";
    private readonly string _file;
    private readonly Func<string, string?> _volume;
    private readonly Func<string, CancellationToken, Func<string, bool>, IReadOnlyList<string>> _sample;
    private readonly Func<IReadOnlyList<string>, int, CancellationToken, double> _measure;
    private readonly Func<DateTime> _now;
    internal ReaderTuner(string? file = null, Func<string, string?>? volume = null,
        Func<string, CancellationToken, Func<string, bool>, IReadOnlyList<string>>? sample = null,
        Func<IReadOnlyList<string>, int, CancellationToken, double>? measure = null, Func<DateTime>? now = null)
    {
        _file = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCSpaceMap", "reader-profiles.json");
        _volume = volume ?? WindowsVolume.VolumeFingerprint;
        _sample = sample ?? Sample;
        _measure = measure ?? Measure;
        _now = now ?? (() => DateTime.UtcNow);
    }
    private Dictionary<string, ReaderProfile> Read()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, ReaderProfile>>(File.ReadAllText(_file)) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    internal ReaderSelection Select(string root, int maximum, CancellationToken token, Func<string, bool> excluded)
    {
        token.ThrowIfCancellationRequested();
        maximum = Math.Clamp(maximum, 1, 16);
        var volume = _volume(root);
        var key = volume + "|" + Mode;
        if (volume is not null && Read().TryGetValue(key, out var cached) && cached is not null && cached.Volume == volume && cached.Mode == Mode
            && cached.Readers is >= 1 and <= 16 && cached.MeasuredUtc <= _now() && _now() - cached.MeasuredUtc < TimeSpan.FromDays(30))
            return new(Math.Min(maximum, cached.Readers), true, true, cached.Seconds);
        var sample = _sample(root, token, excluded);
        // Tiny scopes are poor evidence for a drive-wide profile. Do not replace
        // a drive's learned setting with a measurement of a handful of folders.
        if (sample.Count < 32) return new(Math.Min(maximum, Math.Max(1, sample.Count)), false, false);
        var candidates = new[] { 1, 2, 4, 8, 16 }.Where(x => x <= maximum).ToArray();
        _measure(sample, 1, token); // Warm the same bounded sample for each candidate.
        var scores = candidates.ToDictionary(x => x, _ => 0d);
        foreach (var candidate in candidates.Concat(candidates.Reverse()))
        {
            token.ThrowIfCancellationRequested();
            var seconds = _measure(sample, candidate, token);
            if (!double.IsFinite(seconds) || seconds <= 0) throw new IOException("Reader probe returned an invalid timing.");
            scores[candidate] += seconds / 2;
        }
        var fastest = scores.Values.Min();
        var selected = candidates.First(x => scores[x] <= fastest * 1.1); // Prefer fewer readers within measurement noise.
        if (volume is not null) Save(key, new(volume, Mode, selected, _now(), scores), token);
        return new(selected, false, true, scores);
    }
    private void Save(string key, ReaderProfile profile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))))[..16];
        using var mutex = new Mutex(false, @"Local\PCSpaceMap.ReaderProfiles." + userKey);
        var acquired = false;
        string? temporary = null;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            token.ThrowIfCancellationRequested();
            var all = Read(); all[key] = profile;
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all));
            File.Move(temporary, _file, overwrite: true);
            temporary = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Profiling must not prevent inventory scanning. */ }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { }
            if (acquired) mutex.ReleaseMutex();
        }
    }
    private static IReadOnlyList<string> Sample(string root, CancellationToken token, Func<string, bool> excluded)
    {
        var result = new List<string>();
        var queue = new Queue<string>(); queue.Enqueue(root);
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        while (queue.Count > 0 && result.Count < 64)
        {
            token.ThrowIfCancellationRequested();
            var directory = queue.Dequeue();
            if (excluded(directory)) continue;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                result.Add(directory);
                foreach (var child in Directory.EnumerateDirectories(directory, "*", options).Take(64))
                    if (!excluded(child) && queue.Count < 128) queue.Enqueue(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
    private static double Measure(IReadOnlyList<string> directories, int readers, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        Parallel.ForEach(directories, new ParallelOptions { MaxDegreeOfParallelism = readers, CancellationToken = token }, directory =>
        {
            try
            {
                try
                {
                    using var entries = WindowsVolume.DirectoryEntries(directory, token).GetEnumerator();
                    for (var i = 0; i < 256 && entries.MoveNext(); i++) token.ThrowIfCancellationRequested();
                }
                catch (NotSupportedException)
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory).Take(256))
                    { token.ThrowIfCancellationRequested(); try { WindowsVolume.ReadEntry(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
        return Math.Max(clock.Elapsed.TotalSeconds, 0.000001);
    }
}
