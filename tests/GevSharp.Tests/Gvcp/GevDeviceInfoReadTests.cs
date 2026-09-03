using System.Net;
using System.Net.NetworkInformation;
using GevSharp.Gvcp;
using GevSharp.Sim;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Gvcp;

/// <summary>
/// 열린 장치에서 식별 블록을 읽는 경로. 블록 전체를 READMEM 한 번으로 읽으면 예약 워드를 구현하지 않은 장치에서 0x0024 이후 필드가
/// 밀려 보인다 — 필드별 읽기가 그런 장치에서도 맞는지, 요청이 정확히 어떤 주소로 나가는지, 선택 레지스터 거절을 어떻게 다루는지.
/// </summary>
public class GevDeviceInfoReadTests
{
    private static GvcpChannel Open(GvcpTestResponder r)
        => new(r.EndPoint, IPAddress.Loopback, new GvcpChannelOpt { TimeoutMs = 300, Retries = 1 });

    private static GevDeviceInfo ParseBulk(GvcpAck ack) => GevDeviceInfo.ParseDiscoveryAck(ack.MemData, IPAddress.Loopback);

    [Fact]
    public async Task EveryFieldIsReadAtItsOwnAddress()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r);

        var info = await GevDeviceInfo.ReadFromDeviceAsync(ch, IPAddress.Loopback);

        Assert.Equal(1, info.SpecMajor);
        Assert.Equal(2, info.SpecMinor);
        Assert.Equal(0x8000_0001u, info.DeviceMode);
        Assert.Equal(PhysicalAddress.Parse("00-11-22-33-44-55"), info.Mac);
        Assert.Equal(0x8000_0007u, info.SupportedIpCfg);
        Assert.Equal(1u, info.CurrentIpCfg);
        Assert.Equal(IPAddress.Loopback, info.Address);
        Assert.Equal(IPAddress.Parse("255.0.0.0"), info.Subnet);
        Assert.Equal(IPAddress.Any, info.Gateway);
        Assert.Equal("GevSharp Test", info.Manufacturer);
        Assert.Equal("Responder", info.Model);
        Assert.Equal("1.0", info.DeviceVersion);
        Assert.Equal("loopback", info.ManufacturerInfo);
        Assert.Equal("SN0001", info.SerialNumber);

        var regs = r.Requests.Where(q => q.Command == GvcpConst.ReadRegCmd).Select(q => Assert.Single(q.Addresses)).ToArray();
        Assert.Equal(new[]
        {
            GvbsAddr.Version, GvbsAddr.DeviceMode, GvbsAddr.MacHigh, GvbsAddr.MacLow, GvbsAddr.SupportedIpCfg, GvbsAddr.CurrentIpCfg,
            GvbsAddr.CurrentIp, GvbsAddr.CurrentSubnet, GvbsAddr.CurrentGateway,
        }, regs);

        var mems = r.Requests.Where(q => q.Command == GvcpConst.ReadMemCmd).Select(q =>
        {
            GvcpPacket.ReadMemFields(q.Payload, out var addr, out var count);
            return (addr, count);
        }).ToArray();
        Assert.Equal(new[]
        {
            (GvbsAddr.ManufacturerName, GvbsAddr.ManufacturerNameLen), (GvbsAddr.ModelName, GvbsAddr.ModelNameLen),
            (GvbsAddr.DeviceVersion, GvbsAddr.DeviceVersionLen), (GvbsAddr.ManufacturerInfo, GvbsAddr.ManufacturerInfoLen),
            (GvbsAddr.SerialNumber, GvbsAddr.SerialNumberLen), (GvbsAddr.UserDefinedName, GvbsAddr.UserDefinedNameLen),
        }, mems);
        Assert.Equal(15, r.Requests.Count);
    }

    [Theory]
    [InlineData(GvbsAddr.SerialNumber)]
    [InlineData(GvbsAddr.UserDefinedName)]
    public async Task OptionalStringRegisterTheDeviceRefusesIsLeftEmpty(uint refused)
    {
        using var r = new GvcpTestResponder();
        r.ErrorAddr = refused;
        r.ErrorStatus = GvcpConst.StatusInvalidAddress;
        using var ch = Open(r);

        var info = await GevDeviceInfo.ReadFromDeviceAsync(ch, IPAddress.Loopback);

        Assert.Equal("Responder", info.Model);
        Assert.Equal(refused == GvbsAddr.SerialNumber ? string.Empty : "SN0001", info.SerialNumber);
        Assert.Equal(refused == GvbsAddr.UserDefinedName ? string.Empty : "unit", info.UserDefinedName);
        Assert.Equal(6, r.CountOf(GvcpConst.ReadMemCmd));
    }

    [Theory]
    [InlineData(GvbsAddr.CurrentIp)]
    [InlineData(GvbsAddr.ModelName)]
    public async Task MandatoryFieldTheDeviceRefusesFailsTheRead(uint refused)
    {
        using var r = new GvcpTestResponder();
        r.ErrorAddr = refused;
        r.ErrorStatus = GvcpConst.StatusInvalidAddress;
        using var ch = Open(r);

        var ex = await Assert.ThrowsAsync<GevStatusException>(() => GevDeviceInfo.ReadFromDeviceAsync(ch, IPAddress.Loopback));

        Assert.Equal(GvcpConst.StatusInvalidAddress, ex.Status);
    }

    [Fact]
    public async Task OpenIsNotFooledByADeviceWhoseBulkReadSkipsUnimplementedWords()
    {
        using var sim = new SimDevice(new SimDeviceOpt { GvcpPort = 0, HasReservedWordHoles = true, UserDefinedName = "bench" });
        sim.Start();

        // 시뮬레이터가 문제의 장치 동작을 정말 재현하는지부터 — 벌크 READMEM 을 그대로 해석하면 0x0024 이후가 밀린다.
        using (var probe = new GvcpChannel(sim.GvcpEndPoint, IPAddress.Loopback, new GvcpChannelOpt { TimeoutMs = 500, Retries = 1 }))
        {
            var bulk = await probe.RequestAsync(GvcpCmd.ReadMem(0, GvbsAddr.DiscoveryDataLen));
            var shifted = ParseBulk(bulk);
            Assert.NotEqual("SimCamera", shifted.Model);
            Assert.NotEqual(IPAddress.Loopback, shifted.Address);
            Assert.Equal(new PhysicalAddress(sim.Mac), shifted.Mac);   // 홀 앞의 필드는 멀쩡하다 — 그래서 눈에 잘 안 띈다
        }

        await using var dev = await GevDevice.OpenAsync(sim.GvcpEndPoint, new GevDeviceOpt { AccessMode = GevAccessMode.ReadOnly, GvcpTimeoutMs = 500, GvcpRetries = 1 });
        var info = dev.Info;

        Assert.Equal("GevSharp", info.Manufacturer);
        Assert.Equal("SimCamera", info.Model);
        Assert.Equal("1.0", info.DeviceVersion);
        Assert.Equal("in-process simulator", info.ManufacturerInfo);
        Assert.Equal("SIM0001", info.SerialNumber);
        Assert.Equal("bench", info.UserDefinedName);
        Assert.Equal(new PhysicalAddress(sim.Mac), info.Mac);
        Assert.Equal(IPAddress.Loopback, info.Address);
        Assert.Equal(IPAddress.Parse("255.0.0.0"), info.Subnet);
        Assert.Equal(IPAddress.Any, info.Gateway);
        Assert.Equal(2, info.SpecMajor);
        Assert.Equal(0, info.SpecMinor);
    }
}
