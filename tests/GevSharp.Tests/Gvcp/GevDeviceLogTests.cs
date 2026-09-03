using GevSharp.Tests.GenApi.Model;

namespace GevSharp.Tests.Gvcp;

/// <summary>
/// 설정이 스스로 지킬 수 없는 값이 될 때 세션이 조용히 넘어가지 않는지 — 전역 싱크를 바꾸므로 격리 컬렉션에서만 돈다.
/// ⚠ 싱크를 잡고 있는 동안은 다른 테스트의 핫패스 로그까지 이 싱크로 흘러든다. 싱크를 쥔 채 장치를 열거나
/// 네트워크를 기다리지 않는다 — 순수 계산만 감싸 창을 마이크로초로 유지한다.
/// </summary>
[Collection(GevLogSinkCollection.Name)]
public class GevDeviceLogTests
{
    [Fact]
    public void ATooTightHeartbeatWarnsAndFallsBackToOneResponseWindow()
    {
        var logged = new List<(GevLogLevel Level, string Source, string Message)>();
        var prevSink = GevLog.Sink;
        var prevLevel = GevLog.MinLevel;
        int tight, roomy;
        try
        {
            GevLog.Sink = (lvl, src, msg, _) => { lock (logged) logged.Add((lvl, src, msg)); };
            GevLog.MinLevel = GevLogLevel.Warn;
            // 장치 타임아웃 1000, 주기 333, 응답 창 500 → 남는 예산(-333)이 응답 창 하나에도 못 미친다.
            tight = GevDevice.AutoPendingAckWaitMs(1000, 333, 500);
            // 여유가 있는 설정은 조용히 계산만 한다.
            roomy = GevDevice.AutoPendingAckWaitMs(3000, 1000, 300);
        }
        finally
        {
            GevLog.Sink = prevSink;
            GevLog.MinLevel = prevLevel;
        }

        Assert.Equal(500, tight);    // PENDING_ACK 이 아무것도 못 얻는 값(0·음수)으로는 떨어뜨리지 않는다
        Assert.Equal(1400, roomy);
        var entry = Assert.Single(logged);
        Assert.Equal(GevLogLevel.Warn, entry.Level);
        Assert.Equal("GevDevice", entry.Source);
        Assert.Contains("no PENDING_ACK budget", entry.Message);
    }
}
