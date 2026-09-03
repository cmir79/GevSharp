using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using GevSharp.Gvcp;

namespace GevSharp.Tests.Gvcp;

public class GvcpPacketTests
{
    // ---------------------------------------------------------------- headers

    [Fact]
    public void CmdHeaderRoundTripsAndValidates()
    {
        var buf = new byte[8];
        new GvcpCmdHeader(GvcpConst.ReadRegCmd, 4, 0x1234, GvcpConst.FlagAckRequired).Write(buf);
        Assert.Equal(new byte[] { 0x42, 0x01, 0x00, 0x80, 0x00, 0x04, 0x12, 0x34 }, buf);

        var full = new byte[12];
        buf.CopyTo(full, 0);
        var h = GvcpCmdHeader.Parse(full);
        Assert.Equal(GvcpConst.ReadRegCmd, h.Command);
        Assert.Equal(4, h.Length);
        Assert.Equal(0x1234, h.ReqId);
        Assert.True(h.IsAckRequired);

        Assert.Throws<GevException>(() => GvcpCmdHeader.Parse(buf));                 // payload missing
        Assert.Throws<GevException>(() => GvcpCmdHeader.Parse(new byte[7]));         // too short
        full[0] = 0x00;
        Assert.Throws<GevException>(() => GvcpCmdHeader.Parse(full));                // wrong type
        Assert.False(GvcpCmdHeader.TryParse(full, out _));
    }

    [Fact]
    public void AckHeaderReadsStatusWordIncludingErrorType()
    {
        var packet = new byte[] { 0x80, 0x06, 0x00, 0x83, 0x00, 0x00, 0x00, 0x07 };
        var h = GvcpAckHeader.Parse(packet);
        Assert.Equal(GvcpConst.StatusAccessDenied, h.Status);
        Assert.True(h.IsError);
        Assert.Equal(GvcpConst.WriteRegAck, h.Command);
        Assert.Equal(7, h.ReqId);

        var truncated = new byte[] { 0x00, 0x00, 0x00, 0x81, 0x00, 0x08, 0x00, 0x01, 0xAA, 0xBB };
        Assert.Throws<GevException>(() => GvcpAckHeader.Parse(truncated));
        Assert.False(GvcpAckHeader.TryParse(truncated, out _));
        Assert.False(GvcpAckHeader.TryParse(new byte[3], out _));
    }

    // ---------------------------------------------------------------- READREG / WRITEREG

    [Fact]
    public void ReadRegCmdHasExactBytesAndStampsReqId()
    {
        var cmd = GvcpCmd.ReadReg(0x0A00);
        Assert.Equal(GvcpConst.ReadRegCmd, cmd.Command);
        Assert.Equal(GvcpConst.ReadRegAck, cmd.ExpectedAck);
        Assert.Equal("READREG", cmd.Name);
        Assert.Equal(new byte[] { 0x42, 0x01, 0x00, 0x80, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x00 }, cmd.Packet.ToArray());
        Assert.Equal(new byte[] { 0x42, 0x01, 0x00, 0x80, 0x00, 0x04, 0xBE, 0xEF, 0x00, 0x00, 0x0A, 0x00 }, cmd.ToArray(0xBEEF));
    }

    [Fact]
    public void ReadRegsRoundTripAndLimits()
    {
        var addrs = new uint[] { 0x0000, 0x0934, 0x0D04 };
        var cmd = GvcpCmd.ReadRegs(addrs);
        Assert.Equal(12, cmd.PayloadLength);
        Assert.Equal(3, GvcpPacket.ReadRegCount(cmd.Payload));
        for (var i = 0; i < addrs.Length; i++)
            Assert.Equal(addrs[i], GvcpPacket.ReadRegAddress(cmd.Payload, i));

        Assert.Throws<GevException>(() => GvcpCmd.ReadRegs(ReadOnlySpan<uint>.Empty));
        Assert.Throws<GevException>(() => GvcpCmd.ReadRegs(new uint[136]));
        Assert.Throws<GevException>(() => GvcpCmd.ReadReg(0x0A02));
        Assert.Throws<GevException>(() => GvcpPacket.ReadRegCount(new byte[6]));
        Assert.Throws<GevException>(() => GvcpPacket.ReadRegAddress(cmd.Payload, 3));
        Assert.Equal(GvcpConst.MaxRegsPerPacket, GvcpPacket.ReadRegCount(GvcpCmd.ReadRegs(new uint[135]).Payload));
    }

    [Fact]
    public void WriteRegsRoundTripExactBytesAndLimits()
    {
        var single = GvcpCmd.WriteReg(0x0A00, 2);
        Assert.Equal(new byte[] { 0x42, 0x01, 0x00, 0x82, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x02 }, single.Packet.ToArray());

        var writes = new[] { new KeyValuePair<uint, uint>(0x0938, 3000), new KeyValuePair<uint, uint>(0x0D00, 50000) };
        var cmd = GvcpCmd.WriteRegs(writes);
        Assert.Equal(2, GvcpPacket.WriteRegCount(cmd.Payload));
        GvcpPacket.WriteRegEntry(cmd.Payload, 1, out var addr, out var value);
        Assert.Equal(0x0D00u, addr);
        Assert.Equal(50000u, value);

        Assert.Throws<GevException>(() => GvcpCmd.WriteRegs(ReadOnlySpan<KeyValuePair<uint, uint>>.Empty));
        Assert.Throws<GevException>(() => GvcpCmd.WriteRegs(new KeyValuePair<uint, uint>[68]));
        Assert.Throws<GevException>(() => GvcpCmd.WriteReg(0x0A01, 1));
        Assert.Equal(67, GvcpPacket.MaxWriteRegsPerPacket);
    }

    // ---------------------------------------------------------------- READMEM / WRITEMEM

    [Fact]
    public void ReadMemCmdExactBytesAndValidation()
    {
        var cmd = GvcpCmd.ReadMem(0x0200, 512);
        Assert.Equal(new byte[] { 0x42, 0x01, 0x00, 0x84, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02, 0x00 }, cmd.Packet.ToArray());
        GvcpPacket.ReadMemFields(cmd.Payload, out var addr, out var count);
        Assert.Equal(0x0200u, addr);
        Assert.Equal(512, count);

        Assert.Throws<GevException>(() => GvcpCmd.ReadMem(0x0200, 0));
        Assert.Throws<GevException>(() => GvcpCmd.ReadMem(0x0200, 6));
        Assert.Throws<GevException>(() => GvcpCmd.ReadMem(0x0200, 516));
        Assert.Throws<GevException>(() => GvcpCmd.ReadMem(0x0201, 4));
        Assert.Throws<GevException>(() => GvcpCmd.ReadMem(0xFFFF_FFFC, 8));
        Assert.Throws<GevException>(() => GvcpPacket.ReadMemFields(new byte[7], out _, out _));
    }

    [Fact]
    public void WriteMemRoundTrip()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var cmd = GvcpCmd.WriteMem(0x1000, data);
        Assert.Equal(12, cmd.PayloadLength);
        Assert.Equal(0x1000u, GvcpPacket.WriteMemAddress(cmd.Payload));
        Assert.Equal(data, GvcpPacket.WriteMemData(cmd.Payload).ToArray());

        Assert.Throws<GevException>(() => GvcpCmd.WriteMem(0x1000, new byte[3]));
        Assert.Throws<GevException>(() => GvcpCmd.WriteMem(0x1000, new byte[516]));
        Assert.Throws<GevException>(() => GvcpCmd.WriteMem(0x1002, new byte[4]));
        Assert.Throws<GevException>(() => GvcpPacket.WriteMemData(new byte[2]));
    }

    // ---------------------------------------------------------------- DISCOVERY / FORCEIP

    [Fact]
    public void DiscoveryCmdIsHeaderOnlyWithBroadcastFlags()
    {
        var cmd = GvcpCmd.Discovery();
        Assert.Equal(new byte[] { 0x42, 0x11, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00 }, cmd.Packet.ToArray());
        Assert.Equal(GvcpConst.DiscoveryAck, cmd.ExpectedAck);
        Assert.Equal(0x01, GvcpCmd.Discovery(allowBroadcastAck: false).Flags);
    }

    [Fact]
    public void ForceIpLayoutIs56BytesWithFixedOffsets()
    {
        var mac = PhysicalAddress.Parse("00-11-22-33-44-55");
        var cmd = GvcpCmd.ForceIp(mac, IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0"), IPAddress.Parse("192.168.1.1"));
        Assert.Equal(GvcpConst.ForceIpCmd, cmd.Command);
        Assert.Equal(56, cmd.PayloadLength);
        var p = cmd.Payload;
        Assert.Equal(new byte[] { 0, 0, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, p.Slice(0, 8).ToArray());
        Assert.Equal(new byte[] { 192, 168, 1, 20 }, p.Slice(20, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 0 }, p.Slice(36, 4).ToArray());
        Assert.Equal(new byte[] { 192, 168, 1, 1 }, p.Slice(52, 4).ToArray());
        Assert.True(p.Slice(8, 12).ToArray().All(b => b == 0));

        GvcpPacket.ReadForceIp(p, out var m2, out var ip, out var sn, out var gw);
        Assert.Equal(mac, m2);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), ip);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), sn);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), gw);

        Assert.Throws<GevException>(() => GvcpCmd.ForceIp(mac, IPAddress.IPv6Loopback, sn, gw));
        Assert.Throws<GevException>(() => GvcpPacket.ReadForceIp(new byte[55], out _, out _, out _, out _));
    }

    // ---------------------------------------------------------------- PACKETRESEND

    [Fact]
    public void PacketResendStandardLayout()
    {
        Span<byte> buf = stackalloc byte[GvcpPacket.PacketResendMaxSize];
        var len = GvcpPacket.WritePacketResend(buf, 0x0102, blockId: 0xABCD, firstPacketId: 5, lastPacketId: 9, extendedIds: false, streamChannel: 0);
        Assert.Equal(20, len);
        Assert.Equal(new byte[] { 0x42, 0x00, 0x00, 0x40, 0x00, 0x0C, 0x01, 0x02 }, buf.Slice(0, 8).ToArray());
        Assert.Equal(new byte[] { 0x00, 0x00, 0xAB, 0xCD, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x09 }, buf.Slice(8, 12).ToArray());

        GvcpPacket.ReadPacketResend(buf.Slice(8, 12), false, out var ch, out var block, out var first, out var last);
        Assert.Equal(0, ch);
        Assert.Equal(0xABCDul, block);
        Assert.Equal(5u, first);
        Assert.Equal(9u, last);

        var cmd = GvcpCmd.PacketResend(1, 2, 3, extendedIds: false);
        Assert.False(cmd.IsAckRequired);
        Assert.Equal(20, cmd.Length);
    }

    [Fact]
    public void PacketResendExtendedLayout()
    {
        var buf = new byte[GvcpPacket.PacketResendMaxSize];
        var len = GvcpPacket.WritePacketResend(buf, 7, blockId: 0x0102030405060708, firstPacketId: 0x01000000, lastPacketId: 0x01000010, extendedIds: true, streamChannel: 1);
        Assert.Equal(28, len);
        Assert.Equal(0x10, buf[1]);
        Assert.Equal(20, BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4)));
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00 }, buf.AsSpan(8, 4).ToArray());
        Assert.Equal(0x01000000u, BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(12)));
        Assert.Equal(0x01000010u, BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16)));
        Assert.Equal(0x0102030405060708ul, BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(20)));

        GvcpPacket.ReadPacketResend(buf.AsSpan(8, 20), true, out var ch, out var block, out var first, out var last);
        Assert.Equal(1, ch);
        Assert.Equal(0x0102030405060708ul, block);
        Assert.Equal(0x01000000u, first);
        Assert.Equal(0x01000010u, last);
    }

    [Fact]
    public void PacketResendRejectsIdsThatDoNotFitStandardMode()
    {
        var buf = new byte[GvcpPacket.PacketResendMaxSize];
        Assert.Throws<GevException>(() => GvcpPacket.WritePacketResend(buf, 1, 0x10000, 1, 2, extendedIds: false));
        Assert.Throws<GevException>(() => GvcpPacket.WritePacketResend(buf, 1, 1, 1, 0x01000000, extendedIds: false));
        Assert.Throws<GevException>(() => GvcpPacket.WritePacketResend(buf, 1, 1, 5, 4, extendedIds: true));
        Assert.Throws<GevException>(() => GvcpPacket.ReadPacketResend(new byte[11], false, out _, out _, out _, out _));
    }

    // ---------------------------------------------------------------- ACK writers <-> GvcpAck

    [Fact]
    public void ReadRegAckRoundTrip()
    {
        var buf = new byte[64];
        var len = GvcpPacket.WriteReadRegAck(buf, 9, new uint[] { 0xDEADBEEF, 1 });
        Assert.Equal(16, len);
        var ack = GvcpAck.Parse(buf.AsSpan(0, len));
        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
        Assert.Equal(9, ack.ReqId);
        Assert.False(ack.IsError);
        Assert.Equal(2, ack.RegCount);
        Assert.Equal(0xDEADBEEFu, ack.GetRegValue(0));
        Assert.Equal(new uint[] { 0xDEADBEEF, 1 }, ack.GetRegValues());
        Assert.Throws<GevException>(() => ack.GetRegValue(2));
        Assert.Throws<GevException>(() => ack.MemAddress);
        Assert.Throws<GevException>(() => ack.WriteIndex);
    }

    [Fact]
    public void WriteAcksCarryIndex()
    {
        var buf = new byte[16];
        var len = GvcpPacket.WriteWriteRegAck(buf, 3, 2);
        Assert.Equal(12, len);
        var ack = GvcpAck.Parse(buf.AsSpan(0, len));
        Assert.Equal(2, ack.WriteIndex);
        Assert.True(ack.TryGetWriteIndex(out var idx) && idx == 2);

        len = GvcpPacket.WriteWriteMemAck(buf, 4, 512, GvcpConst.StatusWriteProtect);
        ack = GvcpAck.Parse(buf.AsSpan(0, len));
        Assert.True(ack.IsError);
        Assert.Equal(GvcpConst.WriteMemAck, ack.Command);
        Assert.Equal(512, ack.WriteIndex);

        var empty = new GvcpAck(0, GvcpConst.WriteRegAck, 1, Array.Empty<byte>());
        Assert.False(empty.TryGetWriteIndex(out _));
        Assert.Throws<GevException>(() => empty.WriteIndex);
    }

    [Fact]
    public void ReadMemAckRoundTrip()
    {
        var data = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var buf = new byte[64];
        var len = GvcpPacket.WriteReadMemAck(buf, 11, 0x0200, data);
        Assert.Equal(8 + 4 + 16, len);
        var ack = GvcpAck.Parse(buf.AsSpan(0, len));
        Assert.Equal(0x0200u, ack.MemAddress);
        Assert.Equal(data, ack.MemData.ToArray());

        var tooShort = new GvcpAck(0, GvcpConst.ReadMemAck, 1, new byte[2]);
        Assert.Throws<GevException>(() => tooShort.MemAddress);
        Assert.Throws<GevException>(() => GvcpPacket.WriteReadMemAck(new byte[600], 1, 0, new byte[516]));
    }

    [Fact]
    public void PendingAckCarriesTimeToCompletion()
    {
        var buf = new byte[12];
        var len = GvcpPacket.WritePendingAck(buf, 5, 750);
        Assert.Equal(12, len);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x89, 0x00, 0x04, 0x00, 0x05, 0x00, 0x00, 0x02, 0xEE }, buf);
        var ack = GvcpAck.Parse(buf);
        Assert.Equal(750, ack.PendingAckTimeMs);
        Assert.Equal("PENDING_ACK", ack.Name);
    }

    [Fact]
    public void DiscoveryAndForceIpAcks()
    {
        var bootstrap = new byte[GvbsAddr.DiscoveryDataLen];
        bootstrap[3] = 2;
        var buf = new byte[300];
        var len = GvcpPacket.WriteDiscoveryAck(buf, 1, bootstrap);
        Assert.Equal(8 + 248, len);
        var ack = GvcpAck.Parse(buf.AsSpan(0, len));
        Assert.Equal(248, ack.DiscoveryData.Length);
        Assert.Equal(2, ack.DiscoveryData[3]);
        Assert.Throws<GevException>(() => GvcpPacket.WriteDiscoveryAck(buf, 1, new byte[247]));

        var shortAck = new GvcpAck(0, GvcpConst.DiscoveryAck, 1, new byte[100]);
        Assert.Throws<GevException>(() => shortAck.DiscoveryData.Length);

        len = GvcpPacket.WriteForceIpAck(buf, 2);
        Assert.Equal(8, len);
        Assert.Equal(GvcpConst.ForceIpAck, GvcpAck.Parse(buf.AsSpan(0, len)).Command);
    }

    [Fact]
    public void AckParseRejectsTruncatedPayload()
    {
        var buf = new byte[64];
        var len = GvcpPacket.WriteReadRegAck(buf, 9, new uint[] { 1, 2 });
        Assert.Throws<GevException>(() => GvcpAck.Parse(buf.AsSpan(0, len - 1)));
    }

    [Fact]
    public void RawCmdAndCommandNames()
    {
        var cmd = GvcpCmd.Raw(GvcpConst.EventAck, 0, new byte[] { 1, 2, 3, 4 }, 0);
        Assert.Equal(GvcpConst.EventAck, cmd.Command);
        Assert.Equal("EVENT", cmd.Name);
        Assert.Equal("CMD_0x0777", GvcpPacket.CommandName(0x0777));
        Assert.Throws<GevException>(() => GvcpCmd.Raw(1, 1, new byte[541], 2));
    }

    [Fact]
    public void ReqIdSkipsZeroAndWraps()
    {
        var counter = 0;
        Assert.Equal(1, GvcpChannel.NextReqId(ref counter));
        counter = 65534;
        Assert.Equal(65535, GvcpChannel.NextReqId(ref counter));
        Assert.Equal(1, GvcpChannel.NextReqId(ref counter));
        counter = int.MaxValue - 1;
        var a = GvcpChannel.NextReqId(ref counter);
        var b = GvcpChannel.NextReqId(ref counter);
        Assert.NotEqual(0, a);
        Assert.NotEqual(0, b);
    }
}
