using System.Net;
using GevSharp.Cli.Commands;
using GevSharp.Sim;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Cli;

/// <summary>
/// <see cref="CliApp.RunAsync"/> 는 콘솔과 <see cref="GevLog.Sink"/> 라는 프로세스 전역을 바꾼다. 이 컬렉션은 다른 어떤 컬렉션과도
/// 나란히 돌지 않아 다른 테스트의 로그·출력이 섞이지 않고, 각 실행이 끝나면 원래 값으로 되돌린다.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliConsoleCollection
{
    public const string Name = "GevSharp.Cli console";
}

/// <summary>명령을 인프로세스로 실행해 종료 코드와 출력을 본다. 장치는 루프백의 시뮬레이터(임시 포트 → ip:port 접미사 경로).</summary>
[Collection(CliConsoleCollection.Name)]
public class CliRunTests
{
    private const string AcqFallback = "--acq-start-addr 0x10030 --acq-stop-addr 0x10034";

    private static async Task<(int Code, string Out, string Err)> RunAsync(string commandLine)
    {
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        var prevSink = GevLog.Sink;
        var prevLevel = GevLog.MinLevel;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = await CliApp.RunAsync(commandLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), CancellationToken.None);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
            GevLog.Sink = prevSink;
            GevLog.MinLevel = prevLevel;
        }
    }

    private static SimDevice StartSim(Action<SimDeviceOpt>? configure = null)
    {
        var opt = new SimDeviceOpt { BindAddress = IPAddress.Loopback, GvcpPort = 0, Width = 64, Height = 32, FrameRateHz = 200 };
        configure?.Invoke(opt);
        var sim = new SimDevice(opt);
        sim.Start();
        return sim;
    }

    private static string Target(SimDevice sim) => $"127.0.0.1:{sim.GvcpEndPoint.Port}";

    [Fact]
    public async Task NoArgumentsPrintsUsageWithExitCode1()
    {
        var (code, stdout, stderr) = await RunAsync(string.Empty);

        Assert.Equal(CliExitCode.Usage, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("usage:", stderr);
    }

    [Fact]
    public async Task HelpAndVersionExitZero()
    {
        var help = await RunAsync("--help");
        Assert.Equal(CliExitCode.Ok, help.Code);
        Assert.Contains("commands:", help.Out);

        var commandHelp = await RunAsync("regtest --help");
        Assert.Equal(CliExitCode.Ok, commandHelp.Code);
        Assert.StartsWith($"usage: {CliApp.ToolName} regtest", commandHelp.Out, StringComparison.Ordinal);

        var version = await RunAsync("--version");
        Assert.Equal(CliExitCode.Ok, version.Code);
        Assert.StartsWith(CliApp.ToolName + " ", version.Out, StringComparison.Ordinal);
        Assert.Contains("GevSharp", version.Out);
    }

    [Fact]
    public async Task UsageErrorsExit1AndShowTheCommandUsage()
    {
        var unknown = await RunAsync("frobnicate");
        Assert.Equal(CliExitCode.Usage, unknown.Code);
        Assert.Contains("unknown command 'frobnicate'", unknown.Err);

        var badOption = await RunAsync("grab 10.0.0.1 --bogus");
        Assert.Equal(CliExitCode.Usage, badOption.Code);
        Assert.Contains("unknown option '--bogus'", badOption.Err);
        Assert.Contains($"usage: {CliApp.ToolName} grab", badOption.Err);

        var badAddress = await RunAsync("info 10.0.0.1:99999");
        Assert.Equal(CliExitCode.Usage, badAddress.Code);

        var missingNode = await RunAsync("get 10.0.0.1");
        Assert.Equal(CliExitCode.Usage, missingNode.Code);
        Assert.Contains("<node>", missingNode.Err);
    }

    [Fact]
    public async Task GlobalValuedOptionBeforeTheCommandIsNotTakenAsTheCommandName()
    {
        using var sim = StartSim();

        // 사용법 텍스트가 전역 옵션에 자리 제한을 두지 않으므로 명령 앞의 --access 도 값 토큰까지 건너뛰고 명령을 찾아야 한다.
        var separate = await RunAsync($"--access readonly info {Target(sim)}");
        Assert.Equal(CliExitCode.Ok, separate.Code);
        Assert.Contains("(read-only session)", separate.Out);

        var inline = await RunAsync($"--access=readonly info {Target(sim)}");
        Assert.Equal(CliExitCode.Ok, inline.Code);
        Assert.Contains("(read-only session)", inline.Out);

        var control = await RunAsync($"--verbose --access control info {Target(sim)}");
        Assert.Equal(CliExitCode.Ok, control.Code);
        Assert.Contains("(control session)", control.Out);
        Assert.Contains("DEBUG", control.Err);
        Assert.Null(sim.ControlOwner);   // 세션이 끝나며 CCP 를 풀었다

        var badValue = await RunAsync($"--access nope info {Target(sim)}");
        Assert.Equal(CliExitCode.Usage, badValue.Code);
        Assert.Contains("--access expects", badValue.Err);

        var missingValue = await RunAsync("--access");
        Assert.Equal(CliExitCode.Usage, missingValue.Code);
        Assert.Contains("requires a value", missingValue.Err);
    }

    [Fact]
    public async Task InfoPrintsBootstrapAndXmlUrlOfTheSimulator()
    {
        using var sim = StartSim();

        var (code, stdout, _) = await RunAsync($"info {Target(sim)}");

        Assert.Equal(CliExitCode.Ok, code);
        Assert.Contains("GevSharp", stdout);
        Assert.Contains("SimCamera", stdout);
        Assert.Contains("SIM0001", stdout);
        Assert.Contains("Heartbeat timeout      3000 ms", stdout);
        Assert.Contains("1000000000 Hz", stdout);
        Assert.Contains("packet-resend", stdout);
        Assert.Contains("Local:SimCamera.xml;100000;", stdout);
        Assert.Contains("read-only session", stdout);
        Assert.Null(sim.ControlOwner);   // info 는 CCP 를 건드리지 않는다
    }

    [Fact]
    public async Task ProbeListsTheSimulatorInTheDiscoveryTable()
    {
        using var sim = StartSim();

        var (code, stdout, _) = await RunAsync($"discover --probe {Target(sim)}");

        Assert.Equal(CliExitCode.Ok, code);
        Assert.Contains("IP", stdout);
        Assert.Contains("127.0.0.1", stdout);
        Assert.Contains("SimCamera", stdout);
        Assert.Contains("1 device(s)", stdout);
        Assert.Equal(1, sim.DiscoveryCount);
    }

    [Fact]
    public async Task ProbeWithoutAnAnswerExits2()
    {
        // 아무도 듣지 않는 루프백 포트 — 응답 없음은 장치 오류(2)다.
        var (code, _, stderr) = await RunAsync("discover --probe 127.0.0.1:1 --timeout 200");

        Assert.Equal(CliExitCode.Device, code);
        Assert.Contains("no reply", stderr);
    }

    [Fact]
    public async Task RegTestReadsBothRegistersWithoutMismatch()
    {
        using var sim = StartSim();

        var (code, stdout, _) = await RunAsync($"regtest {Target(sim)} --count 300");

        Assert.Equal(CliExitCode.Ok, code);
        Assert.Contains("300 alternating reads of 0x0000 (= 0x00020000) and 0x0004 (= 0x80000001)", stdout);
        Assert.Contains("reads         300 of 300", stdout);
        Assert.Contains("result        0 mismatch(es), 0 error(s)", stdout);
        Assert.Contains("latency       min", stdout);
        Assert.Contains("control held", stdout);
        Assert.True(sim.ReadRegCount >= 302);
        Assert.Null(sim.ControlOwner);   // DisposeAsync 가 CCP 를 풀었다
    }

    [Fact]
    public async Task RegTestReportsMismatchesAndExits2()
    {
        using var sim = StartSim();
        // A = 0x0950 (DiscoveryAckDelay, RW, 0) 은 명령이 기준값을 읽은 뒤 장치 쪽에서 바꾼다. 시점은 시간이 아니라 시뮬레이터가 센 READREG 수로
        // 잡는다 — 열기와 기준 읽기가 끝나고 루프가 돌기 시작한 뒤라야 하고, 남은 읽기가 충분해야 한다(총 2000 회 중 100 회 시점).
        var target = Target(sim);
        var change = Task.Run(async () =>
        {
            while (sim.ReadRegCount < 100) await Task.Delay(1);
            sim.Registers.WriteU32(GevSharp.Gvcp.GvbsAddr.DiscoveryAckDelay, 0x1234);
        });

        var (code, stdout, _) = await RunAsync($"regtest {target} --count 2000 --addr-a 0x0950 --addr-b 0x0004");
        await change;

        Assert.Equal(CliExitCode.Device, code);
        Assert.Contains("mismatch(es)", stdout);
        Assert.DoesNotContain("result        0 mismatch(es)", stdout);
    }

    [Fact]
    public async Task GrabWithRegisterFallbackDeliversFramesAndPrintsFinalStatistics()
    {
        using var sim = StartSim();

        var (code, stdout, stderr) = await RunAsync($"grab {Target(sim)} -n 4 --stats-every 0 --packet-size 1500 {AcqFallback}");

        Assert.Equal(CliExitCode.Ok, code);
        Assert.Contains("acquisition control: register writes (start 0x10030 <- 1, stop 0x10034)", stdout);
        Assert.Contains("First frame: id 1, 64x32 Mono8 (0x01080001), stride 64, payload 2048 bytes", stdout);
        Assert.Contains("frame count 4 reached", stdout);
        Assert.Contains("frames        4 delivered", stdout);
        Assert.Contains("packet size 1500 bytes (fixed)", stdout);
        Assert.Contains("resend        0 packets requested, 0 recovered", stdout);
        Assert.DoesNotContain("stream error", stderr);
        Assert.False(sim.IsAcquiring);
        Assert.Null(sim.ControlOwner);
    }

    [Fact]
    public async Task GrabRecoversInjectedLossThroughResend()
    {
        // 320×120 Mono8 은 페이로드 패킷 27 개짜리 프레임이다. 매 다섯째 패킷(5·10·15·20·25)을 버려
        // "구멍 → 리센드 요청 → 재전송(0x0100) → 완성" 을 CLI 경로 전체로 통과시킨다 — 프레임 한가운데의 구멍(id 건너뜀으로 찾는다),
        // 한 프레임 안의 여러 구멍(5 개), 그리고 요청 예산(ceil(27 × 0.25) = 7)이 실제로 걸리는 손실률까지 한 번에 지난다.
        // 예산을 요청 횟수로 세는 회귀가 생기면 두 번째 라운드에서 5 + 5 > 7 로 프레임이 버려져 아래 "dropped 0 incomplete" 가 걸린다.
        // 프레임을 더 키우지 않는 이유는 리센드 왕복이 보존 시간(100 ms, CLI 가 라이브러리 기본값을 그대로 쓴다) 안에 끝나야 하기 때문이다 —
        // 구멍이 프레임당 수십 개면 밀린 러너에서 장치가 그 안에 다 답하지 못해, 라이브러리가 아니라 러너의 스케줄링을 재는 시험이 된다.
        using var sim = StartSim(o =>
        {
            o.Width = 320;
            o.Height = 120;
            o.DropPacket = (_, packetId) => packetId % 5 == 0;
        });
        // 장치가 정확히 3 프레임만 보내고 프레임 경계에서 멈추게 한다. 자유 실행으로 두면 -n 3 을 채운 뒤 스트림을 내리는 동안
        // 다음 프레임이 중간까지 와 있다가 끊기고, 그 조각이 "불완전 프레임" 으로 집계된다 — 리센드 복구와는 무관한 꼬리다.
        sim.Registers.WriteU32(SimFeatureAddr.AcquisitionMode, SimFeatureAddr.AcquisitionModeMultiFrame);
        sim.Registers.WriteU32(SimFeatureAddr.AcquisitionFrameCount, 3);

        // -t 는 안전망이다: 복구가 깨져 세 번째 프레임이 영영 안 오면 -n 3 만으로는 명령이 끝나지 않는다.
        var (code, stdout, _) = await RunAsync($"grab {Target(sim)} -n 3 -t 10 --stats-every 0 --packet-size 1500 {AcqFallback}");

        Assert.Equal(CliExitCode.Ok, code);
        // 이 세 가지는 실패했을 때 명령이 스스로 찍은 통계(전달·포기 프레임 수, 리센드 요청·복구 수)가 그대로 보여야 원인을 가릴 수 있다.
        Assert.True(stdout.Contains("frame count 3 reached"), stdout);
        Assert.True(stdout.Contains("frames        3 delivered"), stdout);
        // 장치가 보낸 세 프레임은 버려진 패킷을 리센드로 전부 메워 나와야 한다 — 하나라도 포기됐으면 여기서 걸린다.
        Assert.True(stdout.Contains("dropped       0 incomplete"), stdout);
        Assert.True(sim.PacketsDropped >= 15, $"the device dropped {sim.PacketsDropped} packets for 3 frames with 5 holes each");
        Assert.True(sim.PacketsResent >= 15, $"the device resent {sim.PacketsResent} packets for {sim.PacketsDropped} injected holes");
        Assert.Matches(@"resend\s+[1-9]\d* packets requested, [1-9]\d* recovered", stdout);
    }

    [Fact]
    public async Task GrabSavesRawFramesWithJsonSidecars()
    {
        using var sim = StartSim();
        var dir = Path.Combine(Path.GetTempPath(), "gevsharp-cli-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (code, stdout, _) = await RunAsync($"grab {Target(sim)} -n 2 --stats-every 0 --packet-size 1500 --save {dir} {AcqFallback}");

            Assert.Equal(CliExitCode.Ok, code);
            Assert.Contains("saved         2 frame(s), 4096 bytes", stdout);
            var bin = Path.Combine(dir, "1.bin");
            var json = Path.Combine(dir, "1.json");
            Assert.True(File.Exists(bin));
            Assert.True(File.Exists(json));
            Assert.Equal(SimDevice.BuildPatternFrame(64, 32, 0x01080001, 1), File.ReadAllBytes(bin));
            var sidecar = File.ReadAllText(json);
            Assert.Contains("\"width\": 64", sidecar);
            Assert.Contains("\"height\": 32", sidecar);
            Assert.Contains("\"stride\": 64", sidecar);
            Assert.Contains("\"pixelFormat\": \"Mono8\"", sidecar);
            Assert.Contains("\"dataFile\": \"1.bin\"", sidecar);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GrabDeviceErrorBeforeFramesFlowIsADeviceErrorNotAStreamError()
    {
        using var sim = StartSim();

        // 0x0000(Version) 은 쓰기 보호 레지스터 — AcquisitionStart 대체 쓰기가 WRITE_PROTECT 로 거절된다. 프레임이 흐르기 전이므로 종료 코드 2.
        var (code, stdout, stderr) = await RunAsync($"grab {Target(sim)} -n 1 --stats-every 0 --packet-size 1500 --acq-start-addr 0x0000");

        Assert.Equal(CliExitCode.Device, code);
        Assert.Contains("error: ", stderr);
        Assert.Contains("WRITE_PROTECT", stderr);
        Assert.DoesNotContain("stream error", stderr);
        Assert.Contains("Stream: local port", stdout);       // 스트림은 열렸고
        Assert.DoesNotContain("final statistics", stdout);   // 통계는 찍지 않는다
        Assert.False(sim.IsAcquiring);
        Assert.Null(sim.ControlOwner);                       // 스트림 정지·CCP 해제까지 마쳤다
        Assert.Equal(0u, sim.Registers.ReadU32(GevSharp.Gvcp.GvbsAddr.StreamChannel(0, GevSharp.Gvcp.GvbsAddr.ScpOffset)));
    }

    [Fact]
    public async Task GrabStartsAcquisitionThroughTheNodeMapWithoutAFallbackAddress()
    {
        using var sim = StartSim();

        // 대체 레지스터 주소를 주지 않아도 노드맵의 AcquisitionStart/Stop 커맨드로 획득이 돈다.
        var (code, stdout, stderr) = await RunAsync($"grab {Target(sim)} -n 2 --stats-every 0");

        Assert.Equal(CliExitCode.Ok, code);
        Assert.Contains("acquisition control: GenApi AcquisitionStart / AcquisitionStop", stdout);
        Assert.Contains("final statistics", stdout);
        // 전달 수만 -n 으로 정해진다. 자유 실행 장치는 정지 명령이 닿기 전에 프레임을 더 완성할 수 있어
        // completed 는 delivered 이상이면 된다(실장치에서도 5 전달 / 6 완성이 나온다).
        Assert.Contains("2 delivered,", stdout);
        Assert.Matches(@"frames\s+2 delivered, (\d+) completed", stdout);
        Assert.DoesNotContain("--acq-start-addr", stderr);   // 대체 경로를 안내할 이유가 없다
        Assert.False(sim.IsAcquiring);                        // AcquisitionStop 까지 갔다
        Assert.Null(sim.ControlOwner);                        // CCP 해제까지 마쳤다
    }

    [Fact]
    public async Task FeatureCommandsReadAndWriteThroughTheNodeMap()
    {
        using var sim = StartSim();

        var (getCode, getOut, _) = await RunAsync($"get {Target(sim)} Width");
        Assert.Equal(CliExitCode.Ok, getCode);
        Assert.Equal("64", getOut.Trim());                    // StartSim 이 잡은 폭

        var (setCode, setOut, _) = await RunAsync($"set {Target(sim)} Width 320");
        Assert.Equal(CliExitCode.Ok, setCode);
        Assert.Contains("Width = 320", setOut);
        Assert.Equal(320u, sim.Registers.ReadU32(SimFeatureAddr.Width));

        var (featCode, featOut, featErr) = await RunAsync($"features {Target(sim)}");
        Assert.Equal(CliExitCode.Ok, featCode);
        Assert.Contains("Root [Category]", featOut);
        Assert.Contains("Width [Integer] RW = 320", featOut);
        Assert.Contains("PixelFormat [Enumeration] RW = Mono8", featOut);
        Assert.Contains("DeviceModelName [String] RO = \"SimCamera\"", featOut);
        Assert.DoesNotContain("unexpected error", featErr);

        Assert.Null(sim.ControlOwner);
    }
}
