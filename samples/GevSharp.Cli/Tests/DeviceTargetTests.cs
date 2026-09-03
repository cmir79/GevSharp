using System.Net;
using GevSharp.Cli.Commands;
using GevSharp.Gvcp;

namespace GevSharp.Tests.Cli;

/// <summary>&lt;ip[:port]&gt; 인자 해석 — 표준 포트 기본값, 포트 범위, IPv4 한정.</summary>
public class DeviceTargetTests
{
    [Fact]
    public void PlainAddressUsesTheStandardPort()
    {
        var target = DeviceTarget.Parse("192.168.1.10");

        Assert.Equal(IPAddress.Parse("192.168.1.10"), target.Address);
        Assert.Equal(GvcpConst.Port, target.Port);
        Assert.True(target.IsStandardPort);
        Assert.Equal("192.168.1.10", target.ToString());
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.10"), GvcpConst.Port), target.EndPoint);
    }

    [Fact]
    public void PortSuffixIsHonoured()
    {
        var target = DeviceTarget.Parse(" 127.0.0.1:4000 ");

        Assert.Equal(IPAddress.Loopback, target.Address);
        Assert.Equal(4000, target.Port);
        Assert.False(target.IsStandardPort);
        Assert.Equal("127.0.0.1:4000", target.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("::1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:65536")]
    [InlineData("127.0.0.1:abc")]
    [InlineData("127.0.0.1:")]
    public void InvalidTargetsAreUsageErrors(string text)
    {
        Assert.Throws<CliUsageException>(() => DeviceTarget.Parse(text));
    }
}
