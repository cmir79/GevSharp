using GevSharp.Pfnc;

namespace GevSharp.Cli.Commands;

/// <summary>
/// 스트림을 열고 프레임을 받으며 구간·최종 통계를 찍는다. 순서: 장치 열기(제어) → 획득 경로 결정 → 스트림 시작 → AcquisitionStart →
/// 수신 루프(개수·시간·Ctrl+C 로 끝) → AcquisitionStop → 스트림 정지 → 최종 통계.
/// 종료 코드 경계: 스트림·획득을 시작하는 동안의 실패(SCP/SCDA 쓰기 거절, 협상 실패, AcquisitionStart 거절, 제어권 상실)는 프레임이 흐르기 전이라
/// 장치 오류(2)로 올린다. 획득이 시작된 뒤의 실패만 스트림 오류(3)다.
/// </summary>
public sealed class GrabCmd : ICliCommand
{
    private const int StopTimeoutMs = 5000;

    public string Name => "grab";

    public string Summary => "stream frames and report per-interval and final statistics";

    public string Usage =>
        "grab <ip[:port]> [-n count] [-t seconds] [--packet-size auto|N] [--socket-buffer bytes] [--buffers N] [--no-resend]\n" +
        "     [--stats-every seconds] [--save dir] [--packet-delay ticks] [--packet-timeout ms]\n" +
        "     [--initial-packet-timeout ms] [--frame-retention ms]\n" +
        "     [--acq-start-addr hex] [--acq-stop-addr hex]\n" +
        "  -n, --count N          stop after N delivered frames (default: unlimited)\n" +
        "  -t, --seconds S        stop after S seconds (default: unlimited; Ctrl+C stops at any time)\n" +
        "  --packet-size auto|N   SCPS: auto negotiates from the interface MTU with fire-test packets (default); N fixes it (576..16000)\n" +
        "  --socket-buffer bytes  receive socket buffer request, k/m suffix allowed (default 32m); the granted size is logged\n" +
        "  --buffers N            frame buffers in the pool (default 8)\n" +
        "  --no-resend            do not request lost packets (default: resend on)\n" +
        "  --initial-packet-timeout ms  grace a hole gets the first time it is seen, before any resend is asked for\n" +
        "                         (default 2). A packet that is merely out of order usually lands within it, so raising\n" +
        "                         this removes resend requests for packets that were never lost. --packet-timeout does\n" +
        "                         not affect the first request; it is the interval before asking for the same hole again\n" +
        "  --packet-timeout ms    wait before asking again for the same hole, and the silence after which the\n" +
        "                         un-sent tail counts as missing (default 20)\n" +
        "  --frame-retention ms   give up on a frame this long after its last data packet (default 100)\n" +
        "  --stats-every S        interval statistics period in seconds (default 1; 0 disables)\n" +
        "  --save dir             write every delivered frame as <frameId>.bin plus a <frameId>.json sidecar (width, height,\n" +
        "                         stride, pixel format, ...)\n" +
        "  --packet-delay ticks   SCPD inter-packet delay in timestamp ticks (default 0 = no delay; a delay left on the device is cleared)\n" +
        "  --acq-start-addr hex   start acquisition by writing 1 to this register instead of the AcquisitionStart node;\n" +
        "                         required when the GenApi runtime is not available (simulator: 0x10030)\n" +
        "  --acq-stop-addr hex    stop acquisition by writing 1 to this register (simulator: 0x10034)\n" +
        "  Per interval and at the end: frames, fps, MB/s, incomplete frames, no-buffer drops, packets, missing packets,\n" +
        "  resend requests/recovered, error packets, packet size and local port. Exit code 3 for errors after acquisition started;\n" +
        "  failures while opening or starting the stream and acquisition are device errors (2).";

    public CliOptSpec Spec { get; } = new CliOptSpec()
        .Value("count", 'n')
        .Value("seconds", 't')
        .Value("packet-size")
        .Value("socket-buffer")
        .Value("buffers")
        .Flag("no-resend")
        .Value("stats-every")
        .Value("save")
        .Value("packet-delay")
        .Value("initial-packet-timeout")
        .Value("packet-timeout")
        .Value("frame-retention")
        .Value("acq-start-addr")
        .Value("acq-stop-addr");

    public async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var target = DeviceArgs.Target(args);
        args.RejectExtraPositionals(1);
        var count = args.GetLong("count", 0, 0);
        var seconds = args.GetDouble("seconds", 0, 0);
        var statsEvery = args.GetDouble("stats-every", 1.0, 0);
        var streamOpt = BuildStreamOpt(args);
        var packetSizeMode = streamOpt.PacketSizeMode == PacketSizeMode.Auto ? "negotiated" : "fixed";
        var startAddr = args.GetHex("acq-start-addr");
        var stopAddr = args.GetHex("acq-stop-addr");
        if (stopAddr is not null && startAddr is null)
            throw new CliUsageException("--acq-stop-addr requires --acq-start-addr");
        var saveDir = args.Get("save");
        var devOpt = DeviceArgs.BuildOpt(args, GevAccessMode.Control);
        if (devOpt.AccessMode == GevAccessMode.ReadOnly)
            throw new CliUsageException("grab needs control of the device to configure the stream channel; --access readonly cannot stream");

        var saver = saveDir is null ? null : new FrameSaver(saveDir);

        await using var dev = await target.OpenAsync(devOpt, ct);
        var acq = await AcqControl.CreateAsync(dev, startAddr, stopAddr, ct);
        if (acq.PayloadSize is > 0 && streamOpt.PayloadSize is null) streamOpt.PayloadSize = acq.PayloadSize;
        Console.WriteLine($"Device {target}: {dev.Info.Manufacturer} {dev.Info.Model} [{dev.Info.SerialNumber}] via {dev.LocalAddress}; acquisition control: {acq.Description}");

        await using var stream = await dev.OpenStreamAsync(streamOpt, ct);
        var printer = new GrabStatsPrinter(stream, Console.Out);

        // 스트림 시작과 획득 시작은 아직 프레임이 흐르기 전이다 — 여기서 나는 오류는 그대로 올려 장치 오류(2)가 되게 한다.
        // 실패하면 await using 이 스트림을 정지(SCP=0)하고 장치를 닫는다(CCP 해제).
        await stream.StartAsync(ct);
        Console.WriteLine(
            $"Stream: local port {stream.LocalPort}, packet size {stream.PacketSize} bytes ({packetSizeMode}), {streamOpt.BufferCount} buffers, " +
            $"socket buffer {Granted(stream, streamOpt)}, resend {(streamOpt.ResendEnabled ? "on" : "off")}" +
            (streamOpt.InterPacketDelay > 0 ? $", packet delay {streamOpt.InterPacketDelay} ticks" : string.Empty) +
            (streamOpt.PayloadSize is { } ps ? $", payload size {ps} bytes" : string.Empty));
        await acq.StartAsync(ct);

        var stopReason = "cancelled by user";
        var exitCode = CliExitCode.Ok;
        printer.Start();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (seconds > 0) limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            var statsTask = statsEvery > 0 ? printer.RunIntervalsAsync(TimeSpan.FromSeconds(statsEvery), limit.Token) : Task.CompletedTask;

            long delivered = 0;
            try
            {
                while (true)
                {
                    using var frame = await stream.ReceiveAsync(limit.Token);
                    delivered++;
                    if (delivered == 1)
                    {
                        Console.WriteLine(
                            $"First frame: id {frame.FrameId}, {frame.Width}x{frame.Height} {PixelFormatInfo.Name(frame.PixelFormatCode)} " +
                            $"(0x{frame.PixelFormatCode:X8}), stride {frame.Stride}, payload {frame.PayloadSize} bytes, {frame.ExpectedPackets} packets" +
                            (frame.HasChunkData ? ", chunk data appended" : string.Empty));
                    }
                    saver?.Save(frame);
                    if (count > 0 && delivered >= count)
                    {
                        stopReason = $"frame count {count} reached";
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                stopReason = ct.IsCancellationRequested ? "cancelled by user" : $"time limit {seconds} s reached";
            }
            limit.Cancel();
            await statsTask;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stopReason = "cancelled by user";
        }
        catch (Exception ex)
        {
            // 획득이 시작된 뒤의 실패 — 스트림 오류(3). 최종 통계는 그래도 찍는다.
            Console.Error.WriteLine($"stream error: {ex.Message}");
            if (GevLog.MinLevel <= GevLogLevel.Debug) Console.Error.WriteLine(ex.ToString());
            stopReason = "stopped by error";
            exitCode = CliExitCode.Stream;
        }
        finally
        {
            // 정리는 취소 토큰과 무관하게 끝까지 간다 — 장치가 닫힌 포트로 계속 쏘지 않게, 제어권이 풀리게.
            await StopAcquisitionAsync(acq);
            await StopStreamAsync(stream);
            printer.PrintFinal(stopReason, packetSizeMode, saver);
        }
        return exitCode;
    }

    private static GevStreamOpt BuildStreamOpt(CliArgs args)
    {
        var opt = new GevStreamOpt();
        var packetSize = args.Get("packet-size");
        if (packetSize is not null && !packetSize.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var size = CliArgs.ParseInt(packetSize, "--packet-size");
            if (size < 576 || size > 16000)
                throw new CliUsageException($"option --packet-size must be 'auto' or 576..16000, got {size}");
            opt.PacketSizeMode = PacketSizeMode.Fixed;
            opt.PacketSize = size;
        }
        opt.SocketBufferBytes = (int)args.GetBytes("socket-buffer", opt.SocketBufferBytes, 0, int.MaxValue);
        opt.BufferCount = args.GetInt("buffers", opt.BufferCount, 1, 65536);
        opt.ResendEnabled = !args.Has("no-resend");
        opt.InterPacketDelay = args.GetInt("packet-delay", 0, 0);
        // 느린 호스트나 긴 링크에서는 기본값(20/100 ms)이 짧아 멀쩡한 프레임을 포기한다 — 현장 진단을 위해 열어 둔다.
        opt.InitialPacketTimeoutMs = args.GetInt("initial-packet-timeout", opt.InitialPacketTimeoutMs, 0);
        opt.PacketTimeoutMs = args.GetInt("packet-timeout", opt.PacketTimeoutMs, 1);
        opt.FrameRetentionMs = args.GetInt("frame-retention", opt.FrameRetentionMs, 1);
        return opt;
    }

    private static async Task StopAcquisitionAsync(AcqControl acq)
    {
        using var cts = new CancellationTokenSource(StopTimeoutMs);
        try
        {
            await acq.StopAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: acquisition stop failed: {ex.Message}");
        }
    }

    private static async Task StopStreamAsync(GevStream stream)
    {
        try
        {
            await stream.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: stream stop failed: {ex.Message}");
        }
    }
    /// <summary>
    /// 실제로 받은 수신 버퍼 크기. 요청값만 적으면 덜 받은 것을 알 수 없고, 그것은 부하가 걸릴 때
    /// 유실로만 나타나 원인을 찾기 어렵다. 덜 받았으면 요청값도 함께 적는다.
    /// </summary>
    private static string Granted(GevStream stream, GevStreamOpt opt)
        => stream.SocketReceiveBufferBytes >= opt.SocketBufferBytes
            ? $"{stream.SocketReceiveBufferBytes} bytes"
            : $"{stream.SocketReceiveBufferBytes} bytes granted of {opt.SocketBufferBytes} requested";
}
