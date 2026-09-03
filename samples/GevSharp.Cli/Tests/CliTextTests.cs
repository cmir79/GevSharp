using System.Net;
using System.Net.NetworkInformation;
using GevSharp.Cli.Commands;
using GevSharp.GenApi;
using GevSharp.Gvcp;

namespace GevSharp.Tests.Cli;

/// <summary>표·주소·비트 필드·노드 값의 문자열 변환과 set 명령의 값 해석.</summary>
public class CliTextTests
{
    [Fact]
    public void TextTableAlignsColumnsWithoutTrailingSpaces()
    {
        var table = new TextTable("IP", "Model");
        table.AddRow("10.0.0.1", "SimCamera");
        table.AddRow("192.168.100.200", null);
        var w = new StringWriter();

        table.Write(w);

        var lines = w.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        Assert.Equal("IP               Model", lines[0]);
        Assert.Equal("---------------  ---------", lines[1]);
        Assert.Equal("10.0.0.1         SimCamera", lines[2]);
        Assert.Equal("192.168.100.200", lines[3]);   // 빈 마지막 셀 — 줄 끝 공백이 남지 않는다
        Assert.All(lines, l => Assert.Equal(l.TrimEnd(), l));
        Assert.Equal(2, table.RowCount);
        Assert.Throws<ArgumentException>(() => table.AddRow("only-one"));
    }

    [Fact]
    public void MacAndIpv4Formatting()
    {
        Assert.Equal("02:47:45:56:00:0A", NetText.Mac(new PhysicalAddress(new byte[] { 0x02, 0x47, 0x45, 0x56, 0x00, 0x0A })));
        Assert.Equal(IPAddress.Parse("192.168.1.1"), NetText.Ipv4(0xC0A80101));
        Assert.Equal("(empty)", NetText.Text(string.Empty));
        Assert.Equal("(empty)", NetText.Text(null));
        Assert.Equal("x", NetText.Text("x"));
    }

    [Fact]
    public void BootstrapBitFieldsAreNamed()
    {
        Assert.Equal("none", NetText.GvcpCap(0));
        var cap = NetText.GvcpCap(GvbsAddr.GvcpCapPacketResend | GvbsAddr.GvcpCapHeartbeatDisable | (1u << 10));
        Assert.Contains("packet-resend", cap);
        Assert.Contains("heartbeat-disable", cap);
        Assert.Contains("bit10", cap);

        Assert.Equal("open", NetText.Ccp(0));
        Assert.Equal("control", NetText.Ccp(GvbsAddr.CcpControl));
        Assert.Equal("exclusive, control, switchover-enable", NetText.Ccp(GvbsAddr.CcpExclusive | GvbsAddr.CcpControl | GvbsAddr.CcpSwitchoverEnable));

        Assert.Equal("1500 bytes (flags: fire-test, do-not-fragment)", NetText.Scps(GvbsAddr.ScpsFireTest | GvbsAddr.ScpsDoNotFragment | 1500));
        Assert.Equal("9000 bytes (flags: none)", NetText.Scps(9000));

        Assert.Equal("persistent, DHCP, LLA", NetText.IpCfg(GvbsAddr.IpCfgPersistent | GvbsAddr.IpCfgDhcp | GvbsAddr.IpCfgLla));
        Assert.Equal("none", NetText.IpCfg(0));

        Assert.Equal("UTF-8", NetText.CharacterSet(GevDeviceInfo.CharacterSetUtf8));
        Assert.Equal("ASCII", NetText.CharacterSet(GevDeviceInfo.CharacterSetAscii));
        Assert.Equal("unspecified", NetText.CharacterSet(0));
    }

    [Fact]
    public void IntegerFormattingFollowsRepresentation()
    {
        Assert.Equal("42", NodeText.FormatInteger(42, Representation.Linear, null));
        Assert.Equal("42 us", NodeText.FormatInteger(42, Representation.Linear, "us"));
        Assert.Equal("0xFF", NodeText.FormatInteger(255, Representation.HexNumber, null));
        Assert.Equal("192.168.1.1", NodeText.FormatInteger(0xC0A80101, Representation.IPV4Address, null));
        Assert.Equal("02:47:45:56:00:0A", NodeText.FormatInteger(0x02474556000A, Representation.MACAddress, null));
        Assert.Equal("1.5 dB", NodeText.FormatFloat(1.5, "dB"));
        Assert.Equal("\"a\\\"b\"", NodeText.Quote("a\"b"));
    }

    [Fact]
    public void AccessTagsAndReadability()
    {
        Assert.Equal("RW", NodeText.AccessTag(AccessMode.ReadWrite));
        Assert.Equal("RO", NodeText.AccessTag(AccessMode.ReadOnly));
        Assert.Equal("WO", NodeText.AccessTag(AccessMode.WriteOnly));
        Assert.Equal("NA", NodeText.AccessTag(AccessMode.NotAvailable));
        Assert.Equal("NI", NodeText.AccessTag(AccessMode.NotImplemented));
        Assert.True(NodeText.IsReadable(AccessMode.ReadOnly));
        Assert.False(NodeText.IsReadable(AccessMode.WriteOnly));
        Assert.False(NodeText.IsReadable(AccessMode.NotAvailable));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("On", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("FALSE", false)]
    [InlineData("off", false)]
    [InlineData("no", false)]
    public void BooleanValuesAcceptCommonSpellings(string text, bool expected)
    {
        Assert.Equal(expected, NodeText.ParseBool(text));
    }

    [Fact]
    public void BooleanValuesRejectOtherText()
    {
        Assert.Throws<CliUsageException>(() => NodeText.ParseBool("maybe"));
    }

    [Fact]
    public void RegisterValuesAreHexBytes()
    {
        Assert.Equal(new byte[] { 0x0A, 0x0B, 0xFF }, NodeText.ParseHexBytes("0A0BFF"));
        Assert.Equal(new byte[] { 0x0A, 0x0B }, NodeText.ParseHexBytes("0x0a:0b"));
        Assert.Equal(new byte[] { 0x01, 0x02 }, NodeText.ParseHexBytes("01 02"));
        Assert.Throws<CliUsageException>(() => NodeText.ParseHexBytes("ABC"));
        Assert.Throws<CliUsageException>(() => NodeText.ParseHexBytes("ZZ"));
        Assert.Throws<CliUsageException>(() => NodeText.ParseHexBytes(""));
    }
}
