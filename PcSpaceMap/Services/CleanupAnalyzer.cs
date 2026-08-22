using System.IO;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

internal sealed class CleanupAnalyzer
{
    private readonly Dictionary<string, SuggestionBucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _userTemp = EnsureTrailingSeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
    private readonly string _windowsTemp = EnsureTrailingSeparator(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
    private readonly string _downloads = EnsureTrailingSeparator(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

    public void Consider(string path, long size, DateTime lastWriteUtc)
    {
        if (size <= 0) return;

        var fullPath = System.IO.Path.GetFullPath(path);
        var normalized = fullPath.Replace('/', '\\');
        var lower = normalized.ToLowerInvariant();
        var extension = System.IO.Path.GetExtension(lower);
        var age = DateTime.UtcNow - lastWriteUtc;

        RuleMatch? match = null;
        if ((lower.StartsWith(_userTemp, StringComparison.OrdinalIgnoreCase) || lower.StartsWith(_windowsTemp, StringComparison.OrdinalIgnoreCase)) && age.TotalDays >= 2)
        {
            match = new("temporary-files", "Old temporary files", SuggestionConfidence.High,
                "Temporary files older than two days. Close applications before cleaning and review the location first.");
        }
        else if (lower.Contains("\\$recycle.bin\\", StringComparison.OrdinalIgnoreCase))
        {
            match = new("recycle-bin", "Recycle Bin contents", SuggestionConfidence.High,
                "Files already placed in the Recycle Bin. Empty it through Windows after reviewing it.");
        }
        else if ((extension is ".dmp" or ".mdmp" or ".hdmp" || extension == ".tmp") && age.TotalDays >= 14)
        {
            match = new("old-dumps-temp", "Old dumps and temporary files", SuggestionConfidence.High,
                "Crash dumps or temporary files older than two weeks. Dumps can still be useful for diagnosing old crashes.");
        }
        else if (IsBrowserCache(lower) && age.TotalDays >= 14)
        {
            match = new("browser-caches", "Browser caches", SuggestionConfidence.Medium,
                "Cached browser data older than two weeks. Browsers can recreate it, but clean it through the browser when possible.");
        }
        else if (IsDeveloperCache(lower) && age.TotalDays >= 30)
        {
            match = new("developer-caches", "Developer build caches", SuggestionConfidence.Medium,
                "Old build or package cache data. Tools normally recreate it, which may make the next build or restore slower.");
        }
        else if (lower.StartsWith(_downloads, StringComparison.OrdinalIgnoreCase) && IsInstallerOrArchive(extension) && age.TotalDays >= 120)
        {
            match = new("old-downloads", "Old installers and archives in Downloads", SuggestionConfidence.Review,
                "Installer or archive files in Downloads older than four months. Confirm they are not your only copy.");
        }
        else if ((extension is ".log" or ".etl") && size >= 100L * 1024 * 1024 && age.TotalDays >= 30)
        {
            match = new("large-logs", "Old large logs", SuggestionConfidence.Review,
                "Large log files older than a month. Check which application owns them before removing anything.");
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
