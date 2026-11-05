namespace PcSpaceMap.Models;

public sealed class ScanResult
{
    public PcSpaceMap.Services.InventoryStore? Inventory { get; init; }
    public long SnapshotId { get; init; }
    public string ScanMethod { get; init; } = "Directory walk";
    public DateTime CompletedUtc { get; init; }
    public IReadOnlyList<InventoryChange> Changes { get; init; } = [];
    private IReadOnlyList<ScanPhaseTiming> _timings = [];
    public IReadOnlyList<ScanPhaseTiming> Timings { get => _timings; init => _timings = value; }
    public ScanWork? Work { get; init; }
    public required ScanNode Root { get; init; }
    public required IReadOnlyList<ScanNode> LargestFiles { get; init; }
    public required IReadOnlyList<CleanupSuggestion> Suggestions { get; init; }
    public required IReadOnlyList<ScanIssue> Issues { get; init; }
    private TimeSpan _duration;
    public required TimeSpan Duration { get => _duration; init => _duration = value; }
    internal void IncludePhase(string phase, TimeSpan duration) { _duration += duration; _timings = [new(phase, duration.TotalSeconds), .._timings]; }
    internal void SetDuration(TimeSpan duration) => _duration = duration;
    public long FileCount { get; init; }
    public long DirectoryCount { get; init; }
    public long SkippedReparsePoints { get; init; }
}

public sealed record ScanPhaseTiming(string Phase, double Seconds);
public sealed record ScanWork(string StorageFormat, long VersionsWritten, long FoldersRecalculated, int ReaderCount = 0,
    bool ReaderProfileReused = false, IReadOnlyDictionary<int, double>? ReaderProbeSeconds = null);

public sealed record InventoryChange(string Path, string Kind, long BeforeBytes, long AfterBytes)
{
    public long DeltaBytes => AfterBytes - BeforeBytes;
    public string DeltaText => (DeltaBytes > 0 ? "+" : DeltaBytes < 0 ? "−" : "") + SizeFormatter.Format(Math.Abs(DeltaBytes));
}

public sealed class ScanProgress
{
    public string Phase { get; init; } = "Scanning";
    public string CurrentPath { get; init; } = "";
    public long FileCount { get; init; }
    public long DirectoryCount { get; init; }
    public long BytesFound { get; init; }
    public long? CompletedUnits { get; init; }
    public long? TotalUnits { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int? PhasePercent => CompletedUnits is >= 0 && TotalUnits is > 0
        ? (int)Math.Clamp(100.0 * CompletedUnits.Value / TotalUnits.Value, 0, 100)
        : null;
}

public sealed class ScanIssue
{
    public required string Path { get; init; }
    public required string Message { get; init; }
}

public enum SuggestionConfidence
{
    High,
    Medium,
    Review
}

public sealed class CleanupSuggestion
{
    public required string Category { get; init; }
    public required SuggestionConfidence Confidence { get; init; }
    public required string Reason { get; init; }
    public required long Size { get; init; }
    public required long FileCount { get; init; }
    public required string SamplePath { get; init; }
    public string SizeText => SizeFormatter.Format(Size);
    public string FileCountText => FileCount.ToString("N0");
    public string ConfidenceText => Confidence switch
    {
        SuggestionConfidence.High => "Likely safe",
        SuggestionConfidence.Medium => "Usually safe",
        _ => "Review first"
    };
}
