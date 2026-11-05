using System.Text.Json;
using PcSpaceMap.Service;
using PcSpaceMap.Services;

internal static class DiskScanSlotChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var older = JsonSerializer.Deserialize<ServiceProtocol.Settings>("""
            {"ClientSid":"S-1-5-18","Volumes":{},"Live":true,"AutoStart":true}
            """);
        check(older?.MaxConcurrentScans == 1, "existing service settings keep serial scanning by default");

        var scheduler = new DiskScanSlots(2);
        using var first = await scheduler.AcquireAsync([3], default).WaitAsync(Timeout);
        var overlapping = scheduler.AcquireAsync([3, 4], default);
        using var independent = await scheduler.AcquireAsync([0], default).WaitAsync(Timeout);
        var beyondLimit = scheduler.AcquireAsync([2], default);
        check(!overlapping.IsCompleted && !beyondLimit.IsCompleted,
            "independent disks overlap while a shared disk and work above the configured limit wait");

        first.Dispose();
        using var spanning = await overlapping.WaitAsync(Timeout);
        var sharesSecondExtent = scheduler.AcquireAsync([4], default);
        independent.Dispose();
        using var next = await beyondLimit.WaitAsync(Timeout);
        check(!sharesSecondExtent.IsCompleted, "a volume spanning disks reserves every backing disk");
        spanning.Dispose();
        using var released = await sharesSecondExtent.WaitAsync(Timeout);

        var unknown = scheduler.AcquireAsync([], default);
        using var cancellation = new CancellationTokenSource();
        var canceled = scheduler.AcquireAsync([2], cancellation.Token);
        var behindUnknown = scheduler.AcquireAsync([5], default);
        cancellation.Cancel();
        try { await canceled.WaitAsync(Timeout); throw new Exception("The queued job should have canceled."); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        released.Dispose();
        check(!unknown.IsCompleted && !behindUnknown.IsCompleted,
            "a waiting scan with unknown backing disks drains other work before it starts");
        next.Dispose();
        using var exclusive = await unknown.WaitAsync(Timeout);
        check(!behindUnknown.IsCompleted, "a scan with unknown backing disks runs alone");
        exclusive.Dispose();
        using var afterExclusive = await behindUnknown.WaitAsync(Timeout);
        using var afterCancel = await scheduler.AcquireAsync([2], default).WaitAsync(Timeout);
        check(true, "canceling a queued scan releases its place without losing capacity");

        var priorityScheduler = new DiskScanSlots(2);
        var yielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var background = await priorityScheduler.AcquireAsync([3], default, yieldBackground: () => yielded.TrySetResult());
        using var independentBackground = await priorityScheduler.AcquireAsync([4], default,
            yieldBackground: () => throw new InvalidOperationException("The independent disk should keep scanning."));
        var queuedBackground = priorityScheduler.AcquireAsync([3], default);
        var foreground = priorityScheduler.AcquireAsync([3], default, new DiskScanSlots.Priority(true));
        await yielded.Task.WaitAsync(Timeout);
        check(!foreground.IsCompleted && !queuedBackground.IsCompleted,
            "a foreground scan asks only the conflicting background scan to yield and waits for its slot");
        background.Dispose();
        using var foregroundSlot = await foreground.WaitAsync(Timeout);
        check(!queuedBackground.IsCompleted, "a foreground scan takes the released disk before queued background work");
        foregroundSlot.Dispose();
        using var resumed = await queuedBackground.WaitAsync(Timeout);

        var singleSlot = new DiskScanSlots(1);
        var protectedPriority = new DiskScanSlots.Priority(true);
        using var existingForeground = await singleSlot.AcquireAsync([1], default, protectedPriority,
            () => throw new InvalidOperationException("An existing foreground scan must not be interrupted."));
        var earlier = singleSlot.AcquireAsync([2], default);
        var attachedPriority = new DiskScanSlots.Priority(false);
        var attached = singleSlot.AcquireAsync([3], default, attachedPriority);
        if (!singleSlot.TryPromote(attachedPriority)) throw new InvalidOperationException("A waiting scan should be promotable.");
        check(!attached.IsCompleted, "promotion does not interrupt an existing foreground scan");
        existingForeground.Dispose();
        using var promoted = await attached.WaitAsync(Timeout);
        check(!earlier.IsCompleted, "promoting a waiting scan moves it ahead of background work");
        promoted.Dispose();
        using var earlierSlot = await earlier.WaitAsync(Timeout);
    }
}
