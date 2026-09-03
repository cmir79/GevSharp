using GevSharp.GenApi;
using GevSharp.GenApi.Model;

namespace GevSharp.Tests.GenApi.Model;

/// <summary>minimal.xml — 루트 카테고리 + IntReg + pValue Integer + Port.</summary>
public class GenApiXmlParserMinimalTests
{
    private static GenApiXmlModel Model => GenApiFixtures.Minimal;

    [Fact]
    public void ParsesFourNodesWithoutWarnings()
    {
        Assert.Equal(4, Model.Nodes.Count);
        Assert.Equal(4, Model.NodeList.Count);
        Assert.Empty(Model.Warnings);
        Assert.Equal(new[] { "Root", "Width", "WidthReg", "Device" }, Model.NodeList.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void RootAttributesRoundTrip()
    {
        var info = Model.Info;
        Assert.Equal("GevSharpMinimal", info.ModelName);
        Assert.Equal("GevSharp", info.VendorName);
        Assert.Equal("Minimal hand-written GenApi fixture", info.ToolTip);
        Assert.Equal("GEV", info.StandardNameSpace);
        Assert.Equal(1, info.SchemaMajorVersion);
        Assert.Equal(1, info.SchemaMinorVersion);
        Assert.Equal(0, info.SchemaSubMinorVersion);
        Assert.Equal(1, info.MajorVersion);
        Assert.Equal(2, info.MinorVersion);
        Assert.Equal(3, info.SubMinorVersion);
        Assert.Equal("0f0e0d0c-0b0a-4908-8706-050403020100", info.ProductGuid);
        Assert.Equal("1f1e1d1c-1b1a-4918-8716-151413121110", info.VersionGuid);
    }

    [Fact]
    public void RootCategoryListsFeatures()
    {
        var root = Model.Get<CategoryDef>("Root");
        Assert.Equal(NodeDefKind.Category, root.Kind);
        Assert.Equal(NodeKind.Category, root.InterfaceKind);
        Assert.Equal(NodeNameSpace.Standard, root.NameSpace);
        Assert.Equal("Feature tree entry", root.ToolTip);
        Assert.Equal(new[] { "Width" }, root.PFeatures);
    }

    [Fact]
    public void IntegerWithPValueRoundTrips()
    {
        var width = Model.Get<IntegerDef>("Width");
        Assert.Equal(NodeKind.Integer, width.InterfaceKind);
        Assert.Equal(NodeNameSpace.Standard, width.NameSpace);
        Assert.Equal("Image width", width.ToolTip);
        Assert.Equal("Width of the image in pixels.", width.Description);
        Assert.Equal("Width", width.DisplayName);
        Assert.Equal(Visibility.Beginner, width.Visibility);
        Assert.True(width.IsStreamable);
        Assert.Null(width.Value);
        Assert.Equal("WidthReg", width.PValue);
        Assert.Equal(1L, width.Min);
        Assert.Equal(4096L, width.Max);
        Assert.Equal(1L, width.Inc);
        Assert.Null(width.PMin);
        Assert.Null(width.PMax);
        Assert.Null(width.PInc);
        Assert.Equal(Representation.Linear, width.Representation);
        Assert.Null(width.Unit);
        Assert.Empty(width.PValueCopies);
        Assert.Empty(width.PInvalidators);
        Assert.Empty(width.PSelected);
    }

    [Fact]
    public void IntRegRoundTrips()
    {
        var reg = Model.Get<IntRegDef>("WidthReg");
        Assert.Equal(NodeKind.Integer, reg.InterfaceKind);
        Assert.Equal(NodeNameSpace.Custom, reg.NameSpace);
        var rs = reg.RegisterSet;
        Assert.True(rs.HasStaticAddress);
        Assert.Equal(0x10000L, rs.StaticAddress);
        Assert.Equal(new[] { 0x10000L }, rs.Addresses);
        Assert.Equal(4L, rs.Length);
        Assert.Null(rs.PLength);
        Assert.Equal(AccessMode.ReadWrite, rs.AccessMode);
        Assert.Equal("Device", rs.PPort);
        Assert.Equal(Cachable.WriteThrough, rs.Cachable);
        Assert.Equal(Sign.Unsigned, reg.Sign);
        Assert.Equal(Endianess.BigEndian, reg.Endianess);
        Assert.Null(reg.PollingTimeMs);
        Assert.Null(reg.ImposedAccessMode);
    }

    [Fact]
    public void PortRoundTrips()
    {
        var port = Model.Get<PortDef>("Device");
        Assert.Equal(NodeKind.Port, port.InterfaceKind);
        Assert.Equal("Device register space", port.ToolTip);
        Assert.Null(port.ChunkId);
        Assert.Null(port.PChunkId);
        Assert.False(port.IsEndianessSwapped);
        Assert.False(port.IsChunkDataCached);
        Assert.Null(port.EventId);
    }

    [Fact]
    public void LookupsBehave()
    {
        Assert.Null(Model.Find("Nope"));
        var ex = Assert.Throws<GenApiException>(() => Model.Get("Nope"));
        Assert.Equal("Nope", ex.NodeName);
        var mismatch = Assert.Throws<GenApiException>(() => Model.Get<FloatDef>("Width"));
        Assert.Equal("Width", mismatch.NodeName);
        Assert.Contains("Integer", mismatch.Message);
        Assert.Same(Model.Nodes["Width"], Model.Get("Width"));
    }
}
