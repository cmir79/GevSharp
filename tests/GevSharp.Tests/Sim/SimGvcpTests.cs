using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GevSharp.Gvcp;
using GevSharp.Sim;

namespace GevSharp.Tests.Simulator;

/// <summary>시뮬레이터의 GVCP 응답기를 원시 UDP 패킷으로 검증한다 — 디스커버리, 레지스터/메모리 접근, CCP, 하트비트, PENDING_ACK, XML.</summary>
public class SimGvcpTests
{
    private static SimDevice StartDevice(Action<SimDeviceOpt>? configure = null)
    {
        var opt = new SimDeviceOpt();
        configure?.Invoke(opt);
        var dev = new SimDevice(opt);
        dev.Start();
        return dev;
    }

    // ---- DISCOVERY ----

    [Fact]
    public void Discovery_ReplyIs248BytesWithBootstrapFields()
    {
        using var dev = StartDevice(o => o.UserDefinedName = "bench");
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        var ack = c.Discovery();

        Assert.Equal(GvcpConst.StatusSuccess, ack.Status);
        Assert.Equal(GvcpConst.DiscoveryAck, ack.Command);
        Assert.Equal(c.LastReqId, ack.ReqId);
        Assert.Equal(GvbsAddr.DiscoveryDataLen, ack.Payload.Length);
        Assert.Equal(dev.GvcpEndPoint, ack.From);

        Assert.Equal(0x0002_0000u, ack.U32((int)GvbsAddr.Version));
        uint mode = ack.U32((int)GvbsAddr.DeviceMode);
        Assert.NotEqual(0u, mode & GvbsAddr.DeviceModeBigEndian);
        Assert.Equal(1u, mode & 0xFFFF);
        Assert.Equal(0x0247u, ack.U32((int)GvbsAddr.MacHigh));
        Assert.Equal(0x4556u, ack.U32((int)GvbsAddr.MacLow) >> 16);
        Assert.Equal(0x7F00_0001u, ack.U32((int)GvbsAddr.CurrentIp));
        Assert.Equal(0xFF00_0000u, ack.U32((int)GvbsAddr.CurrentSubnet));
        Assert.Equal(0u, ack.U32((int)GvbsAddr.CurrentGateway));
        Assert.Equal("GevSharp", ack.Str((int)GvbsAddr.ManufacturerName, GvbsAddr.ManufacturerNameLen));
        Assert.Equal("SimCamera", ack.Str((int)GvbsAddr.ModelName, GvbsAddr.ModelNameLen));
        Assert.Equal("1.0", ack.Str((int)GvbsAddr.DeviceVersion, GvbsAddr.DeviceVersionLen));
        Assert.Equal("SIM0001", ack.Str((int)GvbsAddr.SerialNumber, GvbsAddr.SerialNumberLen));
        Assert.Equal("bench", ack.Str((int)GvbsAddr.UserDefinedName, GvbsAddr.UserDefinedNameLen));
        Assert.Equal(1, dev.DiscoveryCount);
    }

    [Fact]
    public void Discovery_WithoutAckRequired_GetsNoReply()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        c.SendRaw(RawGvcpClient.BuildCmd(GvcpConst.DiscoveryCmd, GvcpConst.FlagAllowBroadcastAck, 7, ReadOnlySpan<byte>.Empty));

        Assert.Null(c.Receive(200));
        Assert.Equal(1, dev.DiscoveryCount);
    }

    // ---- READREG / WRITEREG ----

    [Fact]
    public void ReadReg_MultipleRegistersInOnePacket()
    {
        using var dev = StartDevice(o => o.HeartbeatTimeoutMs = 1234);
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        var (status, values) = c.ReadRegs(GvbsAddr.Version, GvbsAddr.HeartbeatTimeout, GvbsAddr.TimestampTickFreqLow, GvbsAddr.GvcpCapability, SimFeatureAddr.Width);

        Assert.Equal(GvcpConst.StatusSuccess, status);
        Assert.Equal(new uint[] { 0x0002_0000, 1234, 1_000_000_000, dev.Registers.ReadU32(GvbsAddr.GvcpCapability), 640 }, values);
        Assert.Equal(1, dev.ReadRegCount);
        Assert.NotEqual(0u, values[3] & GvbsAddr.GvcpCapPacketResend);
        Assert.Equal(0u, values[3] & GvbsAddr.GvcpCapPendingAck);
    }

    [Fact]
    public void ReadReg_UnalignedAddress_BadAlignment()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        var (status, values) = c.ReadRegs(GvbsAddr.Version, 0x0002);

        Assert.Equal(GvcpConst.StatusBadAlignment, status);
        Assert.Single(values);   // 실패 전까지 읽은 값만 실린다
        Assert.Contains("BAD_ALIGNMENT", dev.LastError);
    }

    [Fact]
    public void ReadReg_UnmappedAddress_InvalidAddress()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        var (status, _) = c.ReadRegs(0x0500_0000);

        Assert.Equal(GvcpConst.StatusInvalidAddress, status);
    }

    [Fact]
    public void WriteReg_RoundTripAndIndex()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        var (status, index) = c.WriteRegs((SimFeatureAddr.Width, 320), (SimFeatureAddr.Height, 240));

        Assert.Equal(GvcpConst.StatusSuccess, status);
        Assert.Equal(2, index);
        Assert.Equal(320u, c.ReadReg(SimFeatureAddr.Width));
        Assert.Equal(240u, dev.Registers.ReadU32(SimFeatureAddr.Height));
        Assert.Equal(1, dev.WriteRegCount);
    }

    [Fact]
    public void WriteReg_ReadOnlyRegister_WriteProtectWithFailingIndex()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        var (status, index) = c.WriteRegs((SimFeatureAddr.Width, 128), (GvbsAddr.Version, 0xDEAD_BEEF), (SimFeatureAddr.Height, 96));

        Assert.Equal(GvcpConst.StatusWriteProtect, status);
        Assert.Equal(1, index);                                        // 두 번째(인덱스 1)에서 실패
        Assert.Equal(128u, c.ReadReg(SimFeatureAddr.Width));           // 앞의 것은 반영
        Assert.Equal(0x0002_0000u, c.ReadReg(GvbsAddr.Version));       // 보호 레지스터는 그대로
        Assert.Equal(480u, c.ReadReg(SimFeatureAddr.Height));          // 뒤의 것은 처리 안 됨
    }

    [Fact]
    public void WriteReg_UnalignedAndUnmapped()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        Assert.Equal(GvcpConst.StatusBadAlignment, c.WriteReg(SimFeatureAddr.Width + 2, 1).Status);
        Assert.Equal(GvcpConst.StatusInvalidAddress, c.WriteReg(0x0500_0000, 1).Status);
    }

    // ---- READMEM / WRITEMEM ----

    [Fact]
    public void ReadMem_WithReservedWordHoles_RejectsTheHoleAndCompactsBulkReads()
    {
        using var dev = StartDevice(o => o.HasReservedWordHoles = true);
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        // 홀에서 시작하는 접근은 INVALID_ADDRESS
        Assert.Equal(GvcpConst.StatusInvalidAddress, c.ReadRegs(0x0020).Status);
        Assert.Equal(GvcpConst.StatusInvalidAddress, c.ReadMem(0x0040, 4).Status);

        // 홀을 지나는 벌크 읽기는 그 워드를 빼고 뒤를 당겨 채운다 — 현재 IP(0x0024)가 0x20 자리에, 그 뒤가 한 워드씩 밀린다
        var (status, data) = c.ReadMem(0, 48);
        Assert.Equal(GvcpConst.StatusSuccess, status);
        Assert.Equal(48, data.Length);
        Assert.Equal(0x7F00_0001u, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x20)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x24)));

        // 자기 주소에서 시작하는 접근은 정확하다
        Assert.Equal(0x7F00_0001u, c.ReadReg(GvbsAddr.CurrentIp));
        Assert.Equal(0xFF00_0000u, c.ReadReg(GvbsAddr.CurrentSubnet));
        var (nameStatus, name) = c.ReadMem(GvbsAddr.ManufacturerName, GvbsAddr.ManufacturerNameLen);
        Assert.Equal(GvcpConst.StatusSuccess, nameStatus);
        Assert.Equal("GevSharp", Encoding.UTF8.GetString(name).TrimEnd('\0'));

        // DISCOVERY_ACK 는 장치가 만드는 고정 이미지라 밀리지 않는다
        Assert.Equal("SimCamera", c.Discovery().Str((int)GvbsAddr.ModelName, GvbsAddr.ModelNameLen));
    }

    [Fact]
    public void ReadMem_WithoutTheQuirk_IsAByteImage()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        Assert.Equal(GvcpConst.StatusSuccess, c.ReadRegs(0x0020).Status);
        var (status, data) = c.ReadMem(0, GvbsAddr.DiscoveryDataLen);
        Assert.Equal(GvcpConst.StatusSuccess, status);
        Assert.Equal(0x7F00_0001u, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)GvbsAddr.CurrentIp)));
        Assert.Equal(c.Discovery().Payload, data);
    }

    [Fact]
    public void ReadMem_ReturnsAddressAndBytes()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        var ack = c.Request(GvcpConst.ReadMemCmd, RawGvcpClient.ReadMemPayload(GvbsAddr.ManufacturerName, 32));

        Assert.Equal(GvcpConst.StatusSuccess, ack.Status);
        Assert.Equal(GvcpConst.ReadMemAck, ack.Command);
        Assert.Equal(4 + 32, ack.Payload.Length);
        Assert.Equal(GvbsAddr.ManufacturerName, ack.U32(0));
        Assert.Equal("GevSharp", ack.Str(4, 32));
        Assert.Equal(1, dev.ReadMemCount);
    }

    [Fact]
    public void ReadMem_Errors()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        Assert.Equal(GvcpConst.StatusBadAlignment, c.ReadMem(GvbsAddr.ManufacturerName + 1, 32).Status);
        Assert.Equal(GvcpConst.StatusInvalidParameter, c.ReadMem(GvbsAddr.ManufacturerName, 30).Status);
        Assert.Equal(GvcpConst.StatusInvalidParameter, c.ReadMem(GvbsAddr.ManufacturerName, 516).Status);
        Assert.Equal(GvcpConst.StatusInvalidAddress, c.ReadMem(SimRegisterMap.MainRegionSize - 8, 16).Status);   // 영역 끝을 넘는다
        Assert.Equal(GvcpConst.StatusInvalidAddress, c.ReadMem(0x0002_0000, 4).Status);                       // 매핑 없는 틈
    }

    [Fact]
    public void WriteMem_UserDefinedName_RoundTripAndVisibleInDiscovery()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        var name = new byte[GvbsAddr.UserDefinedNameLen];
        Encoding.ASCII.GetBytes("line-3-cam").CopyTo(name, 0);

        var (status, index) = c.WriteMem(GvbsAddr.UserDefinedName, name);

        Assert.Equal(GvcpConst.StatusSuccess, status);
        Assert.Equal(GvbsAddr.UserDefinedNameLen, index);
        var (rs, data) = c.ReadMem(GvbsAddr.UserDefinedName, GvbsAddr.UserDefinedNameLen);
        Assert.Equal(GvcpConst.StatusSuccess, rs);
        Assert.Equal(name, data);
        Assert.Equal("line-3-cam", c.Discovery().Str((int)GvbsAddr.UserDefinedName, GvbsAddr.UserDefinedNameLen));
        Assert.Equal(1, dev.WriteMemCount);
    }

    [Fact]
    public void WriteMem_Errors()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        var four = new byte[4];

        Assert.Equal(GvcpConst.StatusWriteProtect, c.WriteMem(GvbsAddr.ModelName, new byte[8]).Status);
        Assert.Equal(GvcpConst.StatusWriteProtect, c.WriteMem(SimRegisterMap.XmlRegionBase, four).Status);
        Assert.Equal(GvcpConst.StatusBadAlignment, c.WriteMem(SimFeatureAddr.Width + 2, four).Status);
        Assert.Equal(GvcpConst.StatusInvalidParameter, c.WriteMem(SimFeatureAddr.Width, new byte[6]).Status);
        Assert.Equal(GvcpConst.StatusInvalidAddress, c.WriteMem(0x0002_0000, four).Status);
    }

    // ---- 기타 프로토콜 동작 ----

    [Fact]
    public void UnknownCommand_NotImplemented()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        var ack = c.Request(0x0200, new byte[] { 1, 2, 3, 4 });

        Assert.Equal(GvcpConst.StatusNotImplemented, ack.Status);
        Assert.Equal(0x0201, ack.Command);
        Assert.Equal(c.LastReqId, ack.ReqId);
        Assert.Empty(ack.Payload);
    }

    [Fact]
    public void MalformedPackets_IgnoredAndRecorded()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        c.SendRaw(new byte[] { 0x42, 0x01, 0x00 });                              // 헤더보다 짧다
        Assert.Null(c.Receive(150));
        Assert.Contains("malformed", dev.LastError);

        c.SendRaw(RawGvcpClient.BuildCmd(GvcpConst.ReadRegCmd, GvcpConst.FlagAckRequired, 9, new byte[4]).AsSpan(0, 10).ToArray());   // 길이 필드 > 데이터그램
        Assert.Null(c.Receive(150));

        var wrongType = RawGvcpClient.BuildCmd(GvcpConst.ReadRegCmd, GvcpConst.FlagAckRequired, 9, RawGvcpClient.ReadRegPayload(GvbsAddr.Version));
        wrongType[0] = 0x00;
        c.SendRaw(wrongType);
        Assert.Null(c.Receive(150));

        Assert.Equal(3, dev.MalformedCount);
        Assert.Equal(GvcpConst.StatusSuccess, c.ReadRegs(GvbsAddr.Version).Status);   // 서버는 계속 살아 있다
    }

    [Fact]
    public void LargeDatagram_IsParsedNotDroppedAsSocketError()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        // 4 KiB 를 훌쩍 넘는 READREG — 수신 버퍼가 데이터그램 최대 길이라 잘리지도(Linux) 소켓 오류로 버려지지도(Windows) 않고
        // 정상 경로에서 거절된다: 5000 / 4 = 1250 개는 한 패킷의 상한(135)을 넘으므로 INVALID_PARAMETER
        var ack = c.Request(GvcpConst.ReadRegCmd, new byte[5000]);

        Assert.Equal(GvcpConst.StatusInvalidParameter, ack.Status);
        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
        Assert.Equal(0, dev.MalformedCount);
        Assert.DoesNotContain("socket error", dev.LastError ?? "");
    }

    [Fact]
    public void ReqId_IsEchoedAndRepliesComeFromGvcpPort()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        c.SendRaw(RawGvcpClient.BuildCmd(GvcpConst.ReadRegCmd, GvcpConst.FlagAckRequired, 0xBEEF, RawGvcpClient.ReadRegPayload(GvbsAddr.Version)));
        var ack = c.Receive();

        Assert.NotNull(ack);
        Assert.Equal(0xBEEF, ack!.ReqId);
        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
        Assert.Equal(dev.GvcpEndPoint, ack.From);
    }

    // ---- CCP / 하트비트 ----

    [Fact]
    public void Ccp_OwnerExclusivityAndRelease()
    {
        using var dev = StartDevice();
        using var a = new RawGvcpClient(dev.GvcpEndPoint);
        using var b = new RawGvcpClient(dev.GvcpEndPoint);
        var owners = new List<IPEndPoint?>();
        dev.ControlOwnerChanged += ep => { lock (owners) owners.Add(ep); };

        Assert.Null(dev.ControlOwner);
        Assert.Equal(GvcpConst.StatusSuccess, a.WriteReg(GvbsAddr.Ccp, GvbsAddr.CcpControl).Status);
        Assert.Equal(a.LocalEndPoint, dev.ControlOwner);
        Assert.Equal(GvbsAddr.CcpControl, b.ReadReg(GvbsAddr.Ccp));                                 // 읽기는 누구나
        Assert.Equal((uint)a.LocalEndPoint.Port, b.ReadReg(GvbsAddr.PrimaryAppPort));
        Assert.Equal(0x7F00_0001u, b.ReadReg(GvbsAddr.PrimaryAppIp));

        Assert.Equal(GvcpConst.StatusAccessDenied, b.WriteReg(SimFeatureAddr.Width, 100).Status);  // 다른 애플리케이션의 쓰기
        Assert.Equal(GvcpConst.StatusAccessDenied, b.WriteReg(GvbsAddr.Ccp, GvbsAddr.CcpControl).Status);
        Assert.Equal(GvcpConst.StatusAccessDenied, b.WriteMem(SimFeatureAddr.Width, new byte[4]).Status);
        Assert.Equal(640u, b.ReadReg(SimFeatureAddr.Width));
        Assert.Equal(GvcpConst.StatusSuccess, a.WriteReg(SimFeatureAddr.Width, 100).Status);      // 보유자는 된다

        Assert.Equal(GvcpConst.StatusSuccess, a.WriteReg(GvbsAddr.Ccp, 0).Status);                // 해제
        Assert.Null(dev.ControlOwner);
        Assert.Equal(0u, b.ReadReg(GvbsAddr.Ccp));
        Assert.Equal(GvcpConst.StatusSuccess, b.WriteReg(SimFeatureAddr.Width, 200).Status);
        Assert.Equal(GvcpConst.StatusSuccess, b.WriteReg(GvbsAddr.Ccp, GvbsAddr.CcpControl | GvbsAddr.CcpExclusive).Status);
        Assert.Equal(b.LocalEndPoint, dev.ControlOwner);
        Assert.Equal(3u, a.ReadReg(GvbsAddr.Ccp));

        lock (owners) Assert.Equal(new IPEndPoint?[] { a.LocalEndPoint, null, b.LocalEndPoint }, owners);
    }

    [Fact]
    public void Ccp_HeartbeatKeepsControlThenExpires()
    {
        // 타임아웃 2 s / 폴링 100 ms. 장치가 재는 것은 읽기 요청이 도착한 간격(= 100 ms + 왕복)이므로,
        // 굶주린 러너에서 왕복이 수백 ms 로 늘어도 2 s 안에는 다음 요청이 닿는다. 1 s 로는 실제로 모자랐다.
        const int timeoutMs = 2000;
        using var dev = StartDevice(o => o.HeartbeatTimeoutMs = timeoutMs);
        using var a = new RawGvcpClient(dev.GvcpEndPoint);

        Assert.Equal(GvcpConst.StatusSuccess, a.WriteReg(GvbsAddr.Ccp, GvbsAddr.CcpControl).Status);
        Assert.Equal((uint)timeoutMs, a.ReadReg(GvbsAddr.HeartbeatTimeout));

        // 타임아웃보다 자주 CCP 를 읽으면 제어권이 유지된다 — 타임아웃보다 긴 시간 동안, 그리고 충분한 횟수만큼.
        // 고정 시간 안에 "몇 번 읽혔나" 로 세면 재는 것이 러너가 된다(굶주린 러너에서 세 번밖에 못 돌아 깨졌다) —
        // 시간과 횟수를 둘 다 조건으로 두고 필요한 만큼 더 돈다. 부하는 이 루프를 늘릴 뿐이라 시험이 약해지지 않는다.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs + 500 || dev.HeartbeatObserved < 8)
        {
            Assert.Equal(GvbsAddr.CcpControl, a.ReadReg(GvbsAddr.Ccp));
            Thread.Sleep(100);
        }
        Assert.Equal(a.LocalEndPoint, dev.ControlOwner);
        Assert.True(dev.HeartbeatObserved >= 5, $"HeartbeatObserved = {dev.HeartbeatObserved}");
        Assert.Equal(0, dev.HeartbeatTimeouts);

        // 명령을 끊으면 타임아웃 뒤 CCP 가 비워진다
        Thread.Sleep(timeoutMs + 1000);
        Assert.Null(dev.ControlOwner);
        Assert.Equal(1, dev.HeartbeatTimeouts);
        Assert.Equal(0u, a.ReadReg(GvbsAddr.Ccp));
        Assert.Equal(0u, a.ReadReg(GvbsAddr.PrimaryAppPort));
    }

    [Fact]
    public void Ccp_HeartbeatTimeoutZero_NeverExpires()
    {
        using var dev = StartDevice(o => o.HeartbeatTimeoutMs = 100);
        using var a = new RawGvcpClient(dev.GvcpEndPoint);

        // 제어권 획득과 HeartbeatTimeout = 0 을 한 WRITEREG 에 실어 보낸다 — 두 명령으로 나누면 그 사이의 왕복 하나가
        // 100 ms 를 넘기는 순간(굶주린 러너에서 실제로 그랬다) 0 을 쓰기도 전에 세션이 만료돼, 시험 대상과 무관하게 깨진다.
        var (status, _) = a.WriteRegs((GvbsAddr.Ccp, GvbsAddr.CcpControl), (GvbsAddr.HeartbeatTimeout, 0u));
        Assert.Equal(GvcpConst.StatusSuccess, status);
        Thread.Sleep(350);

        Assert.Equal(a.LocalEndPoint, dev.ControlOwner);
        Assert.Equal(0, dev.HeartbeatTimeouts);
    }

    // ---- PENDING_ACK ----

    [Fact]
    public void PendingAck_PrecedesRealAckByTheAnnouncedDelay()
    {
        using var dev = StartDevice(o => { o.SupportPendingAck = true; o.PendingAckDelayMs = 150; });
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        Assert.NotEqual(0u, c.ReadReg(GvbsAddr.GvcpCapability) & GvbsAddr.GvcpCapPendingAck);

        var sw = Stopwatch.StartNew();
        var first = c.Request(GvcpConst.WriteRegCmd, RawGvcpClient.WriteRegPayload((SimFeatureAddr.Width, 256)));
        long firstMs = sw.ElapsedMilliseconds;
        var second = c.Receive(2000);
        long secondMs = sw.ElapsedMilliseconds;

        Assert.Equal(GvcpConst.PendingAck, first.Command);
        Assert.Equal(GvcpConst.StatusSuccess, first.Status);
        Assert.Equal(c.LastReqId, first.ReqId);
        Assert.Equal(4, first.Payload.Length);
        Assert.Equal(150, first.U16(2));
        // "PENDING_ACK 가 먼저 온다" 는 위의 first.Command 단정과 아래의 간격(≥ 120 ms)이 이미 못 박는다.
        // 그 위에 "PENDING_ACK 자체가 100 ms 안에 왔다" 를 더 재지 않는다 — 굶주린 스케줄러에서는 그 값이 늘어나지만
        // 순서도 간격도 그대로 성립한다. 늘어난 것은 시뮬레이터가 아니라 러너다.

        Assert.NotNull(second);
        Assert.Equal(GvcpConst.WriteRegAck, second!.Command);
        Assert.Equal(GvcpConst.StatusSuccess, second.Status);
        Assert.Equal(c.LastReqId, second.ReqId);
        Assert.Equal(1, second.U16(2));
        Assert.True(secondMs - firstMs >= 120, $"real ACK arrived only {secondMs - firstMs} ms after PENDING_ACK");
        Assert.Equal(256u, c.ReadReg(SimFeatureAddr.Width));
    }

    [Fact]
    public void PendingAck_NotSentWhenDisabled()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        var ack = c.Request(GvcpConst.WriteRegCmd, RawGvcpClient.WriteRegPayload((SimFeatureAddr.Width, 256)));

        Assert.Equal(GvcpConst.WriteRegAck, ack.Command);
        Assert.Null(c.Receive(100));
    }

    // ---- XML ----

    [Fact]
    public void Xml_ReadableThroughLocalUrlAndWellFormed()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        var (urlStatus, urlBytes) = c.ReadMem(GvbsAddr.FirstUrl, (ushort)GvbsAddr.UrlLen);
        Assert.Equal(GvcpConst.StatusSuccess, urlStatus);
        string url = Encoding.ASCII.GetString(urlBytes, 0, Array.IndexOf(urlBytes, (byte)0));
        Assert.StartsWith("Local:SimCamera.xml;", url);
        var parts = url.Substring("Local:".Length).Split(';');
        Assert.Equal(3, parts.Length);
        uint addr = Convert.ToUInt32(parts[1], 16);
        int len = Convert.ToInt32(parts[2], 16);
        Assert.Equal(SimRegisterMap.XmlRegionBase, addr);

        // 512 바이트 단위로 읽고 마지막 청크는 4의 배수로 올림해 읽은 뒤 잘라낸다
        var xml = new byte[len];
        int done = 0;
        while (done < len)
        {
            int want = Math.Min(GvcpConst.MaxMemPayload, (len - done + 3) & ~3);
            var (st, chunk) = c.ReadMem(addr + (uint)done, (ushort)want);
            Assert.Equal(GvcpConst.StatusSuccess, st);
            Assert.Equal(want, chunk.Length);
            int copy = Math.Min(chunk.Length, len - done);
            Buffer.BlockCopy(chunk, 0, xml, done, copy);
            done += copy;
        }
        string text = Encoding.UTF8.GetString(xml);
        Assert.Equal(SimDevice.DefaultGenApiXml, text);

        var doc = XDocument.Parse(text);
        XNamespace ns = "http://www.genicam.org/GenApi/Version_1_1";
        Assert.Equal(ns + "RegisterDescription", doc.Root!.Name);
        Assert.Equal("SimCamera", (string?)doc.Root.Attribute("ModelName"));
        Assert.Equal("GevSharp", (string?)doc.Root.Attribute("VendorName"));
        Assert.Equal("1", (string?)doc.Root.Attribute("SchemaMajorVersion"));
        var root = doc.Descendants(ns + "Category").Single(e => (string?)e.Attribute("Name") == "Root");
        Assert.NotEmpty(root.Elements(ns + "pFeature"));

        // 요구된 노드들이 존재하고 요구된 구조를 쓴다
        string[] required = { "Width", "Height", "OffsetX", "OffsetY", "PixelFormat", "ExposureTime", "ExposureTimeRaw", "GainSelector", "Gain", "GainRaw",
                              "TriggerMode", "TriggerSource", "AcquisitionMode", "AcquisitionStart", "AcquisitionStop", "AcquisitionFrameRate", "PayloadSize",
                              "TestPattern", "UserSetSelector", "UserSetLoad", "ReverseX", "DeviceModelName", "DeviceUserID", "GevSCPSPacketSize", "GevHeartbeatTimeout" };
        var names = doc.Descendants().Select(e => (string?)e.Attribute("Name")).Where(n => n is not null).ToHashSet();
        foreach (var n in required) Assert.Contains(n, names);
        Assert.NotEmpty(doc.Descendants(ns + "StructReg"));
        Assert.Contains(doc.Descendants(ns + "Group"), g => g.Ancestors(ns + "Group").Any());   // 중첩 Group
        Assert.NotEmpty(doc.Descendants(ns + "pIsAvailable"));
        Assert.NotEmpty(doc.Descendants(ns + "pIsLocked"));
        Assert.NotEmpty(doc.Descendants(ns + "pInvalidator"));
        Assert.NotEmpty(doc.Descendants(ns + "pSelected"));
        Assert.NotEmpty(doc.Descendants(ns + "FormulaTo"));
        Assert.NotEmpty(doc.Descendants(ns + "FormulaFrom"));

        // Converter 의 FormulaFrom 은 정수 레지스터 값(TO)을 받는다 — 수식 엔진은 정수 ÷ 정수를 잘라 버리므로
        // 실수 리터럴로 승격시켜야 0.1 dB·µs 이하 해상도가 살아남고 쓰기→읽기가 왕복한다
        foreach (var conv in doc.Descendants(ns + "Converter"))
        {
            string name = (string?)conv.Attribute("Name") ?? "?";
            string from = (string?)conv.Element(ns + "FormulaFrom") ?? "";
            Assert.True(Regex.IsMatch(from, @"\d+\.\d+"), $"Converter {name}: FormulaFrom '{from}' has no float literal, so integer division would truncate");
        }
        var gain = doc.Descendants(ns + "Converter").Single(e => (string?)e.Attribute("Name") == "Gain");
        Assert.Equal("TO / 10.0", (string?)gain.Element(ns + "FormulaFrom"));
        Assert.Equal("FROM * 10", (string?)gain.Element(ns + "FormulaTo"));
        Assert.Equal("GainRaw", (string?)gain.Element(ns + "pValue"));
        Assert.Contains(doc.Descendants(ns + "Integer"), e => (string?)e.Attribute("Name") == "GainRaw");
    }

    [Fact]
    public void Xml_ReadPastEndIsInvalidAddress()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        uint end = SimRegisterMap.XmlRegionBase + dev.Registers.XmlRegionSize;

        Assert.Equal(GvcpConst.StatusSuccess, c.ReadMem(end - 4, 4).Status);
        Assert.Equal(GvcpConst.StatusInvalidAddress, c.ReadMem(end - 4, 8).Status);
        Assert.Equal(GvcpConst.StatusInvalidAddress, c.ReadMem(end, 4).Status);
    }

    [Fact]
    public void Xml_CustomTextIsServed()
    {
        const string custom = "<?xml version=\"1.0\"?><RegisterDescription xmlns=\"http://www.genicam.org/GenApi/Version_1_1\" ModelName=\"X\" VendorName=\"Y\" SchemaMajorVersion=\"1\" SchemaMinorVersion=\"1\" SchemaSubMinorVersion=\"0\" MajorVersion=\"1\" MinorVersion=\"0\" SubMinorVersion=\"0\" ProductGuid=\"a\" VersionGuid=\"b\"><Category Name=\"Root\"/></RegisterDescription>";
        using var dev = StartDevice(o => o.GenApiXml = custom);
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        var (_, urlBytes) = c.ReadMem(GvbsAddr.FirstUrl, 64);
        string url = Encoding.ASCII.GetString(urlBytes, 0, Array.IndexOf(urlBytes, (byte)0));
        int len = Convert.ToInt32(url.Split(';')[2], 16);
        Assert.Equal(Encoding.UTF8.GetByteCount(custom), len);

        var (st, data) = c.ReadMem(SimRegisterMap.XmlRegionBase, (ushort)((len + 3) & ~3));
        Assert.Equal(GvcpConst.StatusSuccess, st);
        Assert.Equal(custom, Encoding.UTF8.GetString(data, 0, len));
    }

    // ---- 명령 비트·타임스탬프 ----

    [Fact]
    public void CommandBits_SelfClearAndUserSetLoadRestoresDefaults()
    {
        using var dev = StartDevice(o => { o.Width = 800; o.Height = 600; });
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        c.WriteRegOk(SimFeatureAddr.Width, 320);
        c.WriteRegOk(SimFeatureAddr.GainSelector, 2);
        c.WriteRegOk(SimFeatureAddr.GainRaw0 + 8, 77);
        Assert.Equal(77u, c.ReadReg(SimFeatureAddr.GainRaw0 + 8));

        c.WriteRegOk(SimFeatureAddr.UserSetLoad, 1);
        Assert.Equal(0u, c.ReadReg(SimFeatureAddr.UserSetLoad));
        Assert.Equal(800u, c.ReadReg(SimFeatureAddr.Width));
        Assert.Equal(0u, c.ReadReg(SimFeatureAddr.GainRaw0 + 8));
        Assert.Equal(0u, c.ReadReg(SimFeatureAddr.GainSelector));
    }

    [Fact]
    public void TimestampLatch_CapturesRunningCounter()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        c.WriteRegOk(GvbsAddr.TimestampControl, 2);   // reset
        Thread.Sleep(20);
        c.WriteRegOk(GvbsAddr.TimestampControl, 1);   // latch
        var (_, v) = c.ReadRegs(GvbsAddr.TimestampLatchedHigh, GvbsAddr.TimestampLatchedLow, GvbsAddr.TimestampControl);
        ulong latched = ((ulong)v[0] << 32) | v[1];

        // 상한이 지키는 것은 눈금(1 GHz)이지 20 ms 가 아니다 — 굶주린 러너에서는 대기와 두 번의 왕복이 초 단위로 늘어난다.
        // 초·틱처럼 10^3 배 이상 어긋난 값을 실으면 아래위 어느 쪽으로든 범위를 벗어난다.
        Assert.InRange(latched, 10_000_000ul, 20_000_000_000ul);   // 10 ms .. 20 s (1 GHz)
        Assert.Equal(0u, v[2]);
    }

    [Fact]
    public void Ctor_RejectsNonIPv4BindAddress()
    {
        var ex = Assert.Throws<ArgumentException>(() => new SimDevice(new SimDeviceOpt { BindAddress = IPAddress.IPv6Loopback }));
        Assert.Contains("IPv4", ex.Message);
    }

    [Fact]
    public void StartStop_CanBeRepeatedAndPortIsEphemeral()
    {
        var dev = new SimDevice();
        Assert.Throws<InvalidOperationException>(() => dev.GvcpEndPoint);
        dev.Start();
        var first = dev.GvcpEndPoint;
        Assert.NotEqual(0, first.Port);
        Assert.Equal(IPAddress.Loopback, first.Address);
        Assert.Throws<InvalidOperationException>(dev.Start);
        dev.Stop();
        dev.Stop();
        dev.Start();
        using (var c = new RawGvcpClient(dev.GvcpEndPoint)) Assert.Equal(GvcpConst.StatusSuccess, c.Discovery().Status);
        dev.Dispose();
    }
}
