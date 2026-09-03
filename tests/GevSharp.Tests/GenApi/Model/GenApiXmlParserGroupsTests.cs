using GevSharp.GenApi;
using GevSharp.GenApi.Model;

namespace GevSharp.Tests.GenApi.Model;

/// <summary>groups.xml — 3단 중첩 Group, StructReg 3항목, 모든 노드 종류. 종류별로 모든 필드가 정의에 실리는지 본다.</summary>
public class GenApiXmlParserGroupsTests
{
    private static GenApiXmlModel Model => GenApiFixtures.Groups;

    // ---- 전체 ----

    [Fact]
    public void ParsesWithoutWarnings()
    {
        Assert.Empty(Model.Warnings);
        Assert.Equal("GevSharpGroups", Model.Info.ModelName);
        Assert.Equal(2, Model.Info.MajorVersion);
        Assert.Equal(1, Model.Info.SubMinorVersion);
    }

    [Fact]
    public void NodeCountsPerKind()
    {
        Assert.Equal(98, Model.Nodes.Count);
        Assert.Equal(98, Model.NodeList.Count);

        var counts = Model.NodeList.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(8, counts[NodeDefKind.Category]);
        Assert.Equal(13, counts[NodeDefKind.Integer]);
        Assert.Equal(20, counts[NodeDefKind.IntReg]);
        Assert.Equal(8, counts[NodeDefKind.MaskedIntReg]);
        Assert.Equal(6, counts[NodeDefKind.IntSwissKnife]);   // 5 top-level + the named inline LUTValueAddr
        Assert.Equal(1, counts[NodeDefKind.IntConverter]);
        Assert.Equal(3, counts[NodeDefKind.Float]);
        Assert.Equal(2, counts[NodeDefKind.FloatReg]);
        Assert.Equal(1, counts[NodeDefKind.SwissKnife]);
        Assert.Equal(2, counts[NodeDefKind.Converter]);
        Assert.Equal(1, counts[NodeDefKind.String]);
        Assert.Equal(2, counts[NodeDefKind.StringReg]);
        Assert.Equal(4, counts[NodeDefKind.Boolean]);
        Assert.Equal(6, counts[NodeDefKind.Enumeration]);
        Assert.Equal(14, counts[NodeDefKind.EnumEntry]);
        Assert.Equal(3, counts[NodeDefKind.Command]);
        Assert.Equal(1, counts[NodeDefKind.Register]);
        Assert.Equal(2, counts[NodeDefKind.Port]);
        Assert.Equal(1, counts[NodeDefKind.Node]);
        Assert.False(counts.ContainsKey(NodeDefKind.Unknown));
    }

    [Fact]
    public void EveryKnownKindAppearsAtLeastOnce()
    {
        var present = Model.NodeList.Select(n => n.Kind).Distinct().ToHashSet();
        foreach (NodeDefKind kind in Enum.GetValues(typeof(NodeDefKind)))
        {
            if (kind == NodeDefKind.Unknown) continue;
            Assert.Contains(kind, present);
        }
    }

    [Fact]
    public void NodesInsideNestedGroupsAreTopLevel()
    {
        Assert.IsType<CategoryDef>(Model.Get("DeviceControl"));                 // Group depth 1
        Assert.IsType<StringRegDef>(Model.Get("DeviceModelName"));              // Group depth 2
        Assert.IsType<StringDef>(Model.Get("DeviceFirmwareVersion"));           // Group depth 3
        Assert.IsType<IntegerDef>(Model.Get("DeviceSFNCVersionMajor"));         // Group depth 3
        Assert.IsType<FloatDef>(Model.Get("DeviceTemperature"));                // back at depth 1 after the nested groups
        Assert.IsType<CategoryDef>(Model.Get("Root"));                          // outside any group
    }

    [Fact]
    public void EnumEntriesFollowTheirEnumerationInDocumentOrder()
    {
        var names = Model.NodeList.Select(n => n.Name).ToList();
        var i = names.IndexOf("PixelFormat");
        Assert.Equal("EnumEntry_PixelFormat_Mono8", names[i + 1]);
        Assert.Equal("EnumEntry_PixelFormat_Mono10", names[i + 2]);
        Assert.Equal("EnumEntry_PixelFormat_BayerRG8", names[i + 3]);
        Assert.Equal("PixelFormatReg", names[i + 4]);
    }

    // ---- StructReg ----

    [Fact]
    public void StructRegExpandsToOneMaskedIntRegPerEntry()
    {
        var fire = Model.Get<MaskedIntRegDef>("GevSCPSFireTestPacket");
        var dnf = Model.Get<MaskedIntRegDef>("GevSCPSDoNotFragment");
        var size = Model.Get<MaskedIntRegDef>("GevSCPSPacketSize");

        foreach (var e in new[] { fire, dnf, size })
        {
            Assert.True(e.IsStructEntry);
            Assert.Equal(0, e.StructRegIndex);
            Assert.Equal(0x0D04L, e.RegisterSet.StaticAddress);
            Assert.Equal(4L, e.RegisterSet.Length);
            Assert.Equal("Device", e.RegisterSet.PPort);
            Assert.Equal(AccessMode.ReadWrite, e.RegisterSet.AccessMode);
            Assert.Equal(Endianess.BigEndian, e.Endianess);
            Assert.Equal(Sign.Unsigned, e.Sign);
            Assert.Equal("Stream channel packet size register", e.Comment);
            Assert.Equal(NodeNameSpace.Standard, e.NameSpace);
        }

        Assert.Equal(31, fire.Bit);
        Assert.Equal(31, fire.Lsb);
        Assert.Equal(31, fire.Msb);
        Assert.Equal(Visibility.Guru, fire.Visibility);                          // own Visibility overrides the StructReg's Expert
        Assert.Equal("TLParamsLocked", fire.PIsLocked);                          // inherited from the StructReg
        Assert.Equal(Cachable.WriteThrough, fire.RegisterSet.Cachable);
        Assert.Equal(new[] { "DeviceReset" }, fire.PInvalidators);            // inherited from the StructReg

        Assert.Equal(30, dnf.Bit);
        Assert.Equal("Sets the do-not-fragment bit of stream packets", dnf.ToolTip);
        Assert.Equal(Visibility.Expert, dnf.Visibility);                         // inherited from the StructReg
        Assert.Equal("TLParamsLocked", dnf.PIsLocked);

        Assert.Null(size.Bit);
        Assert.Equal(0, size.Lsb);
        Assert.Equal(15, size.Msb);
        Assert.Equal(Cachable.NoCache, size.RegisterSet.Cachable);            // entry override
        Assert.Equal(new[] { "DeviceReset", "AcquisitionStart" }, size.PInvalidators);   // struct first, then own
        Assert.Equal("TLParamsLocked", size.PIsLocked);
        Assert.True(size.IsStreamable);
        Assert.Equal("B", size.Unit);
        Assert.Equal(Representation.Linear, size.Representation);
        Assert.Equal(Visibility.Expert, size.Visibility);
    }

    // ---- Category / Node ----

    [Fact]
    public void CategoriesKeepFeatureOrder()
    {
        Assert.Equal(7, Model.Get<CategoryDef>("Root").PFeatures.Count);
        var dev = Model.Get<CategoryDef>("DeviceControl");
        Assert.Equal("DeviceVendorName", dev.PFeatures[0]);
        Assert.Equal("DeviceLastError", dev.PFeatures[9]);
        Assert.Equal("Device information and control", dev.ToolTip);
    }

    [Fact]
    public void GenericNodeRoundTrips()
    {
        var n = Model.Get<GenericNodeDef>("DeviceMetaData");
        Assert.Equal(NodeDefKind.Node, n.Kind);
        Assert.Equal(NodeKind.Unknown, n.InterfaceKind);
        Assert.Equal(Visibility.Invisible, n.Visibility);
        Assert.Equal("Placeholder node without a value", n.ToolTip);
    }

    // ---- Integer 계열 ----

    [Fact]
    public void IntegerWidthRoundTrips()
    {
        var w = Model.Get<IntegerDef>("Width");
        Assert.Equal("WidthReg", w.PValue);
        Assert.Equal("WidthMinCalc", w.PMin);
        Assert.Equal("WidthMaxCalc", w.PMax);
        Assert.Equal("WidthIncCalc", w.PInc);
        Assert.Null(w.Min);
        Assert.Null(w.Max);
        Assert.Null(w.Inc);
        Assert.Equal(new[] { "OffsetX", "BinningHorizontal" }, w.PInvalidators);
        Assert.Equal(new[] { "DeviceLastError" }, w.PErrors);
        Assert.True(w.IsStreamable);
        Assert.Equal(Representation.Linear, w.Representation);
        Assert.Equal("Width of the image provided by the device in pixels.", w.Description);
        Assert.Equal("Width", w.DisplayName);
    }

    [Fact]
    public void IntegerVariantsRoundTrip()
    {
        var ox = Model.Get<IntegerDef>("OffsetX");
        Assert.Equal(new[] { "OffsetXShadowReg" }, ox.PValueCopies);
        Assert.Equal(0L, ox.Min);
        Assert.Equal(4096L, ox.Max);
        Assert.Equal(4L, ox.Inc);
        Assert.Equal("px", ox.Unit);

        var bin = Model.Get<IntegerDef>("BinningHorizontal");
        Assert.Equal(new long[] { 1, 2, 4 }, bin.ValidValueSet);

        var baseAddr = Model.Get<IntegerDef>("AnalogBase");
        Assert.Equal(0x40000L, baseAddr.Value);
        Assert.Null(baseAddr.PValue);
        Assert.Equal(Visibility.Invisible, baseAddr.Visibility);

        var lutIndex = Model.Get<IntegerDef>("LUTIndex");
        Assert.Equal(new[] { "LUTValue" }, lutIndex.PSelected);

        var gainRaw = Model.Get<IntegerDef>("GainRaw");
        Assert.True(gainRaw.IsDeprecated);
        Assert.Equal("Gain", gainRaw.PAlias);
        Assert.Equal("Gain", gainRaw.PCastAlias);
        Assert.Equal(Visibility.Guru, gainRaw.Visibility);

        var sfnc = Model.Get<IntegerDef>("DeviceSFNCVersionMajor");
        Assert.Equal(2L, sfnc.Value);
        Assert.Equal(Representation.PureNumber, sfnc.Representation);
        Assert.Equal(Visibility.Expert, sfnc.Visibility);
    }

    [Fact]
    public void IntRegVariantsRoundTrip()
    {
        var err = Model.Get<IntRegDef>("DeviceErrorCode");
        Assert.Equal(Sign.Signed, err.Sign);
        Assert.Equal(Representation.HexNumber, err.Representation);
        Assert.Equal(Cachable.NoCache, err.RegisterSet.Cachable);
        Assert.Equal(AccessMode.ReadOnly, err.RegisterSet.AccessMode);
        Assert.Equal(0x0F14L, err.RegisterSet.StaticAddress);

        var gainReg = Model.Get<IntRegDef>("GainRawReg");
        Assert.False(gainReg.RegisterSet.HasStaticAddress);
        Assert.Equal(new[] { 0x40010L }, gainReg.RegisterSet.Addresses);
        var idx = Assert.Single(gainReg.RegisterSet.PIndexes);
        Assert.Equal("GainSelectorReg", idx.PNode);
        Assert.Equal(4L, idx.Offset);
        Assert.Null(idx.POffset);
        Assert.Equal(Sign.Signed, gainReg.Sign);

        var black = Model.Get<IntRegDef>("BlackLevelReg");
        Assert.Equal(new[] { "AnalogBase" }, black.RegisterSet.PAddresses);
        Assert.Equal(new[] { 0x100L }, black.RegisterSet.Addresses);
        Assert.False(black.RegisterSet.HasStaticAddress);

        var evt = Model.Get<IntRegDef>("EventExposureEndTimestamp");
        Assert.Equal("9002", evt.EventId);
        Assert.Equal(0x9002UL, evt.EventIdValue);   // hexadecimal, not 9002 decimal
        Assert.Equal(8L, evt.RegisterSet.Length);
        Assert.Equal(Visibility.Expert, evt.Visibility);
        Assert.Equal(NodeNameSpace.Standard, evt.NameSpace);

        var wo = Model.Get<IntRegDef>("AcquisitionStartReg");
        Assert.Equal(AccessMode.WriteOnly, wo.RegisterSet.AccessMode);
    }

    [Fact]
    public void NamedInlineAddressSwissKnifeIsNestedAndRegistered()
    {
        var reg = Model.Get<IntRegDef>("LUTValueReg");
        Assert.False(reg.RegisterSet.HasStaticAddress);
        Assert.Equal(new[] { 0x60100L }, reg.RegisterSet.Addresses);
        var sk = Assert.Single(reg.RegisterSet.AddressSwissKnives);
        Assert.Equal("LUTValueAddr", sk.Name);
        Assert.Equal(new[] { new FormulaVariableDef("IDX", "LUTIndex") }, sk.Variables);
        Assert.Equal("IDX * 4", sk.Formula);
        // 이름이 있으므로 다른 노드가 가리킬 수 있게 같은 인스턴스가 Nodes 에도 있고, 문서 순서상 소유 레지스터 바로 뒤다.
        Assert.Same(sk, Model.Find("LUTValueAddr"));
        var names = Model.NodeList.Select(n => n.Name).ToList();
        Assert.Equal(names.IndexOf("LUTValueReg") + 1, names.IndexOf("LUTValueAddr"));
    }

    [Fact]
    public void MaskedIntRegRoundTrips()
    {
        var pf = Model.Get<MaskedIntRegDef>("PixelFormatReg");
        Assert.Null(pf.Bit);
        Assert.Equal(0, pf.Lsb);
        Assert.Equal(7, pf.Msb);
        Assert.Equal(Sign.Unsigned, pf.Sign);
        Assert.Equal(Endianess.BigEndian, pf.Endianess);
        Assert.False(pf.IsStructEntry);
        Assert.Null(pf.StructRegIndex);
        Assert.Equal(0x2001CL, pf.RegisterSet.StaticAddress);

        var rx = Model.Get<MaskedIntRegDef>("ReverseXReg");
        Assert.Equal(0, rx.Bit);
        Assert.Equal(0, rx.Lsb);
        Assert.Equal(0, rx.Msb);

        var tm = Model.Get<MaskedIntRegDef>("TriggerModeReg");
        Assert.Equal(new[] { 0x30010L }, tm.RegisterSet.Addresses);
        var idx = Assert.Single(tm.RegisterSet.PIndexes);
        Assert.Equal("TriggerSelectorReg", idx.PNode);
        Assert.Equal(4L, idx.Offset);
        Assert.Equal(0, tm.Bit);

        var ci = Model.Get<MaskedIntRegDef>("ColorImplementedReg");
        Assert.Equal(3, ci.Bit);
        Assert.Equal(AccessMode.ReadOnly, ci.RegisterSet.AccessMode);
    }

    [Fact]
    public void IntSwissKnifeRoundTrips()
    {
        var min = Model.Get<IntSwissKnifeDef>("WidthMinCalc");
        var c = Assert.Single(min.Constants);
        Assert.Equal("MIN", c.Name);
        Assert.Equal("8", c.Text);
        Assert.Equal(8L, c.IntValue);
        Assert.Equal(8.0, c.DoubleValue);
        Assert.Equal("MIN", min.Formula);
        Assert.Empty(min.Variables);
        Assert.Empty(min.Expressions);

        var max = Model.Get<IntSwissKnifeDef>("WidthMaxCalc");
        Assert.Equal(new[] { new FormulaVariableDef("SENSOR", "SensorWidth"), new FormulaVariableDef("OFF", "OffsetX") }, max.Variables);
        Assert.Equal("SENSOR - OFF", max.Formula);

        var inc = Model.Get<IntSwissKnifeDef>("WidthIncCalc");
        Assert.Equal(new[] { new FormulaExpressionDef("STEP", "BIN * 4") }, inc.Expressions);
        Assert.Equal("STEP", inc.Formula);
        Assert.Equal(Representation.PureNumber, inc.Representation);

        var avail = Model.Get<IntSwissKnifeDef>("TriggerModeAvailable");
        Assert.Equal("SEL = 0", avail.Formula);
        Assert.Equal(NodeKind.Integer, avail.InterfaceKind);
        ISwissKnifeNodeDef asFormula = avail;
        Assert.Equal("TriggerModeAvailable", asFormula.Name);
    }

    [Fact]
    public void IntConverterRoundTrips()
    {
        var c = Model.Get<IntConverterDef>("DeviceLinkThroughputLimit");
        var k = Assert.Single(c.Constants);
        Assert.Equal("BITS", k.Name);
        Assert.Equal(8L, k.IntValue);
        Assert.Equal("FROM / BITS", c.FormulaTo);
        Assert.Equal("TO * BITS", c.FormulaFrom);
        Assert.Equal("DeviceLinkThroughputLimitReg", c.PValue);
        Assert.Equal("Bps", c.Unit);
        Assert.Equal(Representation.Linear, c.Representation);
        Assert.Equal(Slope.Increasing, c.Slope);
        Assert.False(c.IsLinear);
        Assert.Empty(c.Variables);
        IConverterNodeDef asConv = c;
        Assert.Equal("DeviceLinkThroughputLimit", asConv.Name);
    }

    // ---- Float 계열 ----

    [Fact]
    public void FloatRoundTrips()
    {
        var t = Model.Get<FloatDef>("DeviceTemperature");
        Assert.Equal("DeviceTemperatureReg", t.PValue);
        Assert.Null(t.Value);
        Assert.Equal(-40.0, t.Min);
        Assert.Equal(125.0, t.Max);
        Assert.Null(t.Inc);
        Assert.Equal("C", t.Unit);
        Assert.Equal(Representation.Linear, t.Representation);
        Assert.Equal(DisplayNotation.Fixed, t.DisplayNotation);
        Assert.Equal(1, t.DisplayPrecision);
        Assert.Equal(NodeKind.Float, t.InterfaceKind);

        var fr = Model.Get<FloatDef>("AcquisitionFrameRate");
        Assert.Equal(0.1, fr.Min);
        Assert.Null(fr.Max);
        Assert.Equal("AcquisitionFrameRateMaxCalc", fr.PMax);
        Assert.Equal("AcquisitionFrameRateEnable", fr.PBlockPolling);
        Assert.Equal("Hz", fr.Unit);
        Assert.True(fr.IsStreamable);

        var gain = Model.Get<FloatDef>("Gain");
        Assert.Equal("GainConverter", gain.PValue);
        Assert.Equal(0.0, gain.Min);
        Assert.Equal(48.0, gain.Max);
        Assert.Equal("dB", gain.Unit);
        Assert.Equal("GainRaw", gain.PCastAlias);
        Assert.Null(gain.PAlias);
    }

    [Fact]
    public void FloatRegRoundTrips()
    {
        var reg = Model.Get<FloatRegDef>("DeviceTemperatureReg");
        Assert.Equal(0x0F00L, reg.RegisterSet.StaticAddress);
        Assert.Equal(4L, reg.RegisterSet.Length);
        Assert.Equal(AccessMode.ReadOnly, reg.RegisterSet.AccessMode);
        Assert.Equal(Cachable.NoCache, reg.RegisterSet.Cachable);
        Assert.Equal(1000L, reg.PollingTimeMs);
        Assert.Equal(Endianess.BigEndian, reg.Endianess);
        Assert.Equal(Visibility.Invisible, reg.Visibility);
        Assert.Null(reg.Unit);

        var fr = Model.Get<FloatRegDef>("AcquisitionFrameRateReg");
        Assert.Equal(AccessMode.ReadWrite, fr.RegisterSet.AccessMode);
        Assert.Null(fr.PollingTimeMs);
    }

    [Fact]
    public void SwissKnifeRoundTrips()
    {
        var sk = Model.Get<SwissKnifeDef>("AcquisitionFrameRateMaxCalc");
        Assert.Equal(new[] { new FormulaVariableDef("W", "Width"), new FormulaVariableDef("H", "Height") }, sk.Variables);
        var c = Assert.Single(sk.Constants);
        Assert.Equal("PIXCLK", c.Name);
        Assert.Equal("100000000.0", c.Text);
        Assert.Null(c.IntValue);
        Assert.Equal(1e8, c.DoubleValue);
        Assert.Equal(new[] { new FormulaExpressionDef("PIXELS", "W * H") }, sk.Expressions);
        Assert.Equal("PIXCLK / PIXELS", sk.Formula);
        Assert.Equal("Hz", sk.Unit);
        Assert.Equal(2, sk.DisplayPrecision);
        Assert.Null(sk.DisplayNotation);
        Assert.Equal(NodeKind.Float, sk.InterfaceKind);
    }

    [Fact]
    public void ConverterRoundTrips()
    {
        var e = Model.Get<ConverterDef>("ExposureTime");
        Assert.Equal(new[] { new FormulaVariableDef("TICKNS", "ExposureTimeBase") }, e.Variables);
        var c = Assert.Single(e.Constants);
        Assert.Equal("SCALE", c.Name);
        Assert.Equal(1000L, c.IntValue);
        Assert.Equal("FROM * SCALE / TICKNS", e.FormulaTo);
        Assert.Equal("TO * TICKNS / SCALE", e.FormulaFrom);
        Assert.Equal("ExposureTimeReg", e.PValue);
        Assert.Equal("us", e.Unit);
        Assert.Equal(Representation.Linear, e.Representation);
        Assert.Equal(DisplayNotation.Fixed, e.DisplayNotation);
        Assert.Equal(1, e.DisplayPrecision);
        Assert.Equal(Slope.Increasing, e.Slope);
        Assert.True(e.IsLinear);
        Assert.True(e.IsStreamable);

        var g = Model.Get<ConverterDef>("GainConverter");
        Assert.Empty(g.Variables);
        Assert.Equal("FROM * 10", g.FormulaTo);
        Assert.Equal("TO / 10", g.FormulaFrom);
        Assert.Equal("GainRawReg", g.PValue);
        Assert.Equal(Slope.Increasing, g.Slope);
        Assert.False(g.IsLinear);
        Assert.Equal(Visibility.Invisible, g.Visibility);
    }

    // ---- String / Boolean ----

    [Fact]
    public void StringAndStringRegRoundTrip()
    {
        var fw = Model.Get<StringDef>("DeviceFirmwareVersion");
        Assert.Equal("1.2.3", fw.Value);
        Assert.Null(fw.PValue);
        Assert.Equal(Visibility.Guru, fw.Visibility);
        Assert.Equal(NodeKind.String, fw.InterfaceKind);

        var model = Model.Get<StringRegDef>("DeviceModelName");
        Assert.Equal(0x0068L, model.RegisterSet.StaticAddress);
        Assert.Equal(32L, model.RegisterSet.Length);
        Assert.Equal(AccessMode.ReadWrite, model.RegisterSet.AccessMode);
        Assert.Equal(AccessMode.ReadOnly, model.ImposedAccessMode);
        Assert.Equal(Cachable.WriteThrough, model.RegisterSet.Cachable);
        Assert.Equal("https://example.invalid/docs/DeviceModelName", model.DocuUrl);
        Assert.Equal("Model name as reported by the bootstrap registers.", model.Description);
        Assert.Equal("Device Model Name", model.DisplayName);

        var vendor = Model.Get<StringRegDef>("DeviceVendorName");
        Assert.Equal(0x0048L, vendor.RegisterSet.StaticAddress);
        Assert.Equal(AccessMode.ReadOnly, vendor.RegisterSet.AccessMode);
        Assert.Null(vendor.ImposedAccessMode);
    }

    [Fact]
    public void BooleanRoundTrips()
    {
        var valid = Model.Get<BooleanDef>("DeviceRegistersValid");
        Assert.True(valid.Value);
        Assert.Null(valid.PValue);
        Assert.Equal(NodeKind.Boolean, valid.InterfaceKind);

        var rx = Model.Get<BooleanDef>("ReverseX");
        Assert.Null(rx.Value);
        Assert.Equal("ReverseXReg", rx.PValue);
        Assert.Equal(1L, rx.OnValue);
        Assert.Equal(0L, rx.OffValue);
        Assert.True(rx.IsStreamable);

        var color = Model.Get<BooleanDef>("ColorImplemented");
        Assert.Equal("ColorImplementedReg", color.PValue);
        Assert.Equal(1L, color.OnValue);
    }

    // ---- Enumeration ----

    [Fact]
    public void EnumerationRoundTrips()
    {
        var pf = Model.Get<EnumerationDef>("PixelFormat");
        Assert.Equal("PixelFormatReg", pf.PValue);
        Assert.Null(pf.Value);
        Assert.True(pf.IsStreamable);
        Assert.Equal(NodeKind.Enumeration, pf.InterfaceKind);
        Assert.Equal(3, pf.Entries.Count);

        var mono8 = pf.Entries[0];
        Assert.Equal("EnumEntry_PixelFormat_Mono8", mono8.Name);
        Assert.Equal("Mono8", mono8.EntryName);   // 픽스처는 이미 한정된 Name 을 쓴다 — 접두를 뗀 나머지가 항목 이름
        Assert.Equal(1L, mono8.Value);
        Assert.Equal("Mono8", mono8.Symbolic);
        Assert.Null(mono8.NumericValue);
        Assert.False(mono8.IsSelfClearing);
        Assert.Equal("Monochrome 8-bit", mono8.ToolTip);
        Assert.Equal(NodeNameSpace.Standard, mono8.NameSpace);
        Assert.Equal(NodeKind.EnumEntry, mono8.InterfaceKind);
        Assert.Same(mono8, Model.Nodes["EnumEntry_PixelFormat_Mono8"]);

        Assert.Equal("ColorImplemented", pf.Entries[2].PIsAvailable);
        Assert.Equal(3L, pf.Entries[2].Value);

        var sel = Model.Get<EnumerationDef>("GainSelector");
        Assert.Equal(new[] { "Gain", "GainRaw" }, sel.PSelected);
        Assert.Equal(new[] { "All", "Red", "Blue" }, sel.Entries.Select(e => e.Symbolic).ToArray());

        var tm = Model.Get<EnumerationDef>("TriggerMode");
        Assert.Equal("TriggerModeAvailable", tm.PIsAvailable);
        Assert.Equal("TLParamsLocked", tm.PIsLocked);
        Assert.Null(tm.PIsImplemented);

        var em = Model.Get<EnumerationDef>("ExposureMode");
        Assert.Equal(1L, em.Value);
        Assert.Null(em.PValue);

        var le = Model.Get<EnumerationDef>("DeviceLastError");
        Assert.Equal(0L, le.Value);
        Assert.Null(le.Representation);
    }

    // ---- Command / Register / Port ----

    [Fact]
    public void CommandRoundTrips()
    {
        var start = Model.Get<CommandDef>("AcquisitionStart");
        Assert.Equal("AcquisitionStartReg", start.PValue);
        Assert.Equal(1L, start.CommandValue);
        Assert.Null(start.PCommandValue);
        Assert.Equal(100L, start.PollingTimeMs);
        Assert.Null(start.Value);
        Assert.Equal(NodeKind.Command, start.InterfaceKind);

        var stop = Model.Get<CommandDef>("AcquisitionStop");
        Assert.Null(stop.PollingTimeMs);

        var reset = Model.Get<CommandDef>("DeviceReset");
        Assert.Equal("DeviceResetReg", reset.PValue);
        Assert.Null(reset.CommandValue);
        Assert.Equal("DeviceResetValue", reset.PCommandValue);
        Assert.Equal(Visibility.Guru, reset.Visibility);
    }

    [Fact]
    public void RegisterRoundTrips()
    {
        var lut = Model.Get<RegisterDef>("LUTData");
        Assert.Equal(0x61000L, lut.RegisterSet.StaticAddress);
        Assert.Equal(1024L, lut.RegisterSet.Length);
        Assert.Equal(AccessMode.ReadWrite, lut.RegisterSet.AccessMode);
        Assert.Equal(Cachable.NoCache, lut.RegisterSet.Cachable);
        Assert.Equal("Device", lut.RegisterSet.PPort);
        Assert.Equal(Visibility.Guru, lut.Visibility);
        Assert.Equal(NodeKind.Register, lut.InterfaceKind);
        IRegisterNodeDef asReg = lut;
        Assert.Same(lut.RegisterSet, asReg.RegisterSet);
    }

    [Fact]
    public void PortRoundTrips()
    {
        var dev = Model.Get<PortDef>("Device");
        Assert.Equal("Device register space", dev.ToolTip);
        Assert.Null(dev.ChunkId);

        var chunk = Model.Get<PortDef>("ChunkPort");
        Assert.Equal(0x4001UL, chunk.ChunkId);
        Assert.Null(chunk.PChunkId);
        Assert.True(chunk.IsEndianessSwapped);
        Assert.False(chunk.IsChunkDataCached);
        Assert.Equal(Visibility.Invisible, chunk.Visibility);
    }

    [Fact]
    public void EveryRegisterNodeNamesThePort()
    {
        foreach (var def in Model.NodeList.OfType<IRegisterNodeDef>())
            Assert.Equal("Device", def.RegisterSet.PPort);
    }

    [Fact]
    public void EveryReferencedNameResolvesInTheFixture()
    {
        // 픽스처 자체의 일관성 — 런타임 바인딩이 실패할 참조가 없어야 한다.
        foreach (var def in Model.NodeList)
        {
            foreach (var r in References(def))
                Assert.True(Model.Nodes.ContainsKey(r), $"{def.Name} references missing node '{r}'");
        }
    }

    private static IEnumerable<string> References(NodeDef def)
    {
        foreach (var s in new[] { def.PIsImplemented, def.PIsAvailable, def.PIsLocked, def.PBlockPolling, def.PAlias, def.PCastAlias })
            if (s is not null) yield return s;
        foreach (var s in def.PInvalidators) yield return s;
        foreach (var s in def.PErrors) yield return s;
        foreach (var s in def.PSelected) yield return s;
        switch (def)
        {
            case CategoryDef c:
                foreach (var s in c.PFeatures) yield return s;
                break;
            case IntegerDef i:
                foreach (var s in new[] { i.PValue, i.PMin, i.PMax, i.PInc, i.PValueDefault, i.PIndex }) if (s is not null) yield return s;
                foreach (var s in i.PValueCopies) yield return s;
                break;
            case FloatDef f:
                foreach (var s in new[] { f.PValue, f.PMin, f.PMax, f.PInc, f.PValueDefault, f.PIndex }) if (s is not null) yield return s;
                foreach (var s in f.PValueCopies) yield return s;
                break;
            case BooleanDef b when b.PValue is not null: yield return b.PValue; break;
            case StringDef s when s.PValue is not null: yield return s.PValue; break;
            case EnumerationDef e when e.PValue is not null: yield return e.PValue; break;
            case CommandDef cmd:
                if (cmd.PValue is not null) yield return cmd.PValue;
                if (cmd.PCommandValue is not null) yield return cmd.PCommandValue;
                break;
        }
        if (def is IFormulaNodeDef fn)
            foreach (var v in fn.Variables) yield return v.PNode;
        if (def is IConverterNodeDef cv && cv.PValue is not null) yield return cv.PValue;
        if (def is IRegisterNodeDef rn)
        {
            var rs = rn.RegisterSet;
            if (rs.PPort is not null) yield return rs.PPort;
            if (rs.PLength is not null) yield return rs.PLength;
            foreach (var s in rs.PAddresses) yield return s;
            foreach (var p in rs.PIndexes)
            {
                yield return p.PNode;
                if (p.POffset is not null) yield return p.POffset;
            }
            foreach (var sk in rs.AddressSwissKnives)
                foreach (var v in sk.Variables) yield return v.PNode;
        }
    }
}
