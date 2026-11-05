using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcSpaceMap.Models;
using PcSpaceMap.Services;

namespace PcSpaceMap.Service;

internal sealed class IndexWorker(ServiceProtocol.Settings settings, ILogger<IndexWorker> log) : BackgroundService
{
    private readonly object _gate = new();
    private readonly int _maxConcurrentScans = Math.Clamp(settings.MaxConcurrentScans, 1, ServiceProtocol.MaxScanSlots);
    private readonly DiskScanSlots _scanSlots = new(Math.Clamp(settings.MaxConcurrentScans, 1, ServiceProtocol.MaxScanSlots));
    private readonly Dictionary<string, DriveIndex> _drives = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private readonly SecurityIdentifier _client = new(settings.ClientSid);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (settings.Volumes.Count == 0 || settings.Volumes.Any(volume => !ServiceProtocol.IsDriveRoot(volume.Key) || string.IsNullOrWhiteSpace(volume.Value)))
            throw new InvalidDataException("Configure at least one fixed NTFS drive with its volume serial.");
        using var pipe = CreatePipe();
        foreach (var (path, fingerprint) in settings.Volumes)
        {
            var drive = new DriveIndex(path, fingerprint);
            _drives.Add(path, drive);
            if (settings.Live && VolumeMatches(drive)) TryWatch(drive);
            Start(drive, "open", stoppingToken);
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var watching = WatchAsync(lifetime.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    var request = await ServiceProtocol.ReadAsync<ServiceProtocol.Request>(pipe, deadline.Token);
                    var reply = Handle(request, stoppingToken);
                    await ServiceProtocol.WriteAsync(pipe, reply, deadline.Token);
                    // Disconnect can discard unread outgoing pipe data on Windows.
                    await pipe.ReadExactlyAsync(new byte[1], deadline.Token);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException)
                {
                    log.LogDebug("An index client disconnected or sent an invalid message.");
                }
                finally
                {
                    try { pipe.Disconnect(); }
                    catch (InvalidOperationException) when (!pipe.IsConnected) { }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
            await watching;
            Task[] work;
            lock (_gate)
            {
                foreach (var drive in _drives.Values) drive.Cancellation?.Cancel();
                work = _drives.Values.Select(drive => drive.Work ?? Task.CompletedTask).ToArray();
            }
            await Task.WhenAll(work);
            foreach (var drive in _drives.Values)
            {
                drive.Watcher?.Dispose();
                drive.Wal?.Dispose();
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!;
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(owner);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(_client, PipeAccessRights.ReadData | PipeAccessRights.WriteData
            | PipeAccessRights.ReadAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(ServiceProtocol.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, acl);
    }

    private ServiceProtocol.Response Handle(ServiceProtocol.Request request, CancellationToken token)
    {
        if (!ServiceProtocol.IsDriveRoot(request.Root) || !_drives.TryGetValue(request.Root, out var drive) || !VolumeMatches(drive))
            return new(false);
        string? scope = null;
        if (request.Scope is { } requested)
        {
            if (request.Operation is not ("scan" or "update")
                || !ServiceProtocol.TryResolveDriveScope(requested, out var requestedDrive, out var folder)
                || !string.Equals(requestedDrive, drive.Root, StringComparison.OrdinalIgnoreCase)
                || DiskScanner.IsBelow(folder, _directory))
                return new(true, Job: request.Job, Error: "The folder is outside the service's allowed inventory scope.");
            if (!string.Equals(folder, drive.Root, StringComparison.OrdinalIgnoreCase)) scope = folder;
        }
        lock (_gate)
        {
            var now = Environment.TickCount64;
            foreach (var expired in drive.Jobs.Where(pair => pair.Value.CompletedAt is long time && now - time > 300_000).Select(pair => pair.Key).ToArray())
                drive.Jobs.Remove(expired);
            DriveJob? job = null;
            if (request.Job is Guid id) drive.Jobs.TryGetValue(id, out job);
            if (request.Operation is "scan" or "update")
            {
                if (job is null)
                {
                    if (drive.Pending.Count >= 16 || drive.Jobs.Count >= 128)
                        return new(true, Error: "The service job queue is full.", Volume: drive.Fingerprint);
                    var idForRequest = request.Job ?? Guid.NewGuid();
                    if (drive.Active is { Scope: null, Background: true, YieldRequested: false } active
                        && drive.Cancellation is { IsCancellationRequested: false }
                        && drive.Recovery.Reason is null
                        && (active.Action != "open" || settings.Live)
                        && (drive.Result is null || (scope is null && request.Operation == "update"))
                        && _scanSlots.TryPromote(active.Priority) && !active.YieldRequested)
                    {
                        job = new DriveJob(idForRequest, request.Operation, scope, foreground: true) { Shared = active };
                        if (active.Progress?.Phase == ServiceProtocol.WaitingPhase)
                            active.Progress = WaitingProgress(drive, active);
                    }
                    else
                    {
                        job = new DriveJob(idForRequest, request.Operation, scope, foreground: true);
                        job.Progress = WaitingProgress(drive, job);
                        if (drive.Cancellation is null) Start(drive, job.Action, token, job);
                        else
                        {
                            drive.Pending.Enqueue(job);
                            if (drive.Active is { Priority.Foreground: false } background)
                                YieldBackground(background, drive.Cancellation);
                        }
                    }
                    drive.Jobs.Add(idForRequest, job);
                }
            }
            else if (request.Operation is "status" or "cancel")
            {
                if (request.Job.HasValue && job is null)
                    return new(true, Job: request.Job, Error: "The service no longer has this job.", Volume: drive.Fingerprint);
                if (request.Operation == "cancel" && job is { CompletedAt: null })
                {
                    if (ReferenceEquals(job, drive.Active)) drive.Cancellation?.Cancel();
                    else { job.Shared = null; job.Error = "Service scan stopped."; job.CompletedAt = now; }
                }
            }
            else return new(true, Error: "Unknown index operation.");
            var current = job?.Shared ?? job ?? drive.Active;
            var recovery = job is null && current is null ? drive.Recovery.Status(now, settings.Live) : null;
            return new(true, job?.Snapshot ?? drive.Result?.SnapshotId ?? 0, job?.Id ?? current?.Id,
                current is { CompletedAt: null }, job is not null ? job.Error : recovery ?? drive.Error,
                current?.Progress, Volume: drive.Fingerprint, SupportsFolderScans: true,
                RecoveryPending: settings.Live && recovery is not null);
        }
    }

    private void Start(DriveIndex drive, string action, CancellationToken token, DriveJob? job = null)
    {
        lock (_gate)
        {
            if (drive.Cancellation is not null || token.IsCancellationRequested) return;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            drive.Cancellation = cancel;
            drive.Active = job ??= new DriveJob(Guid.NewGuid(), action);
            drive.Error = null;
            job.Progress = WaitingProgress(drive, job);
            if (job.Scope is null) drive.DirtyAt = 0;
            drive.Work = Task.Run(async () =>
            {
                try
                {
                    using (await AcquireScanSlotAsync(drive, job, cancel))
                    {
                        if (!VolumeMatches(drive)) throw new IOException("The configured volume is unavailable or has changed.");
                        var scanner = new DiskScanner
                        {
                            DatabasePath = ServiceProtocol.DatabasePath(_directory, drive.Root, drive.Fingerprint),
                            ExcludedDirectory = _directory,
                            RequireJournalForDriveRefresh = true,
                            HistoryGrace = TimeSpan.FromMinutes(10),
                            Parallelism = Math.Clamp(Environment.ProcessorCount / _maxConcurrentScans, _maxConcurrentScans == 1 ? 2 : 1, 8),
                            RequiredVolumeSerial = Convert.ToUInt32(drive.Fingerprint.Split('|')[^1], 16)
                        };
                        var result = drive.Result ?? await scanner.LoadSavedAsync(drive.Root);
                        if (result is not null)
                        {
                            drive.Wal ??= result.Inventory!.KeepWalOpen();
                            lock (_gate) drive.Result = result;
                        }
                        var progress = new ProgressSink(p => PublishProgress(job, p));
                        if (job.Scope is not null && result is null)
                            throw new InvalidOperationException("Build the drive's first service index before refreshing an individual folder.");
                        if (action == "recover")
                        {
                            string? reason;
                            lock (_gate)
                            {
                                cancel.Token.ThrowIfCancellationRequested();
                                drive.Recovery.RebuildStarted(Environment.TickCount64);
                                reason = drive.Recovery.Reason;
                            }
                            result = await scanner.ScanAsync(drive.Root, progress, cancel.Token, reason + " Automatic index rebuild.");
                        }
                        else if ((action == "scan" && job.Scope is null) || (result is null && (action != "open" || settings.Live)))
                            result = await scanner.ScanAsync(drive.Root, progress, cancel.Token);
                        else if (result is not null && (job.Scope is not null || action == "update" || action == "open" && settings.Live))
                        {
                            try { result = await scanner.RefreshAsync(result, job.Scope, progress, cancel.Token); }
                            catch (JournalRefreshException ex)
                            {
                                cancel.Token.ThrowIfCancellationRequested();
                                bool rebuild;
                                bool automaticRebuild;
                                lock (_gate)
                                {
                                    var now = Environment.TickCount64;
                                    drive.Recovery.JournalFailed(now, ex.Message, ex.NeedsRebuild, ex.JournalUnavailable);
                                    rebuild = !job.Background || drive.Jobs.Values.Any(request => ReferenceEquals(request.Shared, job));
                                    automaticRebuild = drive.Recovery.Rebuild;
                                    if (!rebuild) job.Error = drive.Recovery.Status(now, settings.Live);
                                }
                                log.LogWarning(new EventId(2401, "JournalReplayFailed"),
                                    "NTFS journal replay failed for {Drive} at snapshot {Snapshot}. Reason: {Reason} {Outcome}",
                                    drive.Root, result.SnapshotId, ex.LogReason,
                                    rebuild ? "A requested full scan is starting." : "Saved index kept. " +
                                        (!settings.Live ? "Live updates are off."
                                            : automaticRebuild ? "Automatic rebuild scheduled." : "Journal retry scheduled."));
                                if (rebuild)
                                    result = await scanner.ScanAsync(drive.Root, progress, cancel.Token, ex.Message + " Rebuilt the index on request.");
                            }
                        }
                        if (result is not null) drive.Wal ??= result.Inventory!.KeepWalOpen();
                        lock (_gate)
                        {
                            drive.Result = result;
                            if (job.Scope is null && job.Error is null && result is not null) drive.Recovery.Succeeded();
                        }
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        if (!job.YieldRequested) drive.Error = job.Error = "Service scan stopped.";
                        if (job.YieldRequested || !job.Priority.Foreground)
                        {
                            var now = Environment.TickCount64;
                            if (drive.DirtyAt == 0) drive.FirstChangeAt = now;
                            drive.DirtyAt = now;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        drive.Error = job.Error = ex.Message;
                        if (job.Scope is null && drive.Recovery.Reason is not null)
                            drive.Recovery.AttemptFailed(Environment.TickCount64);
                    }
                    log.LogWarning(ex, "Could not index {Root}.", drive.Root);
                }
                finally
                {
                    lock (_gate)
                    {
                        drive.Cancellation = null;
                        drive.Active = null;
                        job.Progress = null;
                        job.Snapshot = drive.Result?.SnapshotId;
                        drive.CompletedAt = Environment.TickCount64;
                        job.CompletedAt = drive.CompletedAt;
                        foreach (var subscriber in drive.Jobs.Values.Where(item => ReferenceEquals(item.Shared, job)))
                        {
                            subscriber.Shared = null;
                            subscriber.Snapshot = job.Snapshot;
                            subscriber.Error = job.Error;
                            subscriber.CompletedAt = job.CompletedAt;
                        }
                        cancel.Dispose();
                        while (!token.IsCancellationRequested && drive.Pending.TryDequeue(out var next))
                        {
                            if (next.CompletedAt.HasValue) continue;
                            Start(drive, next.Action, token, next);
                            break;
                        }
                        if (!settings.Live && job.YieldRequested && drive.Cancellation is null)
                            Start(drive, "open", token);
                    }
                }
            });
        }
    }

    private async Task<IDisposable> AcquireScanSlotAsync(DriveIndex drive, DriveJob job, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        Action yield = () => YieldBackground(job, cancellation);
        if (_maxConcurrentScans == 1) return await _scanSlots.AcquireAsync([], token, job.Priority, yield);

        var disks = WindowsVolume.BackingDisks(drive.Root);
        while (true)
        {
            var slot = await _scanSlots.AcquireAsync(disks, token, job.Priority, yield);
            // Recheck after queuing because Windows disk numbers can change after a remount.
            var current = WindowsVolume.BackingDisks(drive.Root);
            if (disks.AsSpan().SequenceEqual(current)) return slot;
            slot.Dispose();
            disks = current;
        }
    }

    private static ScanProgress WaitingProgress(DriveIndex drive, DriveJob job) => new()
    {
        Phase = job.Priority.Foreground ? ServiceProtocol.PriorityWaitingPhase : ServiceProtocol.WaitingPhase,
        CurrentPath = drive.Root
    };

    private static void YieldBackground(DriveJob job, CancellationTokenSource cancellation)
    {
        if (job.YieldRequested) return;
        job.YieldRequested = true;
        _ = cancellation.CancelAsync();
    }

    private void PublishProgress(DriveJob job, ScanProgress progress)
    {
        lock (_gate) job.Progress = progress;
    }

    private async Task WatchAsync(CancellationToken token)
    {
        if (!settings.Live) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                foreach (var drive in _drives.Values)
                {
                    if (!VolumeMatches(drive))
                    {
                        drive.Watcher?.Dispose();
                        drive.Watcher = null;
                        continue;
                    }
                    if (drive.Watcher is null && TryWatch(drive)) MarkChanged(drive, null);
                    lock (_gate)
                    {
                        var now = Environment.TickCount64;
                        var cooldown = drive.Result?.ScanMethod == "Parallel directory scan" ? 30000 : 5000;
                        if (drive.Cancellation is not null) continue;
                        if (drive.Recovery.IsDue(now))
                            Start(drive, drive.Recovery.Rebuild ? "recover" : "update", token);
                        else if (drive.Recovery.Reason is null && drive.DirtyAt != 0 && now - drive.CompletedAt >= cooldown
                            && (now - drive.DirtyAt >= 1800 || now - drive.FirstChangeAt >= 12000))
                            Start(drive, "update", token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private bool TryWatch(DriveIndex drive)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(drive.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 65536
            };
            watcher.Changed += (_, e) => MarkChanged(drive, e.FullPath);
            watcher.Created += (_, e) => MarkChanged(drive, e.FullPath);
            watcher.Deleted += (_, e) => MarkChanged(drive, e.FullPath);
            watcher.Renamed += (_, e) => { MarkChanged(drive, e.OldFullPath); MarkChanged(drive, e.FullPath); };
            watcher.Error += (_, _) => MarkChanged(drive, null);
            watcher.EnableRaisingEvents = true;
            drive.Watcher = watcher;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            watcher?.Dispose();
            log.LogDebug("Could not watch {Root}: {Reason}", drive.Root, ex.Message);
            return false;
        }
    }

    private void MarkChanged(DriveIndex drive, string? path)
    {
        if (path is not null && (path.Equals(_directory, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return;
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (drive.DirtyAt == 0) drive.FirstChangeAt = now;
            drive.DirtyAt = now;
        }
    }

    private static bool VolumeMatches(DriveIndex drive) => ServiceProtocol.VolumeMatches(drive.Root, drive.Fingerprint);

    private sealed class DriveIndex(string root, string fingerprint)
    {
        public string Root { get; } = root;
        public string Fingerprint { get; } = fingerprint;
        public ScanResult? Result;
        public IDisposable? Wal;
        public FileSystemWatcher? Watcher;
        public Task? Work;
        public CancellationTokenSource? Cancellation;
        public DriveJob? Active;
        public readonly Queue<DriveJob> Pending = new();
        public readonly Dictionary<Guid, DriveJob> Jobs = [];
        public string? Error;
        public DriveRecovery Recovery { get; } = new();
        public long DirtyAt, FirstChangeAt, CompletedAt;
    }

    private sealed class DriveJob(Guid id, string action, string? scope = null, bool foreground = false)
    {
        public Guid Id { get; } = id;
        public string Action { get; } = action;
        public string? Scope { get; } = scope;
        public bool Background { get; } = !foreground;
        public DiskScanSlots.Priority Priority { get; } = new(foreground);
        public volatile bool YieldRequested;
        public DriveJob? Shared;
        public ScanProgress? Progress;
        public string? Error;
        public long? Snapshot;
        public long? CompletedAt;
    }

    private sealed class ProgressSink(Action<ScanProgress> publish) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => publish(value);
    }
}
