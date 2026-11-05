using System.IO;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

internal sealed class CleanupAnalyzer
{
    private readonly DateTime _nowUtc;
    internal CleanupAnalyzer(DateTime? nowUtc = null) => _nowUtc = nowUtc ?? DateTime.UtcNow;
    private readonly Dictionary<string, SuggestionBucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _userTemp = EnsureTrailingSeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
    private readonly string _windowsTemp = EnsureTrailingSeparator(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
    private readonly string _downloads = EnsureTrailingSeparator(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
    private static readonly RuleMatch[] Rules =
    [
        new("temporary-files", "Old temporary files", SuggestionConfidence.High, "Temporary files older than two days. Close applications before cleaning and review the location first."),
        new("recycle-bin", "Recycle Bin contents", SuggestionConfidence.High, "Files already placed in the Recycle Bin. Empty it through Windows after reviewing it."),
        new("old-dumps-temp", "Old dumps and temporary files", SuggestionConfidence.High, "Crash dumps or temporary files older than two weeks. Dumps can still be useful for diagnosing old crashes."),
        new("browser-caches", "Browser caches", SuggestionConfidence.Medium, "Cached browser data older than two weeks. Browsers can recreate it, but clean it through the browser when possible."),
        new("developer-caches", "Developer build caches", SuggestionConfidence.Medium, "Old build or package cache data. Tools normally recreate it, which may make the next build or restore slower."),
        new("old-downloads", "Old installers and archives in Downloads", SuggestionConfidence.Review, "Installer or archive files in Downloads older than four months. Confirm they are not your only copy."),
        new("large-logs", "Old large logs", SuggestionConfidence.Review, "Large log files older than a month. Check which application owns them before removing anything.")
    ];

    // Store matching rules as seven bits. Eligibility can then age naturally in
    // an incremental inventory without rereading unchanged paths or file metadata.
    internal int MatchMask(string path, long size)
    {
        if (size <= 0) return 0;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var mask = 0;
        if (path.StartsWith(_userTemp, StringComparison.OrdinalIgnoreCase) || path.StartsWith(_windowsTemp, StringComparison.OrdinalIgnoreCase)) mask |= 1;
        if (path.Contains("\\$recycle.bin\\", StringComparison.OrdinalIgnoreCase)) mask |= 2;
        if (extension is ".dmp" or ".mdmp" or ".hdmp" or ".tmp") mask |= 4;
        if (IsBrowserCache(path)) mask |= 8;
        if (IsDeveloperCache(path)) mask |= 16;
        if (path.StartsWith(_downloads, StringComparison.OrdinalIgnoreCase) && IsInstallerOrArchive(extension)) mask |= 32;
        if (extension is ".log" or ".etl" && size >= 100L * 1024 * 1024) mask |= 64;
        return mask;
    }

    internal static readonly int[] RuleDays = [2, 0, 14, 14, 30, 120, 30];
    internal static int EligibleCode(int mask, long modified, DateTime now)
    {
        for (var i = 0; i < RuleDays.Length; i++)
            if ((mask & (1 << i)) != 0 && (RuleDays[i] == 0 || modified <= now.AddDays(-RuleDays[i]).Ticks)) return i + 1;
        return 0;
    }

    internal static CleanupSuggestion FromCode(int code, long size, long count, string path)
    {
        var definition = Rules[code - 1];
        return new() { Category = definition.Category, Confidence = definition.Confidence, Reason = definition.Reason,
            Size = size, FileCount = count, SamplePath = path };
    }

    public void Consider(string path, long size, DateTime lastWriteUtc)
    {
        if (size <= 0) return;

        // Inventory paths are already absolute. Preserve literal NTFS leaf names
        // and avoid normalizing and lowercasing a long path for every file.
        var fullPath = System.IO.Path.IsPathFullyQualified(path) ? path : InventoryStore.Normalize(path);
        var normalized = fullPath.Replace('/', '\\');
        var extension = System.IO.Path.GetExtension(normalized).ToLowerInvariant();
        var age = _nowUtc - lastWriteUtc;

        RuleMatch? match = null;
        if ((normalized.StartsWith(_userTemp, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(_windowsTemp, StringComparison.OrdinalIgnoreCase)) && age.TotalDays >= 2)
        {
            match = Rules[0];
        }
        else if (normalized.Contains("\\$recycle.bin\\", StringComparison.OrdinalIgnoreCase))
        {
            match = Rules[1];
        }
        else if ((extension is ".dmp" or ".mdmp" or ".hdmp" || extension == ".tmp") && age.TotalDays >= 14)
        {
            match = Rules[2];
        }
        else if (IsBrowserCache(normalized) && age.TotalDays >= 14)
        {
            match = Rules[3];
        }
        else if (IsDeveloperCache(normalized) && age.TotalDays >= 30)
        {
            match = Rules[4];
        }
        else if (normalized.StartsWith(_downloads, StringComparison.OrdinalIgnoreCase) && IsInstallerOrArchive(extension) && age.TotalDays >= 120)
        {
            match = Rules[5];
        }
        else if ((extension is ".log" or ".etl") && size >= 100L * 1024 * 1024 && age.TotalDays >= 30)
        {
            match = Rules[6];
        }

        if (match is not null)
            Add(match, fullPath, size);
    }

    public IReadOnlyList<CleanupSuggestion> Build() => _buckets.Values
        .Select(x => new CleanupSuggestion
        {
            Category = x.Match.Category,
            Confidence = x.Match.Confidence,
            Reason = x.Match.Reason,
            Size = x.Size,
            FileCount = x.FileCount,
            SamplePath = x.LargestPath
        })
        .OrderByDescending(x => x.Size)
        .ToList();

    private void Add(RuleMatch match, string path, long size)
    {
        if (!_buckets.TryGetValue(match.Key, out var bucket))
        {
            bucket = new SuggestionBucket(match);
            _buckets.Add(match.Key, bucket);
        }
        bucket.Size += size;
        bucket.FileCount++;
        if (size > bucket.LargestSize)
        {
            bucket.LargestSize = size;
            bucket.LargestPath = path;
        }
    }

    private static bool IsBrowserCache(string path) =>
        (path.Contains("\\google\\chrome\\user data\\", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("\\microsoft\\edge\\user data\\", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("\\mozilla\\firefox\\profiles\\", StringComparison.OrdinalIgnoreCase)) &&
        (path.Contains("\\cache\\", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("\\code cache\\", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("\\cache2\\", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("\\gpucache\\", StringComparison.OrdinalIgnoreCase));

    private static bool IsDeveloperCache(string path) =>
        path.Contains("\\node_modules\\.cache\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\.gradle\\caches\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\.nuget\\packages\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\unity\\cache\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\appdata\\locallow\\unity\\cache\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsInstallerOrArchive(string extension) => extension is
        ".exe" or ".msi" or ".msix" or ".iso" or ".zip" or ".7z" or ".rar" or ".tar" or ".gz";

    private static string EnsureTrailingSeparator(string value) => value.TrimEnd('\\', '/') + "\\";

    private sealed record RuleMatch(string Key, string Category, SuggestionConfidence Confidence, string Reason);

    private sealed class SuggestionBucket(RuleMatch match)
    {
        public RuleMatch Match { get; } = match;
        public long Size { get; set; }
        public long FileCount { get; set; }
        public long LargestSize { get; set; }
        public string LargestPath { get; set; } = "";
    }
}
