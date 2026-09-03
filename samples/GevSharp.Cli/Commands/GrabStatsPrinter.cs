using System.Diagnostics;

namespace GevSharp.Cli.Commands;

/// <summary>
/// 스트림 카운터를 구간별(델타)과 최종(누적)으로 찍는다. 구간 값은 직전 스냅샷과의 차이라 fps·MB/s 가 그 구간의 실측이다.
/// MB 는 10^6 바이트, 바이트 수는 GVSP 헤더 포함·IP/UDP 헤더 제외(라이브러리 카운터 기준).
/// </summary>
public sealed class GrabStatsPrinter
{
    private readonly GevStream _stream;
    private readonly TextWriter _out;
    private readonly Stopwatch _clock = new();
    private GevStreamStatsSnap _prev;
    private double _prevSeconds;

    public GrabStatsPrinter(GevStream stream, TextWriter output)
    {
        _stream = stream;
        _out = output;
    }

    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;

    /// <summary>획득 시작 시점에 부른다 — 여기서부터 시간을 센다.</summary>
    public void Start()
    {
        _prev = _stream.Stats.Snapshot();
        _prevSeconds = 0;
        _clock.Restart();
    }

    /// <summary>취소될 때까지 interval 마다 구간 통계 한 줄. 취소는 조용히 끝난다.</summary>
    public async Task RunIntervalsAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, ct);
                PrintInterval();
            }
        }
        catch (OperationCanceledException)
        {
            // 정지 — 마지막 구간은 최종 통계에 포함된다.
        }
    }

    public void PrintInterval()
    {
        var now = _stream.Stats.Snapshot();
        var seconds = ElapsedSeconds;
        var dt = Math.Max(1e-6, seconds - _prevSeconds);
        var frames = now.FramesCompleted - _prev.FramesCompleted;
        var bytes = now.BytesReceived - _prev.BytesReceived;
        _out.WriteLine(
            $"[{seconds,8:F1} s] frames {frames} ({frames / dt:F1} fps, {bytes / dt / 1e6:F2} MB/s)  " +
            $"incomplete {now.FramesIncomplete - _prev.FramesIncomplete}  no-buffer {now.FramesDroppedNoBuffer - _prev.FramesDroppedNoBuffer}  " +
            $"packets {now.PacketsReceived - _prev.PacketsReceived}  missing {now.PacketsMissing - _prev.PacketsMissing}  " +
            $"resend req {now.ResendRequests - _prev.ResendRequests} rec {now.ResendRecovered - _prev.ResendRecovered}  " +
            $"errors {now.ErrorPackets - _prev.ErrorPackets}");
        _prev = now;
        _prevSeconds = seconds;
    }

    public void PrintFinal(string stopReason, string packetSizeMode, FrameSaver? saver)
    {
        var s = _stream.Stats.Snapshot();
        var seconds = Math.Max(1e-6, ElapsedSeconds);
        _out.WriteLine();
        _out.WriteLine($"--- final statistics: {seconds:F2} s, {stopReason} ---");
        _out.WriteLine($"  frames        {s.FramesDelivered} delivered, {s.FramesCompleted} completed ({s.FramesCompleted / seconds:F2} fps), last frame id {s.LastFrameId}");
        _out.WriteLine($"  dropped       {s.FramesIncomplete} incomplete, {s.FramesDroppedNoBuffer} no-buffer, {s.FramesDroppedError} error, {s.FramesDroppedUnsupported} unsupported payload");
        _out.WriteLine($"  throughput    {s.BytesReceived / seconds / 1e6:F2} MB/s ({s.BytesReceived} bytes)");
        _out.WriteLine($"  packets       {s.PacketsReceived} received, {s.PacketsMissing} missing, {s.PacketsDuplicated} duplicated, {s.PacketsIgnored} ignored, {s.PacketsUnsupported} unsupported type");
        _out.WriteLine($"  resend        {s.ResendRequests} packets requested, {s.ResendRecovered} recovered, {s.PacketsResent} resent packets received, {s.ErrorPackets} error packets");
        _out.WriteLine($"  channel       packet size {_stream.PacketSize} bytes ({packetSizeMode}), local port {_stream.LocalPort}");
        if (saver is not null)
            _out.WriteLine($"  saved         {saver.SavedFrames} frame(s), {saver.SavedBytes} bytes in {saver.Directory}");
    }
}
