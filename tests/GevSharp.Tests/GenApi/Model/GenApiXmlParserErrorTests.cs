using GevSharp.GenApi;
using GevSharp.GenApi.Model;

namespace GevSharp.Tests.GenApi.Model;

/// <summary>인라인 XML 로 만드는 오류·경고 케이스와 파싱 세부 규칙(공백·16진·네임스페이스·Group 깊이·StructReg 상속).</summary>
public class GenApiXmlParserErrorTests
{
    private static GenApiXmlModel Parse(string body) => GenApiXmlParser.Parse(GenApiFixtures.Wrap(body));

    private static readonly string _widthPair =
        "<Integer Name=\"Width\"><pValue>WidthReg</pValue></Integer>" + GenApiFixtures.IntReg("WidthReg") + GenApiFixtures.DevicePort;

    // ---- 예외 ----

    [Fact]
    public void DuplicateNameThrows()
    {
        var ex = Assert.Throws<GenApiException>(() => Parse(
            "<Integer Name=\"Width\"><Value>1</Value></Integer><Integer Name=\"Width\"><Value>2</Value></Integer>"));
        Assert.Equal("Width", ex.NodeName);
        Assert.Contains("Duplicate", ex.Message);
    }

    [Fact]
    public void EnumEntryNamesAreScopedToTheirEnumeration()
    {
        // 실제 장치 XML 은 여러 열거에 "Off" 를 두고 피처 이름("Width")과도 겹친다 — 항목은 "EnumEntry_{열거}_{항목}" 으로 한정 등록한다.
        var m = Parse(
            "<Enumeration Name=\"Mode\"><Value>0</Value><EnumEntry Name=\"Off\"><Value>0</Value></EnumEntry><EnumEntry Name=\"On\"><Value>1</Value></EnumEntry></Enumeration>"
            + "<Enumeration Name=\"Gate\"><Value>0</Value><EnumEntry Name=\"Off\"><Value>0</Value></EnumEntry></Enumeration>"
            + "<Integer Name=\"Off\"><Value>7</Value></Integer>");
        Assert.Empty(m.Warnings);
        Assert.Equal(6, m.Nodes.Count);
        Assert.Equal(7L, m.Get<IntegerDef>("Off").Value);

        var modeOff = m.Get<EnumEntryDef>("EnumEntry_Mode_Off");
        Assert.Equal("Off", modeOff.EntryName);
        Assert.Equal("Off", modeOff.Symbolic);
        Assert.Equal(0L, modeOff.Value);
        Assert.Same(modeOff, m.Get<EnumerationDef>("Mode").Entries[0]);
        Assert.Equal("EnumEntry_Mode_On", m.Get<EnumerationDef>("Mode").Entries[1].Name);

        var gateOff = m.Get<EnumEntryDef>("EnumEntry_Gate_Off");
        Assert.NotSame(modeOff, gateOff);
        Assert.Equal("Off", gateOff.EntryName);
        Assert.Null(m.Find("EnumEntry_Gate_On"));
    }

    [Fact]
    public void AlreadyQualifiedEnumEntryNameIsKeptAndPrefixStrippedForEntryName()
    {
        // 벤더 XML 이 Name="EnumEntry_Mode_Off" 로 적고 <Symbolic> 을 생략하면 Symbolic 은 "Off" 여야 한다 — 한정 이름이 그대로 새면 SetAsync("Off") 가 깨진다.
        var m = Parse("<Enumeration Name=\"Mode\"><Value>0</Value><EnumEntry Name=\"EnumEntry_Mode_Off\"><Value>0</Value></EnumEntry>"
            + "<EnumEntry Name=\"EnumEntry_Mode_On\"><Value>1</Value><Symbolic>Enabled</Symbolic></EnumEntry></Enumeration>");
        Assert.Empty(m.Warnings);
        var e = m.Get<EnumEntryDef>("EnumEntry_Mode_Off");
        Assert.Equal("Off", e.EntryName);
        Assert.Equal("Off", e.Symbolic);
        Assert.Null(m.Find("EnumEntry_Mode_EnumEntry_Mode_Off"));
        var on = m.Get<EnumEntryDef>("EnumEntry_Mode_On");
        Assert.Equal("On", on.EntryName);
        Assert.Equal("Enabled", on.Symbolic);   // explicit Symbolic still wins
    }

    [Fact]
    public void QualifiedEnumEntryNameCollisionAcrossEnumerationsIsDisambiguatedWithWarning()
    {
        // "Gain" + "Auto_Off" 와 "Gain_Auto" + "Off" 는 둘 다 EnumEntry_Gain_Auto_Off 가 된다 — 정당한 XML 이므로 죽지 않고 "#2" 로 피한다.
        var m = Parse(
            "<Enumeration Name=\"Gain\"><Value>0</Value><EnumEntry Name=\"Auto_Off\"><Value>0</Value></EnumEntry></Enumeration>"
            + "<Enumeration Name=\"Gain_Auto\"><Value>0</Value><EnumEntry Name=\"Off\"><Value>0</Value></EnumEntry><EnumEntry Name=\"On\"><Value>1</Value></EnumEntry></Enumeration>");
        var w = Assert.Single(m.Warnings);
        Assert.Contains("Gain_Auto", w);
        Assert.Contains("EnumEntry_Gain_Auto_Off#2", w);
        Assert.Equal(5, m.Nodes.Count);

        var first = m.Get<EnumEntryDef>("EnumEntry_Gain_Auto_Off");
        Assert.Equal("Auto_Off", first.EntryName);
        Assert.Same(first, m.Get<EnumerationDef>("Gain").Entries[0]);

        var second = m.Get<EnumEntryDef>("EnumEntry_Gain_Auto_Off#2");
        Assert.Equal("Off", second.EntryName);
        Assert.Equal("Off", second.Symbolic);
        Assert.Same(second, m.Get<EnumerationDef>("Gain_Auto").Entries[0]);
        Assert.Equal("EnumEntry_Gain_Auto_On", m.Get<EnumerationDef>("Gain_Auto").Entries[1].Name);
    }

    [Fact]
    public void DuplicateEnumEntryInsideOneEnumerationThrows()
    {
        var ex = Assert.Throws<GenApiException>(() => Parse(
            "<Enumeration Name=\"Mode\"><Value>0</Value><EnumEntry Name=\"Off\"><Value>0</Value></EnumEntry><EnumEntry Name=\"Off\"><Value>1</Value></EnumEntry></Enumeration>"));
        Assert.Equal("EnumEntry_Mode_Off", ex.NodeName);
        Assert.Contains("Duplicate", ex.Message);
        // 한쪽만 접두를 단 경우도 같은 항목이다
        Assert.Throws<GenApiException>(() => Parse(
            "<Enumeration Name=\"Mode\"><Value>0</Value><EnumEntry Name=\"Off\"><Value>0</Value></EnumEntry><EnumEntry Name=\"EnumEntry_Mode_Off\"><Value>1</Value></EnumEntry></Enumeration>"));
    }

    [Fact]
    public void DuplicateInsideGroupThrows()
    {
        Assert.Throws<GenApiException>(() => Parse(
            "<Integer Name=\"A\"><Value>1</Value></Integer><Group Comment=\"g\"><Integer Name=\"A\"><Value>2</Value></Integer></Group>"));
    }

    [Fact]
    public void MissingNameThrows()
    {
        var ex = Assert.Throws<GenApiException>(() => Parse("<Integer><Value>1</Value></Integer>"));
        Assert.Contains("Name", ex.Message);
        Assert.Throws<GenApiException>(() => Parse("<Category Name=\"\"/>"));
        Assert.Throws<GenApiException>(() => Parse("<Enumeration Name=\"E\"><Value>0</Value><EnumEntry><Value>0</Value></EnumEntry></Enumeration>"));
        Assert.Throws<GenApiException>(() => Parse(
            "<StructReg><Address>0</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>P</pPort><StructEntry><Bit>0</Bit></StructEntry></StructReg>"));
    }

    [Fact]
    public void NoRootThrows()
    {
        Assert.Throws<GenApiException>(() => GenApiXmlParser.Parse(""));
        Assert.Throws<GenApiException>(() => GenApiXmlParser.Parse("<!-- nothing here -->"));
        Assert.Throws<GenApiException>(() => GenApiXmlParser.Parse("<?xml version=\"1.0\"?><!-- still nothing -->"));
        var ex = Assert.Throws<GenApiException>(() => GenApiXmlParser.Parse("<RegisterDescription ModelName=\"x\">"));
        Assert.NotNull(ex.InnerException);
        Assert.Contains("Malformed", ex.Message);
    }

    [Fact]
    public void WrongRootElementThrows()
    {
        var ex = Assert.Throws<GenApiException>(() => GenApiXmlParser.Parse("<Camera ModelName=\"x\"/>"));
        Assert.Contains("RegisterDescription", ex.Message);
    }

    [Theory]
    [InlineData("ModelName")]
    [InlineData("VendorName")]
    [InlineData("SchemaMajorVersion")]
    [InlineData("SchemaMinorVersion")]
    [InlineData("MajorVersion")]
    [InlineData("MinorVersion")]
    [InlineData("SubMinorVersion")]
    public void MissingRequiredRootAttributeThrows(string attr)
    {
        var full = GenApiFixtures.Wrap("<Category Name=\"Root\"/>");
        var broken = full.Replace($" {attr}=\"", $" X{attr}=\"");
        var ex = Assert.Throws<GenApiException>(() => GenApiXmlParser.Parse(broken));
        Assert.Contains(attr, ex.Message);
    }

    [Fact]
    public void NonIntegerVersionAttributeThrows()
    {
        var full = GenApiFixtures.Wrap("<Category Name=\"Root\"/>").Replace("MajorVersion=\"1\"", "MajorVersion=\"one\"");
        Assert.Throws<GenApiException>(() => GenApiXmlParser.Parse(full));
    }

    [Fact]
    public void OptionalRootAttributesMayBeAbsent()
    {
        var m = Parse("<Category Name=\"Root\"/>");
        Assert.Null(m.Info.ProductGuid);
        Assert.Null(m.Info.VersionGuid);
        Assert.Null(m.Info.ToolTip);
        Assert.Null(m.Info.StandardNameSpace);
        Assert.Equal(0, m.Info.SchemaSubMinorVersion);
    }

    [Fact]
    public void MaskedIntRegBitErrorsThrow()
    {
        string Masked(string bits) =>
            $"<MaskedIntReg Name=\"M\"><Address>0</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>P</pPort>{bits}</MaskedIntReg>";
        var none = Assert.Throws<GenApiException>(() => Parse(Masked("")));
        Assert.Equal("M", none.NodeName);
        Assert.Throws<GenApiException>(() => Parse(Masked("<LSB>0</LSB>")));
        Assert.Throws<GenApiException>(() => Parse(Masked("<MSB>3</MSB>")));
        Assert.Throws<GenApiException>(() => Parse(Masked("<Bit>1</Bit><LSB>0</LSB><MSB>3</MSB>")));
        Assert.Throws<GenApiException>(() => Parse(Masked("<Bit>64</Bit>")));
        Assert.Throws<GenApiException>(() => Parse(Masked("<LSB>-1</LSB><MSB>3</MSB>")));
        // 정상: Bit 하나 또는 LSB/MSB 쌍
        Assert.Equal(5, Parse(Masked("<Bit>5</Bit>")).Get<MaskedIntRegDef>("M").Msb);
        Assert.Equal(12, Parse(Masked("<LSB>4</LSB><MSB>12</MSB>")).Get<MaskedIntRegDef>("M").Msb);
    }

    [Fact]
    public void RegisterWithoutLengthThrows()
    {
        var ex = Assert.Throws<GenApiException>(() => Parse("<Register Name=\"R\"><Address>0</Address><AccessMode>RW</AccessMode><pPort>P</pPort></Register>"));
        Assert.Equal("R", ex.NodeName);
        Assert.Contains("Length", ex.Message);
        Assert.Throws<GenApiException>(() => Parse("<Register Name=\"R\"><Address>0</Address><Length>0</Length><AccessMode>RW</AccessMode><pPort>P</pPort></Register>"));

        var ok = Parse("<Register Name=\"R\"><Address>0</Address><pLength>Len</pLength><AccessMode>RW</AccessMode><pPort>P</pPort></Register>");
        var r = ok.Get<RegisterDef>("R");
        Assert.Null(r.RegisterSet.Length);
        Assert.Equal("Len", r.RegisterSet.PLength);
    }

    [Fact]
    public void ValueNodesWithoutAnySourceThrow()
    {
        Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><Min>0</Min></Integer>"));
        Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><pIndex>Sel</pIndex></Integer>"));
        Assert.Throws<GenApiException>(() => Parse("<Float Name=\"F\"><Unit>x</Unit></Float>"));
        Assert.Throws<GenApiException>(() => Parse("<String Name=\"S\"/>"));
        Assert.Throws<GenApiException>(() => Parse("<Boolean Name=\"B\"/>"));
        Assert.Throws<GenApiException>(() => Parse("<Enumeration Name=\"E\"><EnumEntry Name=\"X\"><Value>0</Value></EnumEntry></Enumeration>"));
        Assert.Throws<GenApiException>(() => Parse("<Command Name=\"C\"><CommandValue>1</CommandValue></Command>"));
    }

    [Fact]
    public void FormulaNodesWithoutFormulaThrow()
    {
        Assert.Throws<GenApiException>(() => Parse("<IntSwissKnife Name=\"K\"><pVariable Name=\"A\">X</pVariable></IntSwissKnife>"));
        Assert.Throws<GenApiException>(() => Parse("<SwissKnife Name=\"K\"><Formula></Formula></SwissKnife>"));
        Assert.Throws<GenApiException>(() => Parse("<Converter Name=\"K\"><FormulaTo>FROM</FormulaTo><pValue>X</pValue></Converter>"));
        Assert.Throws<GenApiException>(() => Parse("<IntConverter Name=\"K\"><FormulaFrom>TO</FormulaFrom><pValue>X</pValue></IntConverter>"));
        Assert.Throws<GenApiException>(() => Parse("<IntSwissKnife Name=\"K\"><pVariable>X</pVariable><Formula>1</Formula></IntSwissKnife>"));
        Assert.Throws<GenApiException>(() => Parse("<IntSwissKnife Name=\"K\"><Constant Name=\"C\">abc</Constant><Formula>C</Formula></IntSwissKnife>"));
        Assert.Throws<GenApiException>(() => Parse("<IntSwissKnife Name=\"K\"><Expression Name=\"E\"></Expression><Formula>E</Formula></IntSwissKnife>"));
    }

    [Fact]
    public void BadLiteralsThrowWithNodeName()
    {
        var ex = Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><Value>12abc</Value></Integer>"));
        Assert.Equal("I", ex.NodeName);
        Assert.Contains("12abc", ex.Message);
        Assert.Throws<GenApiException>(() => Parse("<Float Name=\"F\"><Value>1,5</Value></Float>"));
        Assert.Throws<GenApiException>(() => Parse("<Boolean Name=\"B\"><Value>maybe</Value></Boolean>"));
        Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><Value>1</Value><Streamable>Sometimes</Streamable></Integer>"));
        Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><Value>1</Value><Visibility>Hidden</Visibility></Integer>"));
        Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><Value>1</Value><Representation>Decimal</Representation></Integer>"));
        Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\" NameSpace=\"Vendor\"><Value>1</Value></Integer>"));
        Assert.Throws<GenApiException>(() => Parse(GenApiFixtures.IntReg("R", extra: "<Endianess>Middle</Endianess>")));
        Assert.Throws<GenApiException>(() => Parse(GenApiFixtures.IntReg("R", extra: "<Sign>Positive</Sign>")));
        Assert.Throws<GenApiException>(() => Parse(GenApiFixtures.IntReg("R", extra: "<Cachable>Sometimes</Cachable>")));
        Assert.Throws<GenApiException>(() => Parse(GenApiFixtures.IntReg("R").Replace("<AccessMode>RW</AccessMode>", "<AccessMode>ReadWrite</AccessMode>")));
        Assert.Throws<GenApiException>(() => Parse(GenApiFixtures.IntReg("R", extra: "<EventID>ZZ</EventID>")));
        Assert.Throws<GenApiException>(() => Parse("<Port Name=\"P\"><ChunkID>nothex</ChunkID></Port>"));
        Assert.Throws<GenApiException>(() => Parse("<Float Name=\"F\"><Value>1</Value><DisplayNotation>Engineering</DisplayNotation></Float>"));
        Assert.Throws<GenApiException>(() => Parse("<Converter Name=\"C\"><FormulaTo>FROM</FormulaTo><FormulaFrom>TO</FormulaFrom><pValue>X</pValue><Slope>Up</Slope></Converter>"));
    }

    [Fact]
    public void EnumEntryWithoutValueThrows()
    {
        var ex = Assert.Throws<GenApiException>(() => Parse(
            "<Enumeration Name=\"E\"><Value>0</Value><EnumEntry Name=\"EnumEntry_E_A\"><Symbolic>A</Symbolic></EnumEntry></Enumeration>"));
        Assert.Equal("EnumEntry_E_A", ex.NodeName);
    }

    [Fact]
    public void ModelConstructorRejectsDuplicates()
    {
        var info = GenApiFixtures.Minimal.Info;
        var a = new GenericNodeDef { Name = "A" };
        Assert.Throws<GenApiException>(() => new GenApiXmlModel(info, new NodeDef[] { a, a }));
        var ok = new GenApiXmlModel(info, new NodeDef[] { a });
        Assert.Same(a, ok.Get("A"));
        Assert.Empty(ok.Warnings);
    }

    // ---- 경고(죽지 않는 경우) ----

    [Fact]
    public void UnknownElementKindBecomesPlaceholderWithWarning()
    {
        var m = Parse("<Category Name=\"Root\"><pFeature>Fancy</pFeature></Category><FancyVendorNode Name=\"Fancy\"><Whatever/></FancyVendorNode>");
        var u = m.Get<UnknownDef>("Fancy");
        Assert.Equal(NodeDefKind.Unknown, u.Kind);
        Assert.Equal(NodeKind.Unknown, u.InterfaceKind);
        Assert.Equal("FancyVendorNode", u.ElementName);
        var w = Assert.Single(m.Warnings);
        Assert.Contains("FancyVendorNode", w);
        Assert.Contains("Fancy", w);
    }

    [Fact]
    public void UnknownElementWithoutNameGetsSynthesizedName()
    {
        var m = Parse("<Mystery/><Mystery/>");
        Assert.Equal(2, m.Nodes.Count);
        Assert.IsType<UnknownDef>(m.Get("Unknown0_Mystery"));
        Assert.IsType<UnknownDef>(m.Get("Unknown1_Mystery"));
        Assert.Equal(2, m.Warnings.Count);
    }

    [Fact]
    public void UnknownChildElementWarnsButNodeIsKept()
    {
        var m = Parse("<Integer Name=\"I\"><Value>7</Value><VendorGadget>x</VendorGadget></Integer>");
        Assert.Equal(7L, m.Get<IntegerDef>("I").Value);
        var w = Assert.Single(m.Warnings);
        Assert.Contains("VendorGadget", w);
        Assert.Contains("'I'", w);
    }

    [Fact]
    public void ExtensionIsIgnoredSilently()
    {
        var m = Parse("<Integer Name=\"I\"><Extension><Anything><Deep/></Anything></Extension><Value>7</Value></Integer>");
        Assert.Empty(m.Warnings);
    }

    [Fact]
    public void EmptyEnumerationAndEmptyStructRegWarn()
    {
        var m = Parse("<Enumeration Name=\"E\"><Value>0</Value></Enumeration>"
            + "<StructReg Comment=\"empty\"><Address>0</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>P</pPort></StructReg>");
        Assert.Equal(2, m.Warnings.Count);
        Assert.Single(m.Nodes);
    }

    [Fact]
    public void NamespacesAreMatchedByLocalName()
    {
        var body = "<Integer Name=\"I\"><Value>1</Value></Integer>";
        Assert.Empty(GenApiXmlParser.Parse(GenApiFixtures.Wrap(body, "http://www.genicam.org/GenApi/Version_1_0")).Warnings);
        Assert.Empty(GenApiXmlParser.Parse(GenApiFixtures.Wrap(body, "http://www.genicam.org/GenApi/Version_1_1")).Warnings);
        Assert.Empty(GenApiXmlParser.Parse(GenApiFixtures.Wrap(body, "")).Warnings);
        var foreign = GenApiXmlParser.Parse(GenApiFixtures.Wrap(body, "http://example.invalid/OtherSchema"));
        Assert.Equal(1L, foreign.Get<IntegerDef>("I").Value);
        Assert.Contains("namespace", Assert.Single(foreign.Warnings));
    }

    [Fact]
    public void PrefixedNamespaceAlsoWorks()
    {
        var xml = "<g:RegisterDescription xmlns:g=\"http://www.genicam.org/GenApi/Version_1_1\" ModelName=\"P\" VendorName=\"V\" "
            + "SchemaMajorVersion=\"1\" SchemaMinorVersion=\"1\" MajorVersion=\"1\" MinorVersion=\"0\" SubMinorVersion=\"0\">"
            + "<g:Group><g:Integer Name=\"I\"><g:Value>0x10</g:Value><g:ToolTip> tip </g:ToolTip></g:Integer></g:Group></g:RegisterDescription>";
        var m = GenApiXmlParser.Parse(xml);
        var i = m.Get<IntegerDef>("I");
        Assert.Equal(16L, i.Value);
        Assert.Equal("tip", i.ToolTip);
        Assert.Empty(m.Warnings);
    }

    [Fact]
    public void LeadingBomIsTolerated()
    {
        var m = GenApiXmlParser.Parse("﻿" + GenApiFixtures.Wrap("<Category Name=\"Root\"/>"));
        Assert.Single(m.Nodes);
    }

    // ---- 파싱 세부 규칙 ----

    [Fact]
    public void GroupsRecurseToAnyDepth()
    {
        var m = Parse("<Group Comment=\"1\"><Group Comment=\"2\"><Group Comment=\"3\"><Group Comment=\"4\">"
            + "<Integer Name=\"Deep\"><Value>4</Value></Integer>"
            + "</Group></Group></Group></Group>"
            + "<Group><Integer Name=\"Shallow\"><Value>1</Value></Integer></Group>");
        Assert.Equal(4L, m.Get<IntegerDef>("Deep").Value);
        Assert.Equal(1L, m.Get<IntegerDef>("Shallow").Value);
        Assert.Equal(new[] { "Deep", "Shallow" }, m.NodeList.Select(n => n.Name));
        Assert.Empty(m.Warnings);
    }

    /// <summary>Group 을 depth 단 감싼 Integer 하나 — 요소 깊이는 루트 1 + depth + Integer 1 + Value 1.</summary>
    private static string NestedGroups(int depth)
    {
        var sb = new System.Text.StringBuilder(depth * 16 + 64);
        for (var i = 0; i < depth; i++) sb.Append("<Group>");
        sb.Append("<Integer Name=\"Deep\"><Value>1</Value></Integer>");
        for (var i = 0; i < depth; i++) sb.Append("</Group>");
        return sb.ToString();
    }

    [Fact]
    public void NestingUpToTheLimitParses()
    {
        var groups = GenApiXmlParser.MaxElementDepth - 3;   // root + groups + Integer + Value == MaxElementDepth
        var m = Parse(NestedGroups(groups));
        Assert.Equal(1L, m.Get<IntegerDef>("Deep").Value);
        Assert.Empty(m.Warnings);
    }

    [Fact]
    public void NestingBeyondTheLimitThrowsInsteadOfOverflowingTheStack()
    {
        var ex = Assert.Throws<GenApiException>(() => Parse(NestedGroups(GenApiXmlParser.MaxElementDepth - 2)));
        Assert.Contains(GenApiXmlParser.MaxElementDepth.ToString(), ex.Message);
        Assert.Contains("nesting", ex.Message);
    }

    [Fact]
    public void ThousandsOfNestedGroupsAreRejectedWithGenApiException()
    {
        // 장치가 보낸 150 KB 남짓의 문서로 Group 재귀가 호출 스택을 넘기면 프로세스가 통째로 죽는다(StackOverflow 는 잡히지 않는다) — 예외로 거절해야 한다.
        var xml = NestedGroups(10_000);
        Assert.True(xml.Length > 100_000);
        Assert.Throws<GenApiException>(() => Parse(xml));
        // 문서 자체가 XML 로서 멀쩡한지도 확인해 둔다(거절이 XML 파서의 몫이 아니라 깊이 검사의 몫임을 못박는다)
        System.Xml.Linq.XDocument.Parse(GenApiFixtures.Wrap(xml));
    }

    [Fact]
    public void DeepNestingInsideAChildElementIsAlsoRejected()
    {
        // 노드 자식(ToolTip 등)의 텍스트를 읽는 XElement.Value 도 자손을 재귀로 훑는다 — Group 이 아니어도 깊이 검사에 걸려야 한다.
        var sb = new System.Text.StringBuilder();
        sb.Append("<Integer Name=\"I\"><Value>1</Value><ToolTip>");
        for (var i = 0; i < 10_000; i++) sb.Append("<x>");
        sb.Append("deep");
        for (var i = 0; i < 10_000; i++) sb.Append("</x>");
        sb.Append("</ToolTip></Integer>");
        Assert.Throws<GenApiException>(() => Parse(sb.ToString()));
        Assert.Throws<GenApiException>(() => GenApiXmlParser.Parse(System.Xml.Linq.XDocument.Parse(GenApiFixtures.Wrap(sb.ToString()))));
    }

    [Fact]
    public void WhitespaceIsTrimmedEverywhere()
    {
        var m = Parse("<Integer Name=\" Spaced \">\n  <pValue>\n    WidthReg\n  </pValue>\n  <Min> 0x10 </Min>\n  <Unit>  us  </Unit>\n</Integer>"
            + "<IntSwissKnife Name=\"K\"><pVariable Name=\" A \"> Spaced </pVariable><Formula>\n   A * 2\n</Formula></IntSwissKnife>");
        var i = m.Get<IntegerDef>("Spaced");
        Assert.Equal("WidthReg", i.PValue);
        Assert.Equal(16L, i.Min);
        Assert.Equal("us", i.Unit);
        var k = m.Get<IntSwissKnifeDef>("K");
        Assert.Equal("A * 2", k.Formula);
        Assert.Equal(new FormulaVariableDef("A", "Spaced"), k.Variables[0]);
    }

    [Fact]
    public void NumbersParseHexAndDecimal()
    {
        var m = Parse(GenApiFixtures.IntReg("Dec", "65536")
            + GenApiFixtures.IntReg("Hex", "0XFFFFFFFF")
            + GenApiFixtures.IntReg("Sum", "0x1000", "<Address>16</Address>")
            + "<Integer Name=\"Neg\"><Value>-5</Value><Min>-0x10</Min></Integer>"
            + "<Integer Name=\"Big\"><Value>0xFFFFFFFFFFFFFFFF</Value></Integer>"
            + "<Float Name=\"Sci\"><Value>1.5e-3</Value><Min>0x10</Min></Float>"
            + GenApiFixtures.DevicePort);
        Assert.Equal(65536L, m.Get<IntRegDef>("Dec").RegisterSet.StaticAddress);
        Assert.Equal(0xFFFFFFFFL, m.Get<IntRegDef>("Hex").RegisterSet.StaticAddress);
        var sum = m.Get<IntRegDef>("Sum").RegisterSet;
        Assert.Equal(new[] { 0x1000L, 16L }, sum.Addresses);
        Assert.Equal(0x1010L, sum.StaticAddress);
        var neg = m.Get<IntegerDef>("Neg");
        Assert.Equal(-5L, neg.Value);
        Assert.Equal(-16L, neg.Min);
        Assert.Equal(-1L, m.Get<IntegerDef>("Big").Value);
        var sci = m.Get<FloatDef>("Sci");
        Assert.Equal(1.5e-3, sci.Value);
        Assert.Equal(16.0, sci.Min);
    }

    [Fact]
    public void BooleanLiteralVariants()
    {
        var m = Parse("<Boolean Name=\"A\"><Value>Yes</Value></Boolean><Boolean Name=\"B\"><Value>No</Value></Boolean>"
            + "<Boolean Name=\"C\"><Value>1</Value></Boolean><Boolean Name=\"D\"><Value>0</Value></Boolean>"
            + "<Boolean Name=\"E\"><Value>true</Value></Boolean><Boolean Name=\"F\"><Value>False</Value></Boolean>"
            + "<Boolean Name=\"G\"><pValue>X</pValue><OnValue>0xFF</OnValue><OffValue>-1</OffValue></Boolean>");
        Assert.True(m.Get<BooleanDef>("A").Value);
        Assert.False(m.Get<BooleanDef>("B").Value);
        Assert.True(m.Get<BooleanDef>("C").Value);
        Assert.False(m.Get<BooleanDef>("D").Value);
        Assert.True(m.Get<BooleanDef>("E").Value);
        Assert.False(m.Get<BooleanDef>("F").Value);
        var g = m.Get<BooleanDef>("G");
        Assert.Null(g.Value);
        Assert.Equal(255L, g.OnValue);
        Assert.Equal(-1L, g.OffValue);
    }

    [Fact]
    public void StringLiteralMayBeEmpty()
    {
        var m = Parse("<String Name=\"S\"><Value></Value></String>");
        Assert.Equal("", m.Get<StringDef>("S").Value);
    }

    [Fact]
    public void EmptyChildElementsCountAsAbsent()
    {
        // <ValidValueSet/> 이 빈 집합(아무 값도 못 씀)이 되거나 <Unit/> 이 "" 가 되면 뜻이 뒤집힌다 — 빈 요소는 없는 것과 같다.
        var m = Parse("<Integer Name=\"I\"><Value>1</Value><ValidValueSet></ValidValueSet><Unit/><ToolTip>  </ToolTip><Streamable/></Integer>"
            + GenApiFixtures.IntReg("R", extra: "<Cachable></Cachable><pIndex Offset=\"4\">Idx</pIndex>") + GenApiFixtures.DevicePort);
        Assert.Empty(m.Warnings);
        var i = m.Get<IntegerDef>("I");
        Assert.Null(i.ValidValueSet);
        Assert.Null(i.Unit);
        Assert.Null(i.ToolTip);
        Assert.False(i.IsStreamable);
        Assert.Equal(Cachable.WriteThrough, m.Get<IntRegDef>("R").RegisterSet.Cachable);
        // 빈 요소가 값 출처를 대신하지는 못한다
        Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><Value/></Integer>"));
        Assert.Throws<GenApiException>(() => Parse("<Boolean Name=\"B\"><Value></Value></Boolean>"));
    }

    [Fact]
    public void EmptyAddressLiteralThrowsAndEmptyReferenceWarns()
    {
        // 빈 <Address/> 가 조용히 빠지면 주소 0 의 레지스터가 된다 — 리터럴은 예외, 참조 목록의 빈 항목은 경고.
        var ex = Assert.Throws<GenApiException>(() => Parse(GenApiFixtures.IntReg("R", extra: "<Address></Address>") + GenApiFixtures.DevicePort));
        Assert.Equal("R", ex.NodeName);
        Assert.Contains("Address", ex.Message);

        var m = Parse("<Integer Name=\"I\"><Value>1</Value><pInvalidator/><pInvalidator>Other</pInvalidator></Integer>"
            + "<Category Name=\"Root\"><pFeature>I</pFeature><pFeature> </pFeature></Category>");
        Assert.Equal(new[] { "Other" }, m.Get<IntegerDef>("I").PInvalidators);
        Assert.Equal(new[] { "I" }, m.Get<CategoryDef>("Root").PFeatures);
        Assert.Equal(2, m.Warnings.Count);
        Assert.Contains("pInvalidator", m.Warnings[0]);
        Assert.Contains("'I'", m.Warnings[0]);
        Assert.Contains("pFeature", m.Warnings[1]);
    }

    [Fact]
    public void EmptyChildOnStructEntryDoesNotOverrideTheInheritedValue()
    {
        var m = Parse("<StructReg><Address>0</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>P</pPort><ToolTip>struct tip</ToolTip><Unit>u</Unit>"
            + "<StructEntry Name=\"E\"><Bit>0</Bit><ToolTip></ToolTip><Unit/></StructEntry></StructReg>");
        Assert.Empty(m.Warnings);
        var e = m.Get<MaskedIntRegDef>("E");
        Assert.Equal("struct tip", e.ToolTip);
        Assert.Equal("u", e.Unit);
    }

    [Fact]
    public void CommandWithLiteralValueOnly()
    {
        var m = Parse("<Command Name=\"C\"><Value>0</Value><CommandValue>0x2</CommandValue></Command>");
        var c = m.Get<CommandDef>("C");
        Assert.Equal(0L, c.Value);
        Assert.Null(c.PValue);
        Assert.Equal(2L, c.CommandValue);
    }

    [Fact]
    public void EnumEntrySymbolicDefaultsToNameAndExtrasRoundTrip()
    {
        var m = Parse("<Enumeration Name=\"Rate\"><Value>0</Value><Representation>PureNumber</Representation>"
            + "<EnumEntry Name=\"Rate_10\"><Value>0</Value><NumericValue>10.5</NumericValue><IsSelfClearing>Yes</IsSelfClearing><pIsImplemented>Impl</pIsImplemented></EnumEntry>"
            + "</Enumeration>");
        var e = m.Get<EnumerationDef>("Rate");
        Assert.Equal(Representation.PureNumber, e.Representation);
        var entry = Assert.Single(e.Entries);
        Assert.Equal("EnumEntry_Rate_Rate_10", entry.Name);
        Assert.Equal("Rate_10", entry.EntryName);
        Assert.Equal("Rate_10", entry.Symbolic);
        Assert.Same(entry, m.Get("EnumEntry_Rate_Rate_10"));
        Assert.Equal(10.5, entry.NumericValue);
        Assert.True(entry.IsSelfClearing);
        Assert.Equal("Impl", entry.PIsImplemented);
    }

    [Fact]
    public void IndexedValueSelectionRoundTrips()
    {
        var m = Parse("<Integer Name=\"I\"><pIndex>Sel</pIndex><pValueIndexed Index=\"0\">A</pValueIndexed><pValueIndexed Index=\"0x2\">B</pValueIndexed><pValueDefault>D</pValueDefault></Integer>"
            + "<Float Name=\"F\"><pIndex>Sel</pIndex><pValueIndexed Index=\"1\">A</pValueIndexed><pValueDefault>D</pValueDefault></Float>");
        var i = m.Get<IntegerDef>("I");
        Assert.Equal("Sel", i.PIndex);
        Assert.Equal(new[] { new PValueIndexedDef(0, "A"), new PValueIndexedDef(2, "B") }, i.PValueIndexed);
        Assert.Equal("D", i.PValueDefault);
        var f = m.Get<FloatDef>("F");
        Assert.Equal("Sel", f.PIndex);
        Assert.Equal(new[] { new PValueIndexedDef(1, "A") }, f.PValueIndexed);
        Assert.Equal("D", f.PValueDefault);
        Assert.Empty(m.Warnings);
        Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><pIndex>Sel</pIndex><pValueIndexed>A</pValueIndexed></Integer>"));
    }

    [Fact]
    public void IndexedValuesWithoutPIndexThrow()
    {
        // pIndex 가 없으면 어느 슬롯도 열리지 않는다 — 값 출처 검사는 통과하고 런타임이 조용히 0 을 내놓던 자리다.
        var i = Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><ValueIndexed Index=\"0\">7</ValueIndexed></Integer>"));
        Assert.Equal("I", i.NodeName);
        Assert.Contains("pIndex", i.Message);
        var p = Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><pValueIndexed Index=\"0\">A</pValueIndexed></Integer>"));
        Assert.Equal("I", p.NodeName);
        var f = Assert.Throws<GenApiException>(() => Parse("<Float Name=\"F\"><ValueIndexed Index=\"0\">0.5</ValueIndexed></Float>"));
        Assert.Equal("F", f.NodeName);

        // 기본값만 있는 정의는 그대로 통과한다 — pIndex 없이도 기본 노드·리터럴이 값 출처다.
        Assert.Equal(3L, Parse("<Integer Name=\"OnlyDefault\"><ValueDefault>3</ValueDefault></Integer>").Get<IntegerDef>("OnlyDefault").ValueDefault);

        // 슬롯 말고 다른 값 출처가 하나라도 있으면 그 문서는 살아 있어야 한다 — 읽기가 그쪽을 타 제 값을 낸다.
        Assert.Equal("R", Parse("<Integer Name=\"I\"><pValue>R</pValue><ValueIndexed Index=\"0\">7</ValueIndexed></Integer>").Get<IntegerDef>("I").PValue);
        Assert.Equal(3L, Parse("<Integer Name=\"I\"><ValueDefault>3</ValueDefault><ValueIndexed Index=\"0\">7</ValueIndexed></Integer>").Get<IntegerDef>("I").ValueDefault);
        Assert.Equal("D", Parse("<Integer Name=\"I\"><pValueDefault>D</pValueDefault><pValueIndexed Index=\"0\">A</pValueIndexed></Integer>").Get<IntegerDef>("I").PValueDefault);
        Assert.Equal(1L, Parse("<Integer Name=\"I\"><Value>1</Value><ValueIndexed Index=\"0\">7</ValueIndexed></Integer>").Get<IntegerDef>("I").Value);
        Assert.Equal("R", Parse("<Float Name=\"F\"><pValue>R</pValue><ValueIndexed Index=\"0\">0.5</ValueIndexed></Float>").Get<FloatDef>("F").PValue);
    }

    [Fact]
    public void LiteralIndexedValuesRoundTrip()
    {
        var m = Parse("<Integer Name=\"Addr\"><pIndex>Sel</pIndex><ValueIndexed Index=\"0\">0x2000</ValueIndexed><ValueIndexed Index=\"0x2\"> 8192 </ValueIndexed>"
            + "<pValueIndexed Index=\"3\">Other</pValueIndexed><ValueDefault>0x3000</ValueDefault></Integer>"
            + "<Float Name=\"Scale\"><pIndex>Sel</pIndex><ValueIndexed Index=\"1\">0.5</ValueIndexed><ValueDefault>1e2</ValueDefault></Float>"
            + "<Integer Name=\"OnlyDefault\"><ValueDefault>3</ValueDefault></Integer>");
        Assert.Empty(m.Warnings);
        var a = m.Get<IntegerDef>("Addr");
        Assert.Equal("Sel", a.PIndex);
        Assert.Equal(new[] { new ValueIndexedDef<long>(0, 0x2000), new ValueIndexedDef<long>(2, 8192) }, a.ValueIndexed);
        Assert.Equal(new[] { new PValueIndexedDef(3, "Other") }, a.PValueIndexed);
        Assert.Equal(0x3000L, a.ValueDefault);
        Assert.Null(a.PValueDefault);
        var s = m.Get<FloatDef>("Scale");
        Assert.Equal(new[] { new ValueIndexedDef<double>(1, 0.5) }, s.ValueIndexed);
        Assert.Equal(100.0, s.ValueDefault);
        Assert.Empty(s.PValueIndexed);
        Assert.Equal(3L, m.Get<IntegerDef>("OnlyDefault").ValueDefault);
        Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><pIndex>Sel</pIndex><ValueIndexed>1</ValueIndexed></Integer>"));
        var bad = Assert.Throws<GenApiException>(() => Parse("<Integer Name=\"I\"><pIndex>Sel</pIndex><ValueIndexed Index=\"0\">abc</ValueIndexed></Integer>"));
        Assert.Equal("I", bad.NodeName);
    }

    [Fact]
    public void PIndexVariantsRoundTrip()
    {
        var m = Parse(GenApiFixtures.IntReg("NoOffset", extra: "<pIndex>Idx</pIndex>")
            + GenApiFixtures.IntReg("POffset", extra: "<pIndex pOffset=\"Stride\">Idx</pIndex><pIndex Offset=\"0x8\">Idx2</pIndex>")
            + GenApiFixtures.DevicePort);
        var a = Assert.Single(m.Get<IntRegDef>("NoOffset").RegisterSet.PIndexes);
        Assert.Equal("Idx", a.PNode);
        Assert.Null(a.Offset);
        Assert.Null(a.POffset);
        var b = m.Get<IntRegDef>("POffset").RegisterSet.PIndexes;
        Assert.Equal(2, b.Count);
        Assert.Equal("Stride", b[0].POffset);
        Assert.Null(b[0].Offset);
        Assert.Equal(8L, b[1].Offset);
        Assert.Equal("Idx2", b[1].PNode);
        Assert.Throws<GenApiException>(() => Parse(GenApiFixtures.IntReg("Bad", extra: "<pIndex Offset=\"4\"></pIndex>") + GenApiFixtures.DevicePort));
    }

    [Fact]
    public void InlineAddressSwissKnifeWithoutNameGetsSynthesizedName()
    {
        var m = Parse(GenApiFixtures.IntReg("R", extra: "<IntSwissKnife><pVariable Name=\"I\">Idx</pVariable><Formula>I * 8</Formula></IntSwissKnife>"
            + "<IntSwissKnife><Formula>4</Formula></IntSwissKnife>") + GenApiFixtures.DevicePort);
        var knives = m.Get<IntRegDef>("R").RegisterSet.AddressSwissKnives;
        Assert.Equal(2, knives.Count);
        Assert.Equal("R_AddrSwissKnife0", knives[0].Name);
        Assert.Equal("I * 8", knives[0].Formula);
        Assert.Equal("R_AddrSwissKnife1", knives[1].Name);
        Assert.Equal("4", knives[1].Formula);
        Assert.Equal(2, m.Nodes.Count);   // R + Device only — unnamed nested knives are not registered
        Assert.Empty(m.Warnings);
    }

    [Fact]
    public void NamedInlineAddressSwissKnifeIsRegisteredSoOthersCanReferenceIt()
    {
        // 이름 있는 인라인 IntSwissKnife 는 진짜 노드 이름이다 — 다른 노드가 pVariable 로 가리킬 수 있어야 하고 이름 중복 검사에도 든다.
        var m = Parse(GenApiFixtures.IntReg("R", extra: "<IntSwissKnife Name=\"ROffset\"><pVariable Name=\"I\">Idx</pVariable><Formula>I * 8</Formula></IntSwissKnife>")
            + "<Integer Name=\"Idx\"><Value>2</Value></Integer>"
            + "<IntSwissKnife Name=\"Twice\"><pVariable Name=\"O\">ROffset</pVariable><Formula>O * 2</Formula></IntSwissKnife>"
            + GenApiFixtures.DevicePort);
        Assert.Empty(m.Warnings);
        var knife = Assert.Single(m.Get<IntRegDef>("R").RegisterSet.AddressSwissKnives);
        Assert.Same(knife, m.Get<IntSwissKnifeDef>("ROffset"));
        Assert.Equal(new[] { "R", "ROffset", "Idx", "Twice", "Device" }, m.NodeList.Select(n => n.Name));

        var dup = Assert.Throws<GenApiException>(() => Parse(
            GenApiFixtures.IntReg("R", extra: "<IntSwissKnife Name=\"Idx\"><Formula>8</Formula></IntSwissKnife>")
            + "<Integer Name=\"Idx\"><Value>2</Value></Integer>" + GenApiFixtures.DevicePort));
        Assert.Equal("Idx", dup.NodeName);

        // StructReg 안의 이름 있는 knife 는 펼쳐진 항목들 뒤에 등록된다
        var s = Parse("<StructReg><Address>0x100</Address><IntSwissKnife Name=\"SOff\"><Formula>4</Formula></IntSwissKnife><Length>4</Length><AccessMode>RW</AccessMode><pPort>P</pPort>"
            + "<StructEntry Name=\"A\"><Bit>0</Bit></StructEntry><StructEntry Name=\"B\"><Bit>1</Bit></StructEntry></StructReg>");
        Assert.Equal(new[] { "A", "B", "SOff" }, s.NodeList.Select(n => n.Name));
        Assert.Same(s.Get<MaskedIntRegDef>("A").RegisterSet.AddressSwissKnives[0], s.Get("SOff"));
    }

    [Fact]
    public void StructRegInheritanceAndOverrides()
    {
        var m = Parse(
            "<StructReg Comment=\"first\"><Address>0x100</Address><pAddress>Base</pAddress><Length>2</Length><AccessMode>RO</AccessMode><pPort>P</pPort>"
            + "<Cachable>NoCache</Cachable><PollingTime>50</PollingTime><Endianess>LittleEndian</Endianess><Sign>Signed</Sign>"
            + "<ToolTip>struct tip</ToolTip><Visibility>Guru</Visibility><pIsLocked>Lk</pIsLocked><pIsImplemented>Impl</pIsImplemented><pSelected>S1</pSelected><Unit>u</Unit><Streamable>Yes</Streamable>"
            + "<StructEntry Name=\"E0\"><Bit>0</Bit></StructEntry>"
            + "<StructEntry Name=\"E1\" Comment=\"own\"><ToolTip>own tip</ToolTip><Visibility>Expert</Visibility><pSelected>S2</pSelected><Unit>v</Unit><Streamable>No</Streamable>"
            + "<AccessMode>RW</AccessMode><Sign>Unsigned</Sign><PollingTime>10</PollingTime><LSB>1</LSB><MSB>3</MSB></StructEntry>"
            + "</StructReg>"
            + "<StructReg><Address>0x200</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>P</pPort>"
            + "<StructEntry Name=\"F0\"><Bit>7</Bit></StructEntry></StructReg>");
        Assert.Empty(m.Warnings);
        Assert.Equal(3, m.Nodes.Count);

        var e0 = m.Get<MaskedIntRegDef>("E0");
        Assert.Equal(0, e0.StructRegIndex);
        Assert.Equal(new[] { 0x100L }, e0.RegisterSet.Addresses);
        Assert.Equal(new[] { "Base" }, e0.RegisterSet.PAddresses);
        Assert.Equal(2L, e0.RegisterSet.Length);
        Assert.Equal(AccessMode.ReadOnly, e0.RegisterSet.AccessMode);
        Assert.Equal(Cachable.NoCache, e0.RegisterSet.Cachable);
        Assert.Equal(50L, e0.PollingTimeMs);
        Assert.Equal(Endianess.LittleEndian, e0.Endianess);
        Assert.Equal(Sign.Signed, e0.Sign);
        Assert.Equal("first", e0.Comment);
        Assert.Same(e0.RegisterSet, m.Get<MaskedIntRegDef>("E0").RegisterSet);

        var e1 = m.Get<MaskedIntRegDef>("E1");
        Assert.Equal(AccessMode.ReadWrite, e1.RegisterSet.AccessMode);    // override
        Assert.Equal(Cachable.NoCache, e1.RegisterSet.Cachable);          // inherited
        Assert.Equal(Sign.Unsigned, e1.Sign);                              // override
        Assert.Equal(10L, e1.PollingTimeMs);                               // override
        Assert.Equal("own", e1.Comment);                                   // entry's own Comment wins
        Assert.Equal(1, e1.Lsb);
        Assert.Equal(3, e1.Msb);

        // StructReg 의 공통 자식은 항목에 복사되고 항목 쪽이 이긴다
        Assert.Equal("struct tip", e0.ToolTip);
        Assert.Equal(Visibility.Guru, e0.Visibility);
        Assert.Equal("Lk", e0.PIsLocked);
        Assert.Equal("Impl", e0.PIsImplemented);
        Assert.Equal(new[] { "S1" }, e0.PSelected);
        Assert.Equal("u", e0.Unit);
        Assert.True(e0.IsStreamable);
        Assert.Equal("own tip", e1.ToolTip);
        Assert.Equal(Visibility.Expert, e1.Visibility);
        Assert.Equal("Lk", e1.PIsLocked);
        Assert.Equal("Impl", e1.PIsImplemented);
        Assert.Equal(new[] { "S1", "S2" }, e1.PSelected);
        Assert.Equal("v", e1.Unit);
        Assert.False(e1.IsStreamable);

        var f0 = m.Get<MaskedIntRegDef>("F0");
        Assert.Equal(1, f0.StructRegIndex);
        Assert.Equal(Endianess.LittleEndian, f0.Endianess);
        Assert.Null(f0.Comment);
        Assert.Null(f0.PollingTimeMs);
    }

    [Fact]
    public void RegisterSetDefaultsWhenElementsAbsent()
    {
        var m = Parse("<StringReg Name=\"S\"><Address>0</Address><Length>8</Length></StringReg>");
        var rs = m.Get<StringRegDef>("S").RegisterSet;
        Assert.Equal(AccessMode.ReadWrite, rs.AccessMode);
        Assert.Equal(Cachable.WriteThrough, rs.Cachable);
        Assert.Null(rs.PPort);
        Assert.True(rs.HasStaticAddress);
        Assert.Equal(0L, rs.StaticAddress);
    }

    [Fact]
    public void PortChunkIdAcceptsHexPrefix()
    {
        var m = Parse("<Port Name=\"P\"><ChunkID>0x4001</ChunkID><pChunkID>CID</pChunkID><CacheChunkData>Yes</CacheChunkData><EventID>0A10</EventID></Port>");
        var p = m.Get<PortDef>("P");
        Assert.Equal(0x4001UL, p.ChunkId);
        Assert.Equal("CID", p.PChunkId);
        Assert.True(p.IsChunkDataCached);
        Assert.False(p.IsEndianessSwapped);
        Assert.Equal("0A10", p.EventId);
        Assert.Equal(0xA10UL, p.EventIdValue);
    }

    [Fact]
    public void CommonAttributesRoundTripOnAnyKind()
    {
        var m = Parse("<Command Name=\"C\" NameSpace=\"Standard\" Comment=\"note\"><ToolTip>t</ToolTip><Description>d</Description><DisplayName>n</DisplayName>"
            + "<Visibility>Expert</Visibility><DocuURL>u</DocuURL><IsDeprecated>Yes</IsDeprecated><EventID>1F</EventID>"
            + "<pIsImplemented>a</pIsImplemented><pIsAvailable>b</pIsAvailable><pIsLocked>c</pIsLocked><pBlockPolling>d2</pBlockPolling>"
            + "<ImposedAccessMode>WO</ImposedAccessMode><pError>e1</pError><pError>e2</pError><pAlias>f</pAlias><pCastAlias>g</pCastAlias>"
            + "<pInvalidator>h1</pInvalidator><pInvalidator>h2</pInvalidator><Streamable>Yes</Streamable><PollingTime>0x10</PollingTime>"
            + "<pValue>R</pValue><CommandValue>1</CommandValue></Command>");
        var c = m.Get<CommandDef>("C");
        Assert.Equal(NodeNameSpace.Standard, c.NameSpace);
        Assert.Equal("note", c.Comment);
        Assert.Equal("t", c.ToolTip);
        Assert.Equal("d", c.Description);
        Assert.Equal("n", c.DisplayName);
        Assert.Equal(Visibility.Expert, c.Visibility);
        Assert.Equal("u", c.DocuUrl);
        Assert.True(c.IsDeprecated);
        Assert.Equal("1F", c.EventId);
        Assert.Equal(0x1FUL, c.EventIdValue);
        Assert.Equal("a", c.PIsImplemented);
        Assert.Equal("b", c.PIsAvailable);
        Assert.Equal("c", c.PIsLocked);
        Assert.Equal("d2", c.PBlockPolling);
        Assert.Equal(AccessMode.WriteOnly, c.ImposedAccessMode);
        Assert.Equal(new[] { "e1", "e2" }, c.PErrors);
        Assert.Equal("f", c.PAlias);
        Assert.Equal("g", c.PCastAlias);
        Assert.Equal(new[] { "h1", "h2" }, c.PInvalidators);
        Assert.True(c.IsStreamable);
        Assert.Equal(16L, c.PollingTimeMs);
        Assert.Empty(m.Warnings);
    }
}
