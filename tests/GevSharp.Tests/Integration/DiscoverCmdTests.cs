using GevSharp.Cli.Commands;
using GevSharp.Sim;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Integration;

/// <summary>
/// CLI 명령은 Console.Out 에 바로 쓴다. 출력을 가로채는 동안 다른 테스트가 같은 콘솔에 끼어들지 않도록 이 컬렉션은 홀로 돈다.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCaptureCollection
{
    public const string Name = "ConsoleCapture";
}

/// <summary>
/// 탐색 표는 DISCOVERY_ACK 가 실어 온 식별 필드를 빠짐없이 보여야 한다. 특히 장치 버전(펌웨어)은 그 응답에 이미 들어 있으므로
/// 세션을 열지 않고 보여야 한다 — 표에서 빠지면 "펌웨어를 보려면 장치를 열어야 한다" 는 오해가 생긴다.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public class DiscoverCmdTests
{
    [Fact]
    public async Task Probe_ShowsDeviceVersion_WithoutOpeningASession()
    {
        var simOpt = SimRig.DefaultSimOpt();
        simOpt.DeviceVersion = "3.7.3.0";
        using var sim = SimRig.StartSim(simOpt);

        var cmd = new DiscoverCmd();
        var args = CliArgs.Parse(new[] { "--probe", sim.GvcpEndPoint.ToString(), "--timeout", "3000" }, cmd.Spec);

        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int exit;
        try
        {
            exit = await cmd.RunAsync(args, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(CliExitCode.Ok, exit);
        var lines = captured.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0];
        Assert.StartsWith("IP", header, StringComparison.Ordinal);
        Assert.Contains("Version", header, StringComparison.Ordinal);
        var row = Assert.Single(lines, l => l.Contains(simOpt.SerialNumber, StringComparison.Ordinal));
        Assert.Contains("3.7.3.0", row, StringComparison.Ordinal);

        // 탐색 한 발이 전부다 — 레지스터도 메모리도 읽지 않았고 제어권도 잡지 않았다.
        Assert.Equal(1, sim.DiscoveryCount);
        Assert.Equal(0, sim.ReadRegCount);
        Assert.Equal(0, sim.ReadMemCount);
        Assert.Equal(0, sim.WriteRegCount);
        Assert.Null(sim.ControlOwner);
    }
}
