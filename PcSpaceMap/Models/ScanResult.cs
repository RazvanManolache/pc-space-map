namespace PcSpaceMap.Models;

public sealed class ScanResult
{
    public required ScanNode Root { get; init; }
    public required IReadOnlyList<ScanNode> LargestFiles { get; init; }
    public required IReadOnlyList<CleanupSuggestion> Suggestions { get; init; }
    public required IReadOnlyList<ScanIssue> Issues { get; init; }
    public required TimeSpan Duration { get; init; }
    public long FileCount { get; init; }
    public long DirectoryCount { get; init; }
    public long SkippedReparsePoints { get; init; }
}

public sealed class ScanProgress
{
    public string CurrentPath { get; init; } = "";
    public long FileCount { get; init; }
    public long DirectoryCount { get; init; }
    public long BytesFound { get; init; }
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
