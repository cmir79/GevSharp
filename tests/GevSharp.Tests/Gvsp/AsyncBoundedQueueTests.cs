using GevSharp.Gvsp;

namespace GevSharp.Tests.Gvsp;

public class AsyncBoundedQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void KeepsFifoOrderAndRefusesBeyondCapacity()
    {
        var q = new AsyncBoundedQueue<int>(2);

        Assert.True(q.TryEnqueue(1));
        Assert.True(q.TryEnqueue(2));
        Assert.False(q.TryEnqueue(3));
        Assert.Equal(2, q.Count);

        Assert.True(q.TryDequeue(out var a));
        Assert.True(q.TryDequeue(out var b));
        Assert.False(q.TryDequeue(out _));
        Assert.Equal(1, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public async Task DequeueAsyncWaitsForAnItem()
    {
        var q = new AsyncBoundedQueue<int>(1);
        var pending = q.DequeueAsync(Ct).AsTask();

        await Task.Delay(20, Ct);
        Assert.False(pending.IsCompleted);

        Assert.True(q.TryEnqueue(7));
        Assert.Equal(7, await pending.WaitAsync(TimeSpan.FromSeconds(3), Ct));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public async Task ImmediateDequeueCompletesSynchronously()
    {
        var q = new AsyncBoundedQueue<int>(1);
        q.TryEnqueue(3);
        var vt = q.DequeueAsync(Ct);
        Assert.True(vt.IsCompletedSuccessfully);
        Assert.Equal(3, await vt);
    }

    [Fact]
    public async Task CompleteFailsPendingAndFutureDequeues()
    {
        var q = new AsyncBoundedQueue<int>(1);
        var pending = q.DequeueAsync(Ct).AsTask();

        q.Complete(new GevStreamClosedException("closed"));

        await Assert.ThrowsAsync<GevStreamClosedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3), Ct));
        await Assert.ThrowsAsync<GevStreamClosedException>(() => q.DequeueAsync(Ct).AsTask());
        Assert.Throws<GevStreamClosedException>(() => q.TryDequeue(out _));
        Assert.False(q.TryEnqueue(1));
        Assert.True(q.IsCompleted);

        // 두 번째 Complete 는 무시된다.
        q.Complete(new InvalidOperationException("second"));
        await Assert.ThrowsAsync<GevStreamClosedException>(() => q.DequeueAsync(Ct).AsTask());
    }

    [Fact]
    public async Task CompleteKeepsQueuedItemsReadableFirst()
    {
        var q = new AsyncBoundedQueue<int>(2);
        q.TryEnqueue(5);
        q.Complete(new GevStreamClosedException("closed"));

        Assert.True(q.TryDequeue(out var item));
        Assert.Equal(5, item);
        Assert.Throws<GevStreamClosedException>(() => q.TryDequeue(out _));
        await Assert.ThrowsAsync<GevStreamClosedException>(() => q.DequeueAsync(Ct).AsTask());
    }

    [Fact]
    public async Task CancellationReleasesWaiterWithoutLosingItems()
    {
        var q = new AsyncBoundedQueue<int>(1);
        using var cts = new CancellationTokenSource();
        var pending = q.DequeueAsync(cts.Token).AsTask();

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3), Ct));

        // 취소된 대기자에게 건네지지 않고 큐에 남는다.
        Assert.True(q.TryEnqueue(9));
        Assert.True(q.TryDequeue(out var item));
        Assert.Equal(9, item);
    }

    [Fact]
    public async Task PreCancelledTokenFailsImmediately()
    {
        var q = new AsyncBoundedQueue<int>(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => q.DequeueAsync(cts.Token).AsTask());
    }

    [Fact]
    public async Task ManyWaitersAreServedInOrder()
    {
        var q = new AsyncBoundedQueue<int>(4);
        var first = q.DequeueAsync(Ct).AsTask();
        var second = q.DequeueAsync(Ct).AsTask();

        Assert.True(q.TryEnqueue(1));
        Assert.True(q.TryEnqueue(2));

        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(3), Ct));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(3), Ct));
    }

    [Fact]
    public void RejectsZeroCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AsyncBoundedQueue<int>(0));
    }
}
