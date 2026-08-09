using System.Runtime.InteropServices;
using LanSwitch.Windows.Clipboard;
using LanSwitch.Windows.Interop;

namespace LanSwitch.Windows.Tests;

public sealed class ClipboardStaDispatcherTests
{
    [Fact]
    public async Task InvokeRunsOnADedicatedStaThread()
    {
        using var monitor = CreateDispatcherOnlyMonitor();
        var callerThreadId = Environment.CurrentManagedThreadId;
        monitor.Start();

        var result = await monitor.InvokeAsync(() =>
            (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));

        Assert.NotEqual(callerThreadId, result.CurrentManagedThreadId);
        Assert.Equal(ApartmentState.STA, result.Item2);
    }

    [Fact]
    public async Task ReentrantInvokeExecutesInlineWithoutDeadlocking()
    {
        using var monitor = CreateDispatcherOnlyMonitor();
        monitor.Start();

        var result = await monitor.InvokeAsync(() =>
            monitor.InvokeAsync(() => 42).GetAwaiter().GetResult()).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task FailedDispatchPostDoesNotConsumeTheNextWorkItemsWakeMessage()
    {
        var dispatchAttempts = 0;
        bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam)
        {
            if (message == NativeMethods.WmClipboardDispatch &&
                Interlocked.Increment(ref dispatchAttempts) == 1)
            {
                Marshal.SetLastPInvokeError(5);
                return false;
            }

            return NativeMethods.PostThreadMessage(threadId, message, wParam, lParam);
        }

        using var monitor = CreateDispatcherOnlyMonitor(postThreadMessage: PostThreadMessage);
        monitor.Start();

        var failedWork = monitor.InvokeAsync(() => 1);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => failedWork);

        var result = await monitor.InvokeAsync(() => 42).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(42, result);
        Assert.Equal(2, dispatchAttempts);
    }

    [Fact]
    public async Task QueuedWorkCanBeCanceledWithoutRunningClipboardOperation()
    {
        using var monitor = CreateDispatcherOnlyMonitor();
        using var operationStarted = new ManualResetEventSlim();
        using var releaseOperation = new ManualResetEventSlim();
        using var cancellationSource = new CancellationTokenSource();
        monitor.Start();

        var blockingWork = monitor.InvokeAsync(() =>
        {
            operationStarted.Set();
            Assert.True(releaseOperation.Wait(TimeSpan.FromSeconds(3)));
        });
        Assert.True(operationStarted.Wait(TimeSpan.FromSeconds(2)));

        var invocationCount = 0;
        var canceledWork = monitor.InvokeAsync(
            () => Interlocked.Increment(ref invocationCount),
            cancellationSource.Token);
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWork);
        releaseOperation.Set();
        await blockingWork;
        await monitor.InvokeAsync(() => true);
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task FullPendingQueueFailsFastAndAcceptsWorkAfterCapacityIsReleased()
    {
        var options = new ClipboardStaDispatcherOptions { MaxPendingWorkItems = 2 };
        using var monitor = CreateDispatcherOnlyMonitor(options);
        using var operationStarted = new ManualResetEventSlim();
        using var releaseOperation = new ManualResetEventSlim();
        monitor.Start();

        var blockingWork = monitor.InvokeAsync(() =>
        {
            operationStarted.Set();
            Assert.True(releaseOperation.Wait(TimeSpan.FromSeconds(3)));
        });
        Assert.True(operationStarted.Wait(TimeSpan.FromSeconds(2)));
        var firstPendingWork = monitor.InvokeAsync(() => 1);
        var secondPendingWork = monitor.InvokeAsync(() => 2);

        try
        {
            var exception = Assert.Throws<ClipboardDispatcherQueueFullException>(() =>
            {
                _ = monitor.InvokeAsync(() => 3);
            });
            Assert.Equal(2, exception.MaximumPendingWorkItems);
        }
        finally
        {
            releaseOperation.Set();
        }

        await blockingWork;
        Assert.Equal(1, await firstPendingWork);
        Assert.Equal(2, await secondPendingWork);
        Assert.Equal(4, await monitor.InvokeAsync(() => 4));
    }

    [Fact]
    public async Task StopCancelsQueuedWorkAndRejectsNewWork()
    {
        using var monitor = CreateDispatcherOnlyMonitor();
        using var operationStarted = new ManualResetEventSlim();
        using var releaseOperation = new ManualResetEventSlim();
        monitor.Start();

        var blockingWork = monitor.InvokeAsync(() =>
        {
            operationStarted.Set();
            Assert.True(releaseOperation.Wait(TimeSpan.FromSeconds(3)));
        });
        Assert.True(operationStarted.Wait(TimeSpan.FromSeconds(2)));
        var pendingWork = monitor.InvokeAsync(() => 42);

        var stopTask = Task.Run(monitor.Stop);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingWork);
        releaseOperation.Set();
        await blockingWork;
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(monitor.IsRunning);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = monitor.InvokeAsync(() => 42);
        });
    }

    [Fact]
    public async Task BusyClipboardFailureIsRetriedWithinConfiguredBound()
    {
        var options = new ClipboardStaDispatcherOptions
        {
            BusyRetryCount = 2,
            InitialBusyRetryDelay = TimeSpan.Zero,
            MaximumBusyRetryDelay = TimeSpan.Zero
        };
        using var monitor = CreateDispatcherOnlyMonitor(options);
        monitor.Start();
        var attempts = 0;

        var result = await monitor.InvokeAsync(() =>
        {
            if (Interlocked.Increment(ref attempts) <= 2)
            {
                throw new ExternalException("The clipboard is busy.");
            }

            return 42;
        });

        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task BusyClipboardFailureIsPropagatedAfterRetryLimit()
    {
        var options = new ClipboardStaDispatcherOptions
        {
            BusyRetryCount = 2,
            InitialBusyRetryDelay = TimeSpan.Zero,
            MaximumBusyRetryDelay = TimeSpan.Zero
        };
        using var monitor = CreateDispatcherOnlyMonitor(options);
        monitor.Start();
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<ExternalException>(() => monitor.InvokeAsync<int>(() =>
        {
            Interlocked.Increment(ref attempts);
            throw new ExternalException("The clipboard remains busy.");
        }));

        Assert.Equal("The clipboard remains busy.", exception.Message);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task CancellationInterruptsClipboardBusyRetryDelay()
    {
        var options = new ClipboardStaDispatcherOptions
        {
            BusyRetryCount = 10,
            InitialBusyRetryDelay = TimeSpan.FromMilliseconds(500),
            MaximumBusyRetryDelay = TimeSpan.FromMilliseconds(500)
        };
        using var monitor = CreateDispatcherOnlyMonitor(options);
        using var cancellationSource = new CancellationTokenSource();
        monitor.Start();
        var attempts = 0;

        var work = monitor.InvokeAsync<int>(
            () =>
            {
                Interlocked.Increment(ref attempts);
                throw new ExternalException("The clipboard is busy.");
            },
            cancellationSource.Token);
        cancellationSource.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            work.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void InvalidRetryOptionsAreRejectedBeforeStartingNativeThread()
    {
        var options = new ClipboardStaDispatcherOptions { BusyRetryCount = 11 };

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDispatcherOnlyMonitor(options));
    }

    [Fact]
    public void PendingWorkLimitDefaultsToFour()
    {
        Assert.Equal(4, new ClipboardStaDispatcherOptions().MaxPendingWorkItems);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void InvalidPendingWorkLimitsAreRejectedBeforeStartingNativeThread(int maximumPendingWorkItems)
    {
        var options = new ClipboardStaDispatcherOptions { MaxPendingWorkItems = maximumPendingWorkItems };

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDispatcherOnlyMonitor(options));
    }

    private static WindowsClipboardUpdateMonitor CreateDispatcherOnlyMonitor(
        ClipboardStaDispatcherOptions? options = null,
        ClipboardThreadMessagePoster? postThreadMessage = null)
    {
        return new WindowsClipboardUpdateMonitor(
            options,
            registerClipboardListener: false,
            postThreadMessage: postThreadMessage);
    }
}
