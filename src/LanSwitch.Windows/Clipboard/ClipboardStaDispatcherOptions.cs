using System.Runtime.InteropServices;

namespace LanSwitch.Windows.Clipboard;

public sealed record ClipboardStaDispatcherOptions
{
    public int MaxPendingWorkItems { get; init; } = 4;

    public int BusyRetryCount { get; init; } = 4;

    public TimeSpan InitialBusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(40);

    public TimeSpan MaximumBusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    internal void Validate()
    {
        if (MaxPendingWorkItems is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxPendingWorkItems),
                MaxPendingWorkItems,
                "The clipboard dispatcher pending work limit must be between one and 64.");
        }

        if (BusyRetryCount is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BusyRetryCount),
                BusyRetryCount,
                "The clipboard busy retry count must be between zero and ten.");
        }

        if (InitialBusyRetryDelay < TimeSpan.Zero || InitialBusyRetryDelay > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialBusyRetryDelay),
                InitialBusyRetryDelay,
                "The initial clipboard busy retry delay must be between zero and one second.");
        }

        if (MaximumBusyRetryDelay < InitialBusyRetryDelay || MaximumBusyRetryDelay > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumBusyRetryDelay),
                MaximumBusyRetryDelay,
                "The maximum clipboard busy retry delay must be at least the initial delay and no more than five seconds.");
        }
    }
}

public sealed class ClipboardDispatcherQueueFullException(int maximumPendingWorkItems)
    : InvalidOperationException(
        $"The clipboard dispatcher already has its maximum of {maximumPendingWorkItems} pending work items.")
{
    public int MaximumPendingWorkItems { get; } = maximumPendingWorkItems;
}

internal static class ClipboardBusyRetryPolicy
{
    internal static T Execute<T>(
        Func<T> operation,
        ClipboardStaDispatcherOptions options,
        CancellationToken cancellationToken)
    {
        for (var retryIndex = 0; ; retryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return operation();
            }
            catch (ExternalException) when (retryIndex < options.BusyRetryCount)
            {
                WaitForRetry(GetDelay(options, retryIndex), cancellationToken);
            }
        }
    }

    internal static TimeSpan GetDelay(ClipboardStaDispatcherOptions options, int retryIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryIndex);

        var delayTicks = options.InitialBusyRetryDelay.Ticks * (retryIndex + 1L);
        return TimeSpan.FromTicks(Math.Min(delayTicks, options.MaximumBusyRetryDelay.Ticks));
    }

    private static void WaitForRetry(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay == TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if (cancellationToken.WaitHandle.WaitOne(delay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
