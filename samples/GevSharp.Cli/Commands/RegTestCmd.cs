using System.Diagnostics;
using GevSharp.Gvcp;

namespace GevSharp.Cli.Commands;

/// <summary>
/// 두 레지스터를 번갈아 읽으며 응답이 요청과 맞는지(ID 상관) 확인한다. 하트비트가 같은 채널을 함께 쓰는 동안 돌리는 것이 요점이다.
/// 각 읽기의 왕복 시간을 재어 최소·평균·최대를 보고한다. 기본 대상은 값이 변하지 않는 GVBS Version 과 DeviceMode.
/// </summary>
public sealed class RegTestCmd : ICliCommand
{
    private const int MaxReportedFailures = 10;

    public string Name => "regtest";

    public string Summary => "alternating reads of two registers with mismatch and latency report";

    public string Usage =>
        "regtest <ip[:port]> [--count N] [--addr-a hex] [--addr-b hex]\n" +
        "  --count N      number of reads (default 10000)\n" +
        "  --addr-a hex   first register (default 0x0000, GVBS Version)\n" +
        "  --addr-b hex   second register (default 0x0004, GVBS DeviceMode)\n" +
        "  Reads both registers once as the reference, then alternates A, B, A, B ... while the heartbeat runs on the same\n" +
        "  channel. Pick registers whose value does not change. Reports mismatches, read errors, latency min/avg/max and the\n" +
        "  channel's stale/foreign/malformed packet counters. Exit code 2 when any read mismatched or failed.";

    public CliOptSpec Spec { get; } = new CliOptSpec().Value("count").Value("addr-a").Value("addr-b");

    public async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var target = DeviceArgs.Target(args);
        args.RejectExtraPositionals(1);
        var count = args.GetInt("count", 10_000, 1);
        var addrA = args.GetHex("addr-a") ?? GvbsAddr.Version;
        var addrB = args.GetHex("addr-b") ?? GvbsAddr.DeviceMode;
        var opt = DeviceArgs.BuildOpt(args, GevAccessMode.Control);

        await using var dev = await target.OpenAsync(opt, ct);
        var refA = await dev.ReadRegAsync(addrA, ct);
        var refB = await dev.ReadRegAsync(addrB, ct);
        var heartbeat = dev.HeartbeatPeriodMs > 0
            ? $"heartbeat every {dev.HeartbeatPeriodMs} ms (device timeout {dev.DeviceHeartbeatTimeoutMs} ms)"
            : "no heartbeat (read-only session)";
        Console.WriteLine($"regtest {target}: {count} alternating reads of 0x{addrA:X4} (= 0x{refA:X8}) and 0x{addrB:X4} (= 0x{refB:X8}); {heartbeat}");

        var latency = new LatencyStats();
        var mismatches = 0;
        var errors = 0;
        var done = 0;
        var progressStep = Math.Max(1, count / 10);
        var wasCancelled = false;
        var clock = Stopwatch.StartNew();

        try
        {
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var isA = (i & 1) == 0;
                var addr = isA ? addrA : addrB;
                var expected = isA ? refA : refB;

                var t0 = Stopwatch.GetTimestamp();
                uint value;
                try
                {
                    value = await dev.ReadRegAsync(addr, ct);
                }
                catch (GevException ex)
                {
                    errors++;
                    if (errors <= MaxReportedFailures) Console.WriteLine($"  read {i}: 0x{addr:X4} failed: {ex.Message}");
                    done++;
                    continue;
                }
                latency.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                done++;

                if (value != expected)
                {
                    mismatches++;
                    if (mismatches <= MaxReportedFailures)
                        Console.WriteLine($"  read {i}: 0x{addr:X4} returned 0x{value:X8}, expected 0x{expected:X8}");
                }
                if ((i + 1) % progressStep == 0 && i + 1 < count)
                    Console.WriteLine($"  {i + 1}/{count} reads, {mismatches} mismatch(es), {errors} error(s), avg {latency.AvgMs:F3} ms");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            wasCancelled = true;
        }
        clock.Stop();

        var seconds = Math.Max(1e-6, clock.Elapsed.TotalSeconds);
        Console.WriteLine();
        Console.WriteLine($"reads         {done} of {count}{(wasCancelled ? " (cancelled)" : string.Empty)} in {seconds:F2} s ({done / seconds:F0} reads/s)");
        Console.WriteLine($"result        {mismatches} mismatch(es), {errors} error(s)");
        Console.WriteLine(latency.Count == 0
            ? "latency       (no successful read)"
            : $"latency       min {latency.MinMs:F3} ms, avg {latency.AvgMs:F3} ms, max {latency.MaxMs:F3} ms");
        Console.WriteLine($"channel       stale acks {dev.Gvcp.StaleAckCount}, foreign packets {dev.Gvcp.ForeignPacketCount}, malformed {dev.Gvcp.MalformedPacketCount}, pending acks {dev.Gvcp.PendingAckCount}; control {(dev.IsOpen ? "held" : "LOST")}");

        return mismatches == 0 && errors == 0 && dev.IsOpen ? CliExitCode.Ok : CliExitCode.Device;
    }

    private sealed class LatencyStats
    {
        private double _sum;

        public int Count { get; private set; }
        public double MinMs { get; private set; } = double.PositiveInfinity;
        public double MaxMs { get; private set; }
        public double AvgMs => Count == 0 ? 0 : _sum / Count;

        public void Add(double ms)
        {
            Count++;
            _sum += ms;
            if (ms < MinMs) MinMs = ms;
            if (ms > MaxMs) MaxMs = ms;
        }
    }
}
