using GevSharp.Gvcp;
using GevSharp.Gvsp;

namespace GevSharp.Tests;

public class SkeletonTests
{
    [Fact]
    public void StreamChannelAddrFollowsStride()
    {
        Assert.Equal(0x0D04u, GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset));
        Assert.Equal(0x0D44u, GvbsAddr.StreamChannel(1, GvbsAddr.ScpsOffset));
    }

    [Fact]
    public void DataBytesPerPacketSubtractsHeaders()
    {
        Assert.Equal(1500 - 28 - 8, GvspConst.DataBytesPerPacket(1500, extendedIds: false));
        Assert.Equal(9000 - 28 - 20, GvspConst.DataBytesPerPacket(9000, extendedIds: true));
    }

    [Fact]
    public void StatusExceptionCarriesName()
    {
        var ex = new GevStatusException("READREG", GvcpConst.StatusAccessDenied);
        Assert.Contains("ACCESS_DENIED", ex.Message);
        Assert.Equal(GvcpConst.StatusAccessDenied, ex.Status);
    }
}
