using System.Net;
using System.Net.Sockets;
using GevSharp.Gvcp;
using GevSharp.Sim;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Integration;

/// <summary>
/// 표준 GVCP 포트 3956 은 호스트에 하나뿐이고, 다른 테스트 클래스는 브로드캐스트 DISCOVERY 를 그 포트로 보내며 "루프백에는 응답자가 없다" 고
/// 가정한다. 이 컬렉션은 다른 어떤 컬렉션과도 나란히 돌지 않아, 3956 에 띄운 시뮬레이터가 남의 탐색에 잡히거나 남의 명령을 세는 일이 없다.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StandardPortCollection
{
    public const string Name = "StandardPort";
}

/// <summary>표준 포트(3956)만 겨냥하는 공개 API 경로 — 주소만 받는 ProbeAsync/OpenAsync.</summary>
[Collection(StandardPortCollection.Name)]
public class StandardPortTests
{
    [Fact]
    public async Task PublicApi_ProbeAndOpenOnTheStandardPort_WhenItIsFree()
    {
        // 그 포트를 잡을 수 있을 때만 시뮬레이터를 거기에 띄운다. 일련번호를 달리 해 MAC 이 다른 시뮬레이터(SIM0001)와 겹치지 않게 한다.
        var simOpt = SimRig.DefaultSimOpt();
        simOpt.GvcpPort = GvcpConst.Port;
        simOpt.SerialNumber = "SIM3956";
        using var sim = new SimDevice(simOpt);
        try
        {
            sim.Start();
        }
        catch (SocketException ex)
        {
            Assert.Skip($"UDP port {GvcpConst.Port} on 127.0.0.1 is not available ({ex.SocketErrorCode}); the standard-port path cannot be exercised here.");
        }
        Assert.Equal(GvcpConst.Port, sim.GvcpEndPoint.Port);

        // 잘 알려진 포트에는 남의 브로드캐스트도 닿을 수 있다 — 정확한 횟수가 아니라 프로브가 셈에 더해졌는지만 본다.
        var discoveriesBefore = sim.DiscoveryCount;
        var info = await GevDiscovery.ProbeAsync(IPAddress.Loopback, 1000);
        Assert.NotNull(info);
        Assert.Equal("SimCamera", info!.Model);
        Assert.Equal("SIM3956", info.SerialNumber);
        Assert.Equal(IPAddress.Loopback, info.Address);
        Assert.True(sim.DiscoveryCount >= discoveriesBefore + 1, $"the probe did not reach the simulator (DISCOVERY count {discoveriesBefore} -> {sim.DiscoveryCount})");

        await using (var dev = await GevDevice.OpenAsync(info, SimRig.DefaultDeviceOpt()))
        {
            Assert.True(dev.IsOpen);
            Assert.Equal(IPAddress.Loopback, dev.Address);
            Assert.Equal(GvcpConst.Port, dev.Gvcp.DeviceEndPoint.Port);
            Assert.Equal(dev.Gvcp.LocalEndPoint, sim.ControlOwner);
            Assert.Equal(GvbsAddr.CcpControl, sim.Registers.ReadU32(GvbsAddr.Ccp));
            Assert.Equal(128u, await dev.ReadRegAsync(SimFeatureAddr.Width));
        }
        Assert.Null(sim.ControlOwner);

        // 주소만으로 여는 오버로드도 같은 포트를 쓴다.
        await using var byAddress = await GevDevice.OpenAsync(IPAddress.Loopback, SimRig.DefaultDeviceOpt());
        Assert.True(byAddress.IsOpen);
        Assert.Equal(byAddress.Gvcp.LocalEndPoint, sim.ControlOwner);
        Assert.Equal("SimCamera", byAddress.Info.Model);
        Assert.Equal("SIM3956", byAddress.Info.SerialNumber);
    }
}
