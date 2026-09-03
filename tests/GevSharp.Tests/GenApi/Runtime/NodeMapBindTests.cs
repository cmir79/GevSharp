using GevSharp.GenApi;
using GevSharp.GenApi.Runtime;
using GevSharp.Sim;
using GevSharp.Tests.GenApi.Model;

#pragma warning disable xUnit1051

namespace GevSharp.Tests.GenApi.Runtime;

/// <summary>바인딩 — 픽스처 전체가 묶이는지, 빠진 참조·틀린 종류·순환·루트 부재가 노드 이름을 담은 <see cref="GenApiException"/> 인지.</summary>
public class NodeMapBindTests
{
    private static string F(string name) => $"<pFeature>{name}</pFeature>";

    [Fact]
    public void MinimalFixture_BindsAllNodes()
    {
        var port = new MemoryPort();
        var map = GenApiNodeMap.Parse(GenApiFixtures.Minimal, port);

        Assert.Equal("GevSharpMinimal", map.Info.ModelName);
        Assert.Equal(4, map.Nodes.Count);
        Assert.Equal("Root", map.Root.Name);
        Assert.Single(map.Root.Features);
        Assert.Equal("Width", map.Root.Features[0].Name);
        Assert.Equal(NodeKind.Integer, map.GetNode("Width")!.Kind);
        Assert.Same(port, map.GetNode<IPortNode>("Device").Port);
        Assert.Null(map.GetNode("Nope"));
    }

    [Fact]
    public void GroupsFixture_BindsEveryNodeKind()
    {
        var map = GenApiNodeMap.Parse(GenApiFixtures.Groups, new MemoryPort());

        Assert.Equal(98, map.Nodes.Count);
        Assert.Equal(7, map.Root.Features.Count);
        Assert.IsAssignableFrom<ICategory>(map.GetNode("DeviceControl"));
        Assert.IsAssignableFrom<IInteger>(map.GetNode("Width"));
        Assert.IsAssignableFrom<IInteger>(map.GetNode("GevSCPSPacketSize"));       // StructEntry
        Assert.IsAssignableFrom<IInteger>(map.GetNode("LUTValueAddr"));            // named inline IntSwissKnife
        Assert.IsAssignableFrom<IInteger>(map.GetNode("DeviceLinkThroughputLimit"));
        Assert.IsAssignableFrom<IFloat>(map.GetNode("DeviceTemperature"));
        Assert.IsAssignableFrom<IFloat>(map.GetNode("AcquisitionFrameRateMaxCalc"));
        Assert.IsAssignableFrom<IFloat>(map.GetNode("GainConverter"));
        Assert.IsAssignableFrom<IString>(map.GetNode("DeviceFirmwareVersion"));
        Assert.IsAssignableFrom<IString>(map.GetNode("DeviceVendorName"));
        Assert.IsAssignableFrom<IBoolean>(map.GetNode("ReverseX"));
        Assert.IsAssignableFrom<IEnumeration>(map.GetNode("PixelFormat"));
        Assert.IsAssignableFrom<IEnumEntry>(map.GetNode("EnumEntry_PixelFormat_Mono8"));
        Assert.IsAssignableFrom<ICommand>(map.GetNode("DeviceReset"));
        Assert.IsAssignableFrom<IRegister>(map.GetNode("LUTData"));
        Assert.IsAssignableFrom<IPortNode>(map.GetNode("ChunkPort"));
        Assert.Equal(NodeKind.Unknown, map.GetNode("DeviceMetaData")!.Kind);
    }

    [Fact]
    public void SimCameraXml_Binds()
    {
        var map = GenApiNodeMap.Parse(SimDevice.DefaultGenApiXml, new MemoryPort());

        Assert.Equal("SimCamera", map.Info.ModelName);
        Assert.Equal(6, map.Root.Features.Count);
        Assert.IsAssignableFrom<IFloat>(map.GetNode("ExposureTime"));
        Assert.IsAssignableFrom<IInteger>(map.GetNode("PayloadSize"));
        Assert.IsAssignableFrom<IInteger>(map.GetNode("TriggerSourceReg"));
    }

    [Fact]
    public void GetNodeGeneric_WrongTypeThrows()
    {
        var map = GenApiNodeMap.Parse(GenApiFixtures.Minimal, new MemoryPort());

        var ex = Assert.Throws<GenApiException>(() => map.GetFloat("Width"));
        Assert.Equal("Width", ex.NodeName);
        Assert.Contains("Integer", ex.Message);
        Assert.Throws<GenApiException>(() => map.GetInteger("Nope"));
    }

    [Fact]
    public void Parse_NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => GenApiNodeMap.Parse((string)null!, new MemoryPort()));
        Assert.Throws<ArgumentNullException>(() => GenApiNodeMap.Parse("<x/>", null!));
        Assert.Throws<ArgumentNullException>(() => GenApiNodeMap.Parse((GevSharp.GenApi.Model.GenApiXmlModel)null!, new MemoryPort()));
    }

    [Fact]
    public void MissingReference_NamesBothNodes()
    {
        var body = RuntimeFixture.Integer("Width", "Missing");

        var ex = Assert.Throws<GenApiException>(() => RuntimeFixture.Bind(body, new MemoryPort(), "Width"));
        Assert.Equal("Width", ex.NodeName);
        Assert.Contains("'Missing'", ex.Message);
        Assert.Contains("pValue", ex.Message);
    }

    [Fact]
    public void MissingFeature_InCategoryThrowsAtBind()
    {
        var body = "<Category Name=\"Sub\">" + F("Nope") + "</Category>";

        var ex = Assert.Throws<GenApiException>(() => RuntimeFixture.Bind(body, new MemoryPort(), "Sub"));
        Assert.Equal("Sub", ex.NodeName);
        Assert.Contains("'Nope'", ex.Message);
    }

    [Fact]
    public void MissingRootCategory_Throws()
    {
        var xml = GenApiFixtures.Wrap("<Category Name=\"Top\"/>" + GenApiFixtures.DevicePort);

        var ex = Assert.Throws<GenApiException>(() => GenApiNodeMap.Parse(xml, new MemoryPort()));
        Assert.Contains("Root", ex.Message);
    }

    [Fact]
    public void RegisterWithoutPort_Throws()
    {
        var body = "<IntReg Name=\"R\"><Address>0x10</Address><Length>4</Length><AccessMode>RW</AccessMode></IntReg>";

        var ex = Assert.Throws<GenApiException>(() => RuntimeFixture.Bind(body, new MemoryPort()));
        Assert.Equal("R", ex.NodeName);
        Assert.Contains("pPort", ex.Message);
    }

    [Fact]
    public void ReferenceOfWrongKind_Throws()
    {
        var body = "<Integer Name=\"I\"><pValue>S</pValue></Integer>"
            + "<StringReg Name=\"S\"><Address>0x10</Address><Length>8</Length><AccessMode>RW</AccessMode><pPort>Device</pPort></StringReg>";

        var ex = Assert.Throws<GenApiException>(() => RuntimeFixture.Bind(body, new MemoryPort()));
        Assert.Equal("I", ex.NodeName);
        Assert.Contains("StringReg", ex.Message);
    }

    [Fact]
    public void PortNodeAsValue_Throws()
    {
        var body = "<Integer Name=\"I\"><pValue>Device</pValue></Integer>";

        var ex = Assert.Throws<GenApiException>(() => RuntimeFixture.Bind(body, new MemoryPort()));
        Assert.Equal("I", ex.NodeName);
    }

    public static IEnumerable<object[]> Cycles()
    {
        // pValue 두 노드가 서로 가리킨다
        yield return new object[] { "pValue", "<Integer Name=\"A\"><pValue>B</pValue></Integer><Integer Name=\"B\"><pValue>A</pValue></Integer>" };
        // 자기 자신
        yield return new object[] { "self", "<Integer Name=\"A\"><pValue>A</pValue></Integer>" };
        // pAddress 가 그 레지스터를 읽는 정수를 가리킨다
        yield return new object[] { "pAddress", RuntimeFixture.IntReg("R", "0x10", "<pAddress>A</pAddress>") + RuntimeFixture.Integer("A", "R") };
        // pIndex
        yield return new object[] { "pIndex", RuntimeFixture.IntReg("R", "0x10", "<pIndex Offset=\"4\">A</pIndex>") + RuntimeFixture.Integer("A", "R") };
        // pVariable 이 수식 값을 쓰는 정수를 가리킨다
        yield return new object[] { "pVariable", "<IntSwissKnife Name=\"K\"><pVariable Name=\"X\">A</pVariable><Formula>X + 1</Formula></IntSwissKnife>" + RuntimeFixture.Integer("A", "K") };
        // pMin
        yield return new object[] { "pMin", "<Integer Name=\"A\"><Value>1</Value><pMin>B</pMin></Integer><Integer Name=\"B\"><Value>1</Value><pMax>A</pMax></Integer>" };
        // 인라인 주소 IntSwissKnife 가 그 레지스터를 읽는 정수를 참조한다
        yield return new object[] { "address knife", RuntimeFixture.IntReg("R", "0x10", "<IntSwissKnife Name=\"AK\"><pVariable Name=\"X\">A</pVariable><Formula>X * 4</Formula></IntSwissKnife>") + RuntimeFixture.Integer("A", "R") };
        // pLength
        yield return new object[] { "pLength", "<Register Name=\"R\"><Address>0x10</Address><pLength>A</pLength><AccessMode>RW</AccessMode><pPort>Device</pPort></Register><Integer Name=\"A\"><pValue>B</pValue></Integer><Integer Name=\"B\"><Value>4</Value><pMax>C</pMax></Integer><IntSwissKnife Name=\"C\"><pVariable Name=\"L\">A</pVariable><Formula>L</Formula></IntSwissKnife>" };
        // Converter pValue 가 되돌아온다
        yield return new object[] { "converter", "<Converter Name=\"C\"><FormulaTo>FROM</FormulaTo><FormulaFrom>TO</FormulaFrom><pValue>F</pValue></Converter><Float Name=\"F\"><pValue>C</pValue></Float>" };
    }

    [Theory]
    [MemberData(nameof(Cycles))]
    public void ReferenceCycle_IsDetectedAtBind(string label, string body)
    {
        var ex = Assert.Throws<GenApiException>(() => RuntimeFixture.Bind(body, new MemoryPort()));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ex.NodeName);
        Assert.NotNull(label);
    }

    [Fact]
    public void InvalidatorAndSelectedCycles_AreAllowed()
    {
        // pInvalidator·pSelected 는 평가가 따라가지 않으므로 서로 가리켜도 된다
        var body = RuntimeFixture.IntReg("RA", "0x10") + RuntimeFixture.IntReg("RB", "0x14")
            + "<Integer Name=\"A\"><pValue>RA</pValue><pInvalidator>B</pInvalidator><pSelected>B</pSelected></Integer>"
            + "<Integer Name=\"B\"><pValue>RB</pValue><pInvalidator>A</pInvalidator><pSelected>A</pSelected></Integer>";

        var map = RuntimeFixture.Bind(body, new MemoryPort(), "A", "B");
        Assert.NotNull(map.GetInteger("A"));
    }

    [Fact]
    public async Task ForwardReferences_ResolveRegardlessOfDocumentOrder()
    {
        // 참조 대상이 문서 뒤에 있어도 되고, 열거 항목 변수(.Entry.)의 열거가 뒤에 있어도 된다
        var body = "<IntSwissKnife Name=\"IsOn\"><pVariable Name=\"M\">Mode</pVariable><pVariable Name=\"ON.Entry.On\">Mode</pVariable><Formula>M = ON.Entry.On</Formula></IntSwissKnife>"
            + RuntimeFixture.Integer("Width", "WidthReg")
            + "<Enumeration Name=\"Mode\"><EnumEntry Name=\"Off\"><Value>0</Value></EnumEntry><EnumEntry Name=\"On\"><Value>7</Value></EnumEntry><pValue>ModeReg</pValue></Enumeration>"
            + RuntimeFixture.IntReg("ModeReg", "0x20")
            + RuntimeFixture.IntReg("WidthReg", "0x10");
        var port = new MemoryPort();
        port.U32(0x20, 7);

        var map = RuntimeFixture.Bind(body, port, "Width", "Mode");

        Assert.Equal(1, await map.GetInteger("IsOn").GetAsync());
    }

    [Fact]
    public async Task EntryVariable_DoesNotFormAValueEdge()
    {
        // 열거의 술어가 그 열거의 .Entry. 상수를 쓰는 수식을 가리키는 흔한 형태 — 상수는 실행 중에 열거를 읽지 않으므로 순환이 아니다
        var body = "<Enumeration Name=\"TriggerMode\"><pIsAvailable>CanTrigger</pIsAvailable><EnumEntry Name=\"Off\"><Value>0</Value></EnumEntry><EnumEntry Name=\"On\"><Value>1</Value></EnumEntry><pValue>ModeReg</pValue></Enumeration>"
            + "<IntSwissKnife Name=\"CanTrigger\"><pVariable Name=\"CUR\">OtherReg</pVariable><pVariable Name=\"TRIG.Entry.On\">TriggerMode</pVariable><Formula>CUR = TRIG.Entry.On</Formula></IntSwissKnife>"
            + RuntimeFixture.IntReg("ModeReg", "0x10") + RuntimeFixture.IntReg("OtherReg", "0x20");
        var port = new MemoryPort();
        port.U32(0x20, 1);
        var map = RuntimeFixture.Bind(body, port, "TriggerMode");
        var mode = map.GetEnumeration("TriggerMode");

        Assert.True(await mode.IsAvailableAsync());
        Assert.Equal(1, await map.GetInteger("CanTrigger").GetAsync());
        // 열거를 써도 수식은 무효화되지 않는다 — 역방향 색인에 없다
        Assert.DoesNotContain((NodeBase)map.GetNode("CanTrigger")!, ((NodeBase)map.GetNode("TriggerMode")!).Dependents);
        await mode.SetAsync("On");
        Assert.Equal("On", await mode.GetAsync());
        Assert.Equal(1, await map.GetInteger("CanTrigger").GetAsync());
    }

    [Fact]
    public void NodesList_FollowsDocumentOrderAndIncludesEntries()
    {
        var map = GenApiNodeMap.Parse(GenApiFixtures.Minimal, new MemoryPort());
        Assert.Equal(new[] { "Root", "Width", "WidthReg", "Device" }, map.Nodes.Select(n => n.Name).ToArray());

        var groups = GenApiNodeMap.Parse(GenApiFixtures.Groups, new MemoryPort());
        var entry = groups.GetNode<IEnumEntry>("EnumEntry_PixelFormat_Mono8");
        Assert.Equal("Mono8", entry.Symbolic);
        Assert.Equal(1, entry.Value);
        Assert.Null(entry.NumericValue);
        Assert.Contains(groups.Nodes, n => ReferenceEquals(n, entry));
    }

    [Fact]
    public void NodeMetadata_PassesThrough()
    {
        var map = GenApiNodeMap.Parse(GenApiFixtures.Groups, new MemoryPort());
        var n = map.GetNode("DeviceModelName")!;

        Assert.Equal("Device Model Name", n.DisplayName);
        Assert.Equal("Model name as reported by the bootstrap registers.", n.Description);
        Assert.Equal("Model of the device", n.ToolTip);
        Assert.Equal(Visibility.Beginner, n.Visibility);
        Assert.False(n.IsStreamable);
        Assert.True(map.GetNode("Width")!.IsStreamable);
        Assert.Equal(Visibility.Guru, map.GetNode("DeviceFirmwareVersion")!.Visibility);
    }

    [Fact]
    public void StructRegEntries_ShareOneRegisterAndKeepOwnBits()
    {
        var map = GenApiNodeMap.Parse(GenApiFixtures.Groups, new MemoryPort());
        var size = map.GetInteger("GevSCPSPacketSize");
        var fire = map.GetInteger("GevSCPSFireTestPacket");
        var dnf = map.GetInteger("GevSCPSDoNotFragment");

        Assert.Equal(Visibility.Guru, ((INode)fire).Visibility);          // 항목의 값이 이긴다
        Assert.Equal(Visibility.Expert, ((INode)dnf).Visibility);          // StructReg 의 값을 물려받는다
        Assert.Equal("B", size.Unit);
        Assert.Equal(Representation.Linear, size.Representation);
        Assert.Equal(Representation.PureNumber, fire.Representation);
    }
}

/// <summary>술어 간선을 지나야 닫히는 순환은 경고만 남기고 바인딩된다 — 전역 로그 싱크를 바꾸므로 격리 컬렉션에서만 돈다.</summary>
[Collection(GevLogSinkCollection.Name)]
public class NodeBinderLogTests
{
    [Fact]
    public async Task GuardEdgeCycle_BindsWithAWarning()
    {
        // pIsAvailable 술어가 그 노드의 값을 읽는다: 술어는 내부 값 경로로 읽혀 재귀하지 않으므로 문서를 거부하지 않는다
        var body = "<Integer Name=\"A\"><pIsAvailable>K</pIsAvailable><Value>1</Value></Integer>"
            + "<IntSwissKnife Name=\"K\"><pVariable Name=\"X\">A</pVariable><Formula>X</Formula></IntSwissKnife>";
        var logged = new List<(GevLogLevel Level, string Source, string Message)>();
        var prevSink = GevLog.Sink;
        var prevLevel = GevLog.MinLevel;
        GenApiNodeMap map;
        try
        {
            GevLog.Sink = (lvl, src, msg, _) => logged.Add((lvl, src, msg));
            GevLog.MinLevel = GevLogLevel.Warn;
            map = RuntimeFixture.Bind(body, new MemoryPort(), "A");
        }
        finally
        {
            GevLog.Sink = prevSink;
            GevLog.MinLevel = prevLevel;
        }

        var warn = Assert.Single(logged, e => e.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GevLogLevel.Warn, warn.Level);
        Assert.Equal("GenApi.Runtime", warn.Source);
        Assert.Contains("A -> K -> A", warn.Message);

        var a = map.GetInteger("A");
        Assert.True(await a.IsAvailableAsync());
        Assert.Equal(1, await a.GetAsync());
        await a.SetAsync(0);
        Assert.False(await a.IsAvailableAsync());               // 술어가 자기 값을 읽으므로 이제 거짓
        var ex = await Assert.ThrowsAsync<GenApiException>(() => a.GetAsync().AsTask());
        Assert.Contains("not available", ex.Message);
    }
}
