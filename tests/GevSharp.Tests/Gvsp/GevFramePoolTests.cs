using GevSharp.Gvsp;

namespace GevSharp.Tests.Gvsp;

public class GevFramePoolTests
{
    [Fact]
    public void RentsUpToCountThenFails()
    {
        var pool = new GevFramePool(2, 16);

        Assert.True(pool.TryRent(16, out var a, out var va));
        Assert.True(pool.TryRent(16, out var b, out var vb));
        Assert.False(pool.TryRent(16, out _, out _));
        Assert.Equal(0, pool.FreeCount);
        Assert.NotSame(a, b);

        pool.Return(a, va);
        Assert.Equal(1, pool.FreeCount);
        Assert.True(pool.TryRent(16, out var c, out _));
        Assert.Same(a, c);
        pool.Return(b, vb);
    }

    [Fact]
    public void BuffersGrowLazilyToTheLargestRequest()
    {
        var pool = new GevFramePool(2, 16);

        Assert.True(pool.TryRent(100, out var a, out var va));
        Assert.True(a.Data.Length >= 100);
        Assert.Equal(100, pool.BufferBytes);

        // 두 번째 버퍼는 더 작게 요구해도 풀의 최대 크기로 맞춰진다.
        Assert.True(pool.TryRent(10, out var b, out var vb));
        Assert.True(b.Data.Length >= 100);

        pool.Return(a, va);
        pool.Return(b, vb);
    }

    [Fact]
    public void ReturnWithStaleVersionIsIgnored()
    {
        var pool = new GevFramePool(1, 8);

        Assert.True(pool.TryRent(8, out var buf, out var v1));
        pool.Return(buf, v1);
        pool.Return(buf, v1);
        Assert.Equal(1, pool.FreeCount);

        Assert.True(pool.TryRent(8, out var again, out var v2));
        Assert.Same(buf, again);
        Assert.NotEqual(v1, v2);

        // 이전 대여의 버전으로 반납하면 무시된다 — 지금 대여자의 버퍼를 풀어 버리지 않는다.
        pool.Return(buf, v1);
        Assert.Equal(0, pool.FreeCount);
        pool.Return(buf, v2);
        Assert.Equal(1, pool.FreeCount);
    }

    [Fact]
    public void FrameDataThrowsAfterDisposeAndDisposeIsIdempotent()
    {
        var pool = new GevFramePool(1, 8);
        Assert.True(pool.TryRent(8, out var buf, out var version));
        for (var i = 0; i < 8; i++) buf.Data[i] = (byte)(i + 1);

        var meta = new FrameMeta { FrameId = 3, PayloadSize = 8, Width = 8, Height = 1, Stride = 8, IsComplete = true };
        var frame = new GevFrame(pool, buf, version, in meta);

        Assert.Equal(3UL, frame.FrameId);
        Assert.Equal(8, frame.Data.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frame.ToArray());
        Assert.False(frame.IsDisposed);

        frame.Dispose();
        Assert.True(frame.IsDisposed);
        Assert.Equal(1, pool.FreeCount);
        Assert.Throws<ObjectDisposedException>(() => frame.Data);
        Assert.Throws<ObjectDisposedException>(() => frame.ToArray());

        // 여러 스레드에서 거듭 불러도 한 번만 반납된다.
        Parallel.For(0, 16, _ => frame.Dispose());
        Assert.Equal(1, pool.FreeCount);

        // 반납된 버퍼가 다시 대여돼도 Dispose 된 프레임은 계속 막힌다(대여 버전 자체는 아래 테스트가 본다).
        Assert.True(pool.TryRent(8, out var again, out _));
        Assert.Same(buf, again);
        Assert.Throws<ObjectDisposedException>(() => frame.Data);
    }

    [Fact]
    public void FrameDataThrowsWhenItsLeaseEndedWithoutDispose()
    {
        // 오늘의 수신 경로는 프레임을 만들기 전에만 버퍼를 돌려주지만, 막는 것은 그 순서가 아니라 대여 버전이다 —
        // 버전이 오르면 Dispose 표시가 없어도 옛 프레임은 다음 대여자의 픽셀을 읽지 못한다.
        var pool = new GevFramePool(1, 8);
        Assert.True(pool.TryRent(8, out var buf, out var version));
        for (var i = 0; i < 8; i++) buf.Data[i] = (byte)(i + 1);

        var meta = new FrameMeta { FrameId = 7, PayloadSize = 8, Width = 8, Height = 1, Stride = 8, IsComplete = true };
        var frame = new GevFrame(pool, buf, version, in meta);
        Assert.Equal(8, frame.Data.Length);

        // 프레임을 거치지 않고 버퍼가 반납되고 다음 대여가 시작된다.
        pool.Return(buf, version);
        Assert.True(pool.TryRent(8, out var again, out var nextVersion));
        Assert.Same(buf, again);
        Assert.NotEqual(version, nextVersion);

        Assert.False(frame.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => frame.Data);
        Assert.Throws<ObjectDisposedException>(() => frame.ToArray());

        // 뒤늦은 Dispose 도 지금 대여자의 버퍼를 풀어 버리지 않는다.
        frame.Dispose();
        Assert.Equal(0, pool.FreeCount);
        pool.Return(again, nextVersion);
        Assert.Equal(1, pool.FreeCount);
    }

    [Fact]
    public void ReleaseFreeDropsOnlyUnleasedBuffers()
    {
        var pool = new GevFramePool(2, 32);
        Assert.True(pool.TryRent(32, out var held, out var version));

        pool.ReleaseFree();
        Assert.Equal(32, held.Data.Length);

        pool.Return(held, version);
        Assert.Equal(2, pool.FreeCount);
    }

    [Fact]
    public void RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GevFramePool(0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GevFramePool(1, -1));
        var pool = new GevFramePool(1, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.TryRent(-1, out _, out _));
    }
}
