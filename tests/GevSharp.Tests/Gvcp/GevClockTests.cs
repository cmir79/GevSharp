using GevSharp.Gvcp;

namespace GevSharp.Tests.Gvcp;

/// <summary>기한 계산용 단조 시계 — ns 해상도 틱(1 GHz)에서도 오버플로 없이 단조 증가해야 한다.</summary>
public class GevClockTests
{
    private const long NsPerSecond = 1_000_000_000L;

    [Fact]
    public void NanosecondTicksAfterMonthsOfUptimeConvertWithoutOverflow()
    {
        // 1 GHz 틱으로 200 일: 틱 × 1000 은 long 을 넘지만 몫·나머지 방식은 정확하다.
        var days200 = 200L * 86_400 * NsPerSecond;
        Assert.Throws<OverflowException>(() => checked(days200 * 1000L));

        Assert.Equal(200L * 86_400 * 1000, GevClock.ToMs(days200, NsPerSecond));
        Assert.Equal(200L * 86_400 * 1000, GevClock.ToMs(days200 + 999_999, NsPerSecond));
        Assert.Equal(200L * 86_400 * 1000 + 1, GevClock.ToMs(days200 + 1_000_000, NsPerSecond));
    }

    [Fact]
    public void IsMonotonicAcrossTheOldOverflowPoint()
    {
        // 틱 × 1000 이 뒤집히는 지점(long.MaxValue / 1000) 앞뒤에서도 단조 증가하고 값이 이어진다.
        var flip = long.MaxValue / 1000L;
        var before = GevClock.ToMs(flip - 1_000_000, NsPerSecond);
        var at = GevClock.ToMs(flip, NsPerSecond);
        var after = GevClock.ToMs(flip + 1_000_000, NsPerSecond);

        Assert.True(before > 0);
        Assert.Equal(flip / 1_000_000, at);
        Assert.Equal(at - 1, before);
        Assert.Equal(at + 1, after);
        Assert.True(GevClock.ToMs(long.MaxValue, NsPerSecond) > after);
    }

    [Fact]
    public void ConvertsTenMegahertzTicksAndAdvancesInRealTime()
    {
        Assert.Equal(1234, GevClock.ToMs(12_340_000, 10_000_000));
        Assert.Equal(0, GevClock.ToMs(0, NsPerSecond));
        Assert.Throws<ArgumentOutOfRangeException>(() => GevClock.ToMs(1, 0));

        var a = GevClock.NowMs();
        Thread.Sleep(20);
        var b = GevClock.NowMs();
        Assert.InRange(b - a, 10, 5000);
    }
}
