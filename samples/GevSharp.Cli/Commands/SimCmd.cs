using System.Net;
using GevSharp.Gvcp;
using GevSharp.Pfnc;
using GevSharp.Sim;

namespace GevSharp.Cli.Commands;

/// <summary>
/// 인프로세스 시뮬레이터를 독립 가짜 카메라로 띄운다. 기본은 127.0.0.1:3956 — 표준 포트라 다른 셸의 info/grab/regtest 가 주소만으로 닿는다.
/// 브로드캐스트 탐색은 유니캐스트 주소에 묶인 소켓에 닿지 않으므로 discover 는 --probe 로만 답한다.
/// </summary>
public sealed class SimCmd : ICliCommand
{
    private const int StatusIntervalMs = 2000;

    public string Name => "sim";

    public string Summary => "run the in-process simulator as a standalone fake camera until Ctrl+C";

    public string Usage =>
        "sim [--port 3956] [--bind ip] [--width w] [--height h] [--fps f] [--pixel-format name] [--drop every-n] [--extended-ids]\n" +
        "    [--reserved-holes]\n" +
        "  --port N             GVCP port (default 3956, the standard port; 0 = ephemeral). Other ports need ip:port on the client side\n" +
        "  --bind ip            IPv4 address to bind (default 127.0.0.1)\n" +
        "  --width w            frame width (default 640, 8..4096)\n" +
        "  --height h           frame height (default 480, 8..4096)\n" +
        "  --fps f              free-running frame rate (default 30)\n" +
        "  --pixel-format name  PFNC name or 0x code (default Mono8; the simulator's XML lists Mono8, Mono10, Mono12, Mono16, BayerRG8, RGB8)\n" +
        "  --drop every-n       drop every n-th payload packet of each frame on first transmission (resend recovers it)\n" +
        "  --extended-ids       64-bit block ids / 20-byte GVSP headers from the start\n" +
        "  --reserved-holes     leave bootstrap words 0x0020/0x0040 unimplemented: READREG there fails, and a bulk READMEM across them\n" +
        "                       compacts the data (mimics devices whose bulk read of the identity block is not a byte image)\n" +
        "  Prints the endpoint and register hints, then reports control changes and frame counts until Ctrl+C.";

    public CliOptSpec Spec { get; } = new CliOptSpec()
        .Value("port").Value("bind").Value("width").Value("height").Value("fps").Value("pixel-format").Value("drop").Flag("extended-ids")
        .Flag("reserved-holes");

    public async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        args.RejectExtraPositionals(0);
        var port = args.GetInt("port", GvcpConst.Port, 0, 65535);
        var bindText = args.Get("bind") ?? "127.0.0.1";
        if (!IPAddress.TryParse(bindText, out var bind) || bind.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new CliUsageException($"option --bind expects an IPv4 address, got '{bindText}'");
        var width = args.GetInt("width", 640, 8, 4096);
        var height = args.GetInt("height", 480, 8, 4096);
        var fps = args.GetDouble("fps", 30, 0.001, 100_000);
        var drop = args.GetInt("drop", 0, 0);
        var isExtended = args.Has("extended-ids");
        var pixelText = args.Get("pixel-format") ?? "Mono8";
        if (!PixelFormatInfo.TryParse(pixelText, out var pixelFormat) || pixelFormat == PixelFormat.Unknown)
            throw new CliUsageException($"option --pixel-format expects a PFNC name or 0x code, got '{pixelText}'");

        var opt = new SimDeviceOpt
        {
            GvcpPort = port,
            BindAddress = bind,
            Width = width,
            Height = height,
            FrameRateHz = fps,
            ExtendedIds = isExtended,
            PixelFormat = (uint)pixelFormat,
            HasReservedWordHoles = args.Has("reserved-holes"),
        };
        if (drop > 0)
        {
            var every = (uint)drop;
            opt.DropPacket = (_, packetId) => packetId % every == 0;
        }

        using var sim = new SimDevice(opt);
        sim.Start();
        var endPoint = sim.GvcpEndPoint;
        var address = endPoint.Port == GvcpConst.Port ? endPoint.Address.ToString() : $"{endPoint.Address}:{endPoint.Port}";

        Console.WriteLine($"Simulator running: GVCP {endPoint}, GVSP source port {sim.GvspSourcePort}, MAC {NetText.Mac(new System.Net.NetworkInformation.PhysicalAddress(sim.Mac))}");
        Console.WriteLine($"Identity: {opt.Manufacturer} {opt.Model} [{opt.SerialNumber}], heartbeat timeout {opt.HeartbeatTimeoutMs} ms, tick frequency 1 GHz, XML at 0x{SimRegisterMap.XmlRegionBase:X8} ({sim.Registers.XmlLength} bytes)");
        Console.WriteLine($"Image: {width}x{height} {PixelFormatInfo.Name((uint)pixelFormat)} at {fps} fps, extended ids {(isExtended ? "on" : "off")}, packet loss: {(drop > 0 ? $"every {drop}th payload packet of each frame" : "none")}, default packet size {opt.DefaultPacketSize}");
        if (opt.HasReservedWordHoles)
            Console.WriteLine($"Bootstrap quirk: words {string.Join(", ", SimDevice.ReservedWordHoles.Select(a => $"0x{a:X4}"))} are unimplemented; a bulk READMEM across them compacts the data");
        Console.WriteLine();
        Console.WriteLine("Try from another shell:");
        Console.WriteLine($"  {CliApp.ToolName} info {address}");
        Console.WriteLine($"  {CliApp.ToolName} grab {address} -n 10 --acq-start-addr 0x10030 --acq-stop-addr 0x10034");
        Console.WriteLine($"  {CliApp.ToolName} regtest {address} --count 1000");
        Console.WriteLine($"  {CliApp.ToolName} discover --probe {address}");
        Console.WriteLine("Discovery: broadcast discovery never reaches the simulator (its socket is bound to a unicast address); use the unicast");
        Console.WriteLine("probe above. Without --port the simulator listens on 3956, so plain addresses work; otherwise append :<port>.");
        Console.WriteLine();
        Console.WriteLine("Register hints (docs/sim-register-map.md; 32-bit big-endian, WRITEREG/READREG):");
        Console.WriteLine($"  0x{SimFeatureAddr.Width:X5} Width            0x{SimFeatureAddr.Height:X5} Height           0x{SimFeatureAddr.PixelFormat:X5} PixelFormat (PFNC code)");
        Console.WriteLine($"  0x{SimFeatureAddr.AcquisitionStart:X5} AcquisitionStart (write 1)   0x{SimFeatureAddr.AcquisitionStop:X5} AcquisitionStop (write 1)   0x{SimFeatureAddr.AcquisitionStatus:X5} AcquisitionStatus (RO)");
        Console.WriteLine($"  0x{SimFeatureAddr.AcquisitionFrameRate:X5} AcquisitionFrameRate (float32 Hz)   0x{SimFeatureAddr.AcquisitionMode:X5} AcquisitionMode (0 continuous, 1 single, 2 multi)");
        Console.WriteLine($"  0x{SimFeatureAddr.TestPattern:X5} TestPattern (0 off, 1 diagonal ramp, 2 frame counter)   0x{SimFeatureAddr.FrameCounter:X5} FrameCounter (RO)");
        Console.WriteLine($"  0x{GvbsAddr.Ccp:X5} CCP   0x{GvbsAddr.HeartbeatTimeout:X5} HeartbeatTimeout   0x{GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset):X5} SCP   0x{GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset):X5} SCPS   0x{GvbsAddr.StreamChannel(0, GvbsAddr.ScdaOffset):X5} SCDA");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop.");

        sim.ControlOwnerChanged += owner => Console.WriteLine(owner is null ? "[sim] control released" : $"[sim] control taken by {owner}");

        var lastFrames = 0;
        var lastTimeouts = 0;
        try
        {
            while (true)
            {
                await Task.Delay(StatusIntervalMs, ct);
                var frames = sim.FramesSent;
                var timeouts = sim.HeartbeatTimeouts;
                if (frames != lastFrames || timeouts != lastTimeouts)
                {
                    lastFrames = frames;
                    lastTimeouts = timeouts;
                    Console.WriteLine($"[sim] frames sent {frames}, packets {sim.PacketsSent} (dropped {sim.PacketsDropped}, resent {sim.PacketsResent}), resend requests {sim.ResendRequests.Count}, heartbeat timeouts {timeouts}{(sim.LastError is null ? string.Empty : $", last error: {sim.LastError}")}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl+C — 아래에서 정리한다.
        }

        sim.Stop();
        Console.WriteLine($"Simulator stopped: {sim.FramesSent} frames, {sim.PacketsSent} packets sent ({sim.PacketsDropped} dropped, {sim.PacketsResent} resent, {sim.ResendErrorPackets} resend error packets), " +
                          $"{sim.ReadRegCount} READREG, {sim.WriteRegCount} WRITEREG, {sim.ReadMemCount} READMEM, {sim.WriteMemCount} WRITEMEM, {sim.DiscoveryCount} DISCOVERY, " +
                          $"{sim.HeartbeatObserved} heartbeats, {sim.HeartbeatTimeouts} heartbeat timeouts, {sim.MalformedCount} malformed, {sim.ErrorCount} errors" +
                          (sim.LastError is null ? string.Empty : $"; last error: {sim.LastError}"));
        return CliExitCode.Ok;
    }
}
