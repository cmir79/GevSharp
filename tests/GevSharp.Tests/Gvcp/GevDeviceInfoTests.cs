using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using GevSharp.Gvcp;

namespace GevSharp.Tests.Gvcp;

public class GevDeviceInfoTests
{
    internal static byte[] BuildBootstrap(
        string manufacturer = "Maker", string model = "Cam", string version = "2.1", string info = "hand-made",
        string serial = "ABC123", string userName = "line1", uint deviceMode = 0x8000_0001)
    {
        var b = new byte[GvbsAddr.DiscoveryDataLen];
        W(b, GvbsAddr.Version, 0x0002_0001);
        W(b, GvbsAddr.DeviceMode, deviceMode);
        W(b, GvbsAddr.MacHigh, 0x0000_0A1B);
        W(b, GvbsAddr.MacLow, 0x2C3D_4E5F);
        W(b, GvbsAddr.SupportedIpCfg, 0x8000_0007);
        W(b, GvbsAddr.CurrentIpCfg, 0x0000_0005);
        W(b, GvbsAddr.CurrentIp, 0xC0A8_0164);
        W(b, GvbsAddr.CurrentSubnet, 0xFFFF_FF00);
        W(b, GvbsAddr.CurrentGateway, 0xC0A8_0101);
        S(b, GvbsAddr.ManufacturerName, manufacturer);
        S(b, GvbsAddr.ModelName, model);
        S(b, GvbsAddr.DeviceVersion, version);
        S(b, GvbsAddr.ManufacturerInfo, info);
        S(b, GvbsAddr.SerialNumber, serial);
        S(b, GvbsAddr.UserDefinedName, userName);
        return b;

        static void W(byte[] buf, uint addr, uint v) => BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan((int)addr), v);
        static void S(byte[] buf, uint addr, string s) => Encoding.UTF8.GetBytes(s).CopyTo(buf, (int)addr);
    }

    [Fact]
    public void ParsesEveryFieldByGvbsOffset()
    {
        var iface = IPAddress.Parse("192.168.1.10");
        var info = GevDeviceInfo.ParseDiscoveryAck(BuildBootstrap(), iface);

        Assert.Equal(PhysicalAddress.Parse("0A-1B-2C-3D-4E-5F"), info.Mac);
        Assert.Equal(IPAddress.Parse("192.168.1.100"), info.Address);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), info.Subnet);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), info.Gateway);
        Assert.Equal(iface, info.InterfaceAddress);
        Assert.Equal(2, info.SpecMajor);
        Assert.Equal(1, info.SpecMinor);
        Assert.Equal(0x8000_0001u, info.DeviceMode);
        Assert.True(info.IsBigEndianDevice);
        Assert.Equal(1, info.CharacterSet);
        Assert.Equal(0x8000_0007u, info.SupportedIpCfg);
        Assert.Equal(5u, info.CurrentIpCfg);
        Assert.Equal("Maker", info.Manufacturer);
        Assert.Equal("Cam", info.Model);
        Assert.Equal("2.1", info.DeviceVersion);
        Assert.Equal("hand-made", info.ManufacturerInfo);
        Assert.Equal("ABC123", info.SerialNumber);
        Assert.Equal("line1", info.UserDefinedName);
        Assert.True(info.IsReachableDirectly);
        Assert.Contains("Maker Cam", info.ToString());
    }

    [Fact]
    public void TruncatedPayloadIsRejected()
    {
        var full = BuildBootstrap();
        Assert.Throws<GevException>(() => GevDeviceInfo.ParseDiscoveryAck(full.AsSpan(0, 247), IPAddress.Loopback));
        Assert.Throws<GevException>(() => GevDeviceInfo.ParseDiscoveryAck(ReadOnlySpan<byte>.Empty, IPAddress.Loopback));
        Assert.NotNull(GevDeviceInfo.ParseDiscoveryAck(full, IPAddress.Loopback));
        // 더 긴 페이로드는 앞 248 바이트만 쓴다.
        var longer = new byte[300];
        full.CopyTo(longer, 0);
        Assert.Equal("Cam", GevDeviceInfo.ParseDiscoveryAck(longer, IPAddress.Loopback).Model);
    }

    [Fact]
    public void StringsAreNulTerminatedUtf8AndFullLengthWithoutNul()
    {
        var b = BuildBootstrap(userName: "1234567890123456", model: "Kamera-ü");
        var info = GevDeviceInfo.ParseDiscoveryAck(b, IPAddress.Loopback);
        Assert.Equal("1234567890123456", info.UserDefinedName);
        Assert.Equal("Kamera-ü", info.Model);

        var noSerial = BuildBootstrap(serial: "");
        Assert.Equal(string.Empty, GevDeviceInfo.ParseDiscoveryAck(noSerial, IPAddress.Loopback).SerialNumber);
    }

    [Fact]
    public void StringsKeepPaddingAndFollowTheDeclaredCharacterSet()
    {
        // 앞뒤 공백은 장치가 보낸 값의 일부다 — 다듬지 않아야 레지스터에 쓴 값과 대조된다.
        var padded = GevDeviceInfo.ParseDiscoveryAck(BuildBootstrap(userName: "  cam 7 ", model: " X "), IPAddress.Loopback);
        Assert.Equal("  cam 7 ", padded.UserDefinedName);
        Assert.Equal(" X ", padded.Model);

        var ascii = GevDeviceInfo.ParseDiscoveryAck(BuildBootstrap(model: "Kamera-ü", deviceMode: 0x8000_0002), IPAddress.Loopback);
        Assert.Equal(GevDeviceInfo.CharacterSetAscii, ascii.CharacterSet);
        Assert.Equal("Kamera-??", ascii.Model);

        var unspecified = GevDeviceInfo.ParseDiscoveryAck(BuildBootstrap(model: "Kamera-ü", deviceMode: 0x8000_0000), IPAddress.Loopback);
        Assert.Equal(0, unspecified.CharacterSet);
        Assert.Equal("Kamera-ü", unspecified.Model);
    }

    [Fact]
    public void LittleEndianDeviceModeFlag()
    {
        var info = GevDeviceInfo.ParseDiscoveryAck(BuildBootstrap(deviceMode: 0x0000_0002), IPAddress.Loopback);
        Assert.False(info.IsBigEndianDevice);
        Assert.Equal(2, info.CharacterSet);
    }

    [Fact]
    public void NotReachableDirectlyWhenInterfaceIsOnAnotherSubnet()
    {
        var info = GevDeviceInfo.ParseDiscoveryAck(BuildBootstrap(), IPAddress.Parse("10.0.0.5"));
        Assert.False(info.IsReachableDirectly);
    }
}
