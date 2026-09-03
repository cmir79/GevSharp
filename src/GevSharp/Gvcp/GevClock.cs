using System.Diagnostics;

namespace GevSharp.Gvcp;

/// <summary>
/// 기한 계산용 단조 시계(ms). <see cref="Stopwatch"/> 틱을 ms 로 바꿀 때 초 단위 몫과 나머지를 따로 계산해 곱셈 오버플로를 피한다 —
/// 틱이 ns 단위(1 GHz)인 플랫폼에서는 틱 × 1000 이 부팅 100여 일 만에 long 을 넘어 값이 뒤집히고, 그 값으로 만든 기한은
/// 즉시 만료되거나 며칠짜리 대기가 된다. 몫·나머지 방식은 주파수가 초당 9×10^15 틱을 넘지 않는 한 오버플로가 없다.
/// </summary>
internal static class GevClock
{
    /// <summary>부팅 기준 단조 시각(ms). 벽시계 조정에 영향받지 않는다.</summary>
    internal static long NowMs() => ToMs(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    /// <summary>틱을 ms 로 바꾼다. frequency 는 초당 틱 수(양수). 정수 연산만 쓰므로 결과는 정확히 내림한 ms 다.</summary>
    internal static long ToMs(long timestamp, long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency), "must be positive");
        var seconds = timestamp / frequency;
        var remainder = timestamp % frequency;
        return seconds * 1000L + remainder * 1000L / frequency;
    }
}
