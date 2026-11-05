namespace PcSpaceMap.Service;

// Accessed under the worker's drive-state lock. Times are monotonic milliseconds.
internal sealed class DriveRecovery
{
    private const long RetryDelay = 30_000;
    private const long UnavailableDelay = 120_000;
    private const long RebuildCooldown = 3_600_000;
    private long _retryAt;
    private long _nextRebuild;
    private int _verificationFailures;

    public string? Reason { get; private set; }
    public bool Rebuild { get; private set; }

    public void JournalFailed(long now, string reason, bool needsRebuild, bool journalUnavailable)
    {
        Reason = reason;
        _verificationFailures = journalUnavailable ? 0 : Math.Min(2, _verificationFailures + 1);
        Rebuild = !journalUnavailable && (needsRebuild || _verificationFailures >= 2);
        _retryAt = now + (journalUnavailable ? UnavailableDelay : RetryDelay);
    }

    private long DueAt => Rebuild ? Math.Max(_retryAt, _nextRebuild) : _retryAt;

    public bool IsDue(long now) => Reason is not null && now >= DueAt;

    public void RebuildStarted(long now) => _nextRebuild = now + RebuildCooldown;

    public void AttemptFailed(long now) => _retryAt = now + UnavailableDelay;

    public void Succeeded()
    {
        Reason = null;
        Rebuild = false;
        _verificationFailures = 0;
    }

    public string? Status(long now, bool live)
    {
        if (Reason is not { } reason) return null;
        if (!live) return "Saved index kept. Select Update changes at the drive root to rebuild. " + reason;
        var seconds = Math.Max(0, (DueAt - now + 999) / 1000);
        var next = Rebuild ? "Automatic rebuild" : "Journal retry";
        return $"Saved index kept. {next} {(seconds == 0 ? "due" : $"in {seconds}s")}. {reason}";
    }
}
