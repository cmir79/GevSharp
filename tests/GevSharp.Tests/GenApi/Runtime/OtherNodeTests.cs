using System.Text;
using GevSharp.GenApi;
using GevSharp.GenApi.Runtime;
using static GevSharp.Tests.GenApi.Runtime.RuntimeFixture;

#pragma warning disable xUnit1051

namespace GevSharp.Tests.GenApi.Runtime;

/// <summary>Boolean·Enumeration·Command·String·StringReg·Register·Category·Port 의 의미론.</summary>
public class OtherNodeTests
{
    private const string EnumBody =
        "<Enumeration Name=\"E\"><EnumEntry Name=\"A\"><Value>0</Value></EnumEntry><EnumEntry Name=\"B\"><Value>1</Value><Symbolic>Bee</Symbolic><NumericValue>1.5</NumericValue></EnumEntry>"
        + "<EnumEntry Name=\"C\"><Value>2</Value><pIsAvailable>CAvail</pIsAvailable></EnumEntry><EnumEntry Name=\"D\"><Value>3</Value><pIsImplemented>DImpl</pIsImplemented></EnumEntry><pValue>R</pValue></Enumeration>"
        + "<Boolean Name=\"CAvail\"><Value>false</Value></Boolean><Boolean Name=\"DImpl\"><Value>false</Value></Boolean>";

    // ---------------------------------------------------------------- Boolean

    [Fact]
    public async Task Boolean_OverRegister_UsesOnOffValues()
    {
        var port = new MemoryPort();
        var body = "<Boolean Name=\"B\"><pValue>R</pValue></Boolean><Boolean Name=\"C\"><pValue>R</pValue><OnValue>0xFF</OnValue><OffValue>0x10</OffValue></Boolean>" + IntReg("R", "0x10");
        var map = Bind(body, port);
        var b = map.GetBoolean("B");
        var c = map.GetBoolean("C");

        Assert.False(await b.GetAsync());
        await b.SetAsync(true);
        Assert.Equal(1u, port.U32(0x10));
        Assert.True(await b.GetAsync());
        await b.SetAsync(false);
        Assert.Equal(0u, port.U32(0x10));

        await c.SetAsync(true);
        Assert.Equal(0xFFu, port.U32(0x10));
        Assert.True(await c.GetAsync());
        await c.SetAsync(false);
        Assert.Equal(0x10u, port.U32(0x10));
        Assert.False(await c.GetAsync());

        port.U32(0x10, 5);
        map.InvalidateAll();
        Assert.True(await c.GetAsync());                    // 둘 다 아니면 0 이 아닌지로
        Assert.Equal(NodeKind.Boolean, ((INode)b).Kind);
    }

    [Fact]
    public async Task Boolean_Literal_IsHostSide()
    {
        var b = Bind("<Boolean Name=\"B\"><Value>true</Value></Boolean>", new MemoryPort()).GetBoolean("B");
        Assert.True(await b.GetAsync());
        await b.SetAsync(false);
        Assert.False(await b.GetAsync());
    }

    [Fact]
    public async Task Boolean_InternalValuePath_CarriesTruthNotTheDeviceValue()
    {
        // 내부 값 경로가 주고받는 것은 참/거짓(1/0)이다 — OnValue 가 0 인 반전 비트라도 1 을 쓰면 참, 참은 다시 1 로 읽힌다.
        // OnValue/OffValue 는 장치 쪽 표현이라 레지스터에만 나타난다.
        var port = new MemoryPort();
        port.U32(0x10, 0xFF);
        var body = Integer("I", "B")
            + "<Boolean Name=\"B\"><pValue>R</pValue><OnValue>0</OnValue><OffValue>1</OffValue></Boolean>" + IntReg("R", "0x10");
        var map = Bind(body, port);
        var i = map.GetInteger("I");
        var b = map.GetBoolean("B");

        await i.SetAsync(1);
        Assert.Equal(0u, port.U32(0x10));                   // 참 → OnValue(0)
        Assert.True(await b.GetAsync());
        Assert.Equal(1, await i.GetAsync());                // 왕복: 쓴 값이 그대로 돌아온다

        await i.SetAsync(0);
        Assert.Equal(1u, port.U32(0x10));                   // 거짓 → OffValue(1)
        Assert.False(await b.GetAsync());
        Assert.Equal(0, await i.GetAsync());
    }

    [Fact]
    public async Task Boolean_OverMaskedBit_SetsOnlyThatBit()
    {
        var port = new MemoryPort();
        port.U32(0x10, 0x12345678);
        var body = "<Boolean Name=\"B\"><pValue>M</pValue></Boolean>" + MaskedIntReg("M", "0x10", "<Bit>30</Bit>");
        var b = Bind(body, port).GetBoolean("B");

        Assert.False(await b.GetAsync());
        await b.SetAsync(true);
        Assert.Equal(0x1234567Au, port.U32(0x10));
    }

    // ---------------------------------------------------------------- Enumeration

    [Fact]
    public async Task Enumeration_MapsValuesAndSymbolics()
    {
        var port = new MemoryPort();
        port.U32(0x10, 1);
        var map = Bind(EnumBody + IntReg("R", "0x10"), port);
        var e = map.GetEnumeration("E");

        Assert.Equal("Bee", await e.GetAsync());
        Assert.Equal(1, await e.GetIntValueAsync());
        Assert.Equal(4, e.Entries.Count);
        Assert.Equal("Bee", e.GetEntry("Bee")!.Symbolic);
        Assert.Equal("Bee", e.GetEntry("B")!.Symbolic);      // 항목 이름으로도 찾는다
        Assert.Equal(1.5, e.GetEntry("Bee")!.NumericValue);
        Assert.Null(e.GetEntry("zz"));
        Assert.Equal(NodeKind.EnumEntry, e.Entries[0].Kind);

        await e.SetAsync("A");
        Assert.Equal(0u, port.U32(0x10));
        await e.SetIntValueAsync(1);
        Assert.Equal(1u, port.U32(0x10));
    }

    [Fact]
    public async Task Enumeration_NoMatchingEntry_ThrowsListingValue()
    {
        var port = new MemoryPort();
        port.U32(0x10, 7);
        var e = Bind(EnumBody + IntReg("R", "0x10"), port).GetEnumeration("E");

        var ex = await Assert.ThrowsAsync<GenApiException>(() => e.GetAsync().AsTask());
        Assert.Equal("E", ex.NodeName);
        Assert.Contains("7", ex.Message);
        Assert.Contains("Bee=1", ex.Message);
        await Assert.ThrowsAsync<GenApiException>(() => e.SetIntValueAsync(9).AsTask());
        Assert.Equal(7u, port.U32(0x10));
    }

    [Fact]
    public async Task Enumeration_DuplicateValue_PrefersTheImplementedEntry()
    {
        // 벤더 XML 에는 값이 같은 항목이 실제로 있다 — 옛 이름과 새 이름이 같은 값을 쓰고 각자 다른 존재 여부 술어를 단다.
        // 앞의 것을 무조건 고르면, 구현되지 않은 이름을 돌려주고(가용 목록에 없는 이름이다) 그 값으로의 쓰기도 거절한다.
        const string body =
            "<Enumeration Name=\"E\">"
            + "<EnumEntry Name=\"OldName\"><Value>6</Value><pIsImplemented>OldPresent</pIsImplemented></EnumEntry>"
            + "<EnumEntry Name=\"NewName\"><Value>6</Value><pIsImplemented>NewPresent</pIsImplemented></EnumEntry>"
            + "<pValue>R</pValue></Enumeration>"
            + "<Boolean Name=\"OldPresent\"><Value>false</Value></Boolean><Boolean Name=\"NewPresent\"><Value>true</Value></Boolean>";
        var port = new MemoryPort();
        port.U32(0x10, 6);
        var e = Bind(body + IntReg("R", "0x10"), port).GetEnumeration("E");

        // 읽은 이름은 가용 목록에 실제로 있는 이름이어야 한다.
        var name = await e.GetAsync();
        Assert.Equal("NewName", name);
        var available = await e.GetAvailableEntriesAsync();
        Assert.Contains(available, x => x.Symbolic == name);

        // 같은 값으로의 쓰기도 구현된 항목을 골라 통과해야 한다.
        port.U32(0x10, 0);
        await e.SetIntValueAsync(6);
        Assert.Equal(6u, port.U32(0x10));
    }

    [Fact]
    public async Task Enumeration_UnknownSymbolic_Throws()
    {
        var e = Bind(EnumBody + IntReg("R", "0x10"), new MemoryPort()).GetEnumeration("E");
        var ex = await Assert.ThrowsAsync<GenApiException>(() => e.SetAsync("Nope").AsTask());
        Assert.Contains("'Nope'", ex.Message);
    }

    [Fact]
    public async Task Enumeration_EntryGuards_FilterAvailableEntriesAndRejectSet()
    {
        var map = Bind(EnumBody + IntReg("R", "0x10"), new MemoryPort());
        var e = map.GetEnumeration("E");

        var avail = await e.GetAvailableEntriesAsync();
        Assert.Equal(new[] { "A", "Bee" }, avail.Select(x => x.Symbolic).ToArray());
        var notAvail = await Assert.ThrowsAsync<GenApiException>(() => e.SetAsync("C").AsTask());
        Assert.Contains("not available", notAvail.Message);
        var notImpl = await Assert.ThrowsAsync<GenApiException>(() => e.SetAsync("D").AsTask());
        Assert.Contains("not implemented", notImpl.Message);
        Assert.False(await e.Entries[2].IsAvailableAsync());
        Assert.False(await e.Entries[3].IsImplementedAsync());

        await map.GetBoolean("CAvail").SetAsync(true);
        await e.SetAsync("C");
        Assert.Equal(3, (await e.GetAvailableEntriesAsync()).Count);
    }

    [Fact]
    public async Task Enumeration_Literal_IsHostSide()
    {
        var body = "<Enumeration Name=\"E\"><EnumEntry Name=\"Off\"><Value>0</Value></EnumEntry><EnumEntry Name=\"Timed\"><Value>1</Value></EnumEntry><Value>1</Value></Enumeration>";
        var e = Bind(body, new MemoryPort()).GetEnumeration("E");

        Assert.Equal("Timed", await e.GetAsync());
        await e.SetAsync("Off");
        Assert.Equal("Off", await e.GetAsync());
    }

    // ---------------------------------------------------------------- Command

    [Fact]
    public async Task Command_WritesCommandValueAndIsDoneWithoutPolling()
    {
        var port = new MemoryPort();
        var body = "<Command Name=\"Go\"><pValue>R</pValue><CommandValue>3</CommandValue></Command>"
            + "<Command Name=\"Go2\"><pValue>R</pValue><pCommandValue>CV</pCommandValue></Command><Integer Name=\"CV\"><Value>5</Value></Integer>"
            + "<Command Name=\"Go3\"><pValue>R</pValue></Command>"
            + IntReg("R", "0x10");
        var map = Bind(body, port);

        await map.GetCommand("Go").ExecuteAsync();
        Assert.Equal(3u, port.U32(0x10));
        Assert.True(await map.GetCommand("Go").IsDoneAsync());
        await map.GetCommand("Go2").ExecuteAsync();
        Assert.Equal(5u, port.U32(0x10));
        await map.GetCommand("Go3").ExecuteAsync();
        Assert.Equal(1u, port.U32(0x10));                   // 기본 명령 값 1
        Assert.Equal(NodeKind.Command, ((INode)map.GetCommand("Go")).Kind);
    }

    [Fact]
    public async Task Command_WithPollingTime_ReadsBackUntilBitClears()
    {
        var port = new MemoryPort();
        var body = "<Command Name=\"Start\"><pValue>R</pValue><CommandValue>1</CommandValue><PollingTime>10</PollingTime></Command>" + IntReg("R", "0x10");
        var start = Bind(body, port).GetCommand("Start");

        await start.ExecuteAsync();
        Assert.Equal(1u, port.U32(0x10));
        Assert.False(await start.IsDoneAsync());
        var reads = port.ReadCount;

        port.U32(0x10, 0);                                  // 장치가 비트를 지웠다 — 캐시가 아니라 장치를 읽어야 보인다
        Assert.True(await start.IsDoneAsync());
        Assert.Equal(reads + 1, port.ReadCount);
    }

    [Fact]
    public async Task Command_SelfClearingDevice_IsDoneImmediately()
    {
        var port = new MemoryPort { AfterWrite = (addr, _) => { } };
        port.AfterWrite = (addr, data) => { if (addr == 0x10) port.U32(0x10, 0); };
        var body = "<Command Name=\"Start\"><pValue>R</pValue><CommandValue>1</CommandValue><PollingTime>10</PollingTime></Command>" + IntReg("R", "0x10", "<Cachable>NoCache</Cachable>");
        var start = Bind(body, port).GetCommand("Start");

        await start.ExecuteAsync();
        Assert.True(await start.IsDoneAsync());
    }

    [Fact]
    public async Task Command_WithoutRegister_ExecutesLocally()
    {
        var port = new MemoryPort();
        var map = Bind("<Command Name=\"Noop\"><Value>0</Value><CommandValue>1</CommandValue></Command>", port);
        var c = map.GetCommand("Noop");
        var node = (CommandNode)map.GetNode("Noop")!;

        Assert.Equal(0, (await node.ReadValueAsync(default)).AsInt64);
        await c.ExecuteAsync();
        Assert.True(await c.IsDoneAsync());
        Assert.Equal(0, port.WriteCount);
        Assert.Equal(1, (await node.ReadValueAsync(default)).AsInt64);   // 리터럴 Value 는 호스트 측 변수 — 실행이 값으로 남는다
    }

    // ---------------------------------------------------------------- String / StringReg

    [Fact]
    public async Task String_Literal_IsHostSide()
    {
        var map = Bind("<String Name=\"S\"><Value>1.2.3</Value></String><String Name=\"Empty\"><Value></Value></String>", new MemoryPort());
        var s = map.GetString("S");

        Assert.Equal("1.2.3", await s.GetAsync());
        Assert.Equal("", await map.GetString("Empty").GetAsync());
        await s.SetAsync("x");
        Assert.Equal("x", await s.GetAsync());
        Assert.Equal(int.MaxValue, await s.GetMaxLengthAsync());
        Assert.Equal(NodeKind.String, ((INode)s).Kind);
    }

    [Fact]
    public async Task StringReg_TrimsAtNulAndPadsWithNul()
    {
        var port = new MemoryPort();
        port.Str(0x100, 8, "hi");
        var body = "<StringReg Name=\"S\"><Address>0x100</Address><Length>8</Length><AccessMode>RW</AccessMode><pPort>Device</pPort></StringReg>";
        var s = Bind(body, port).GetString("S");

        Assert.Equal("hi", await s.GetAsync());
        Assert.Equal(8, await s.GetMaxLengthAsync());

        await s.SetAsync("hello");
        Assert.Equal(new byte[] { (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0, 0, 0 }, port.Peek(0x100, 8));
        Assert.Equal("hello", await s.GetAsync());

        await s.SetAsync("12345678");                        // 정확히 Length 바이트는 종료 NUL 없이 들어간다
        Assert.Equal(Encoding.ASCII.GetBytes("12345678"), port.Peek(0x100, 8));
        Assert.Equal("12345678", await s.GetAsync());

        var ex = await Assert.ThrowsAsync<GenApiException>(() => s.SetAsync("123456789").AsTask());
        Assert.Equal("S", ex.NodeName);
        Assert.Contains("8", ex.Message);
        Assert.Equal("12345678", await s.GetAsync());

        await s.SetAsync("");
        Assert.Equal(new byte[8], port.Peek(0x100, 8));
        Assert.Equal("", await s.GetAsync());
    }

    [Fact]
    public async Task StringReg_CountsUtf8Bytes()
    {
        var port = new MemoryPort();
        var body = "<StringReg Name=\"S\"><Address>0x100</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort></StringReg>";
        var s = Bind(body, port).GetString("S");

        await s.SetAsync("é1");                              // 3 바이트
        Assert.Equal("é1", await s.GetAsync());
        await Assert.ThrowsAsync<GenApiException>(() => s.SetAsync("ééé").AsTask());   // 6 바이트
    }

    [Fact]
    public async Task String_OverStringReg_DelegatesAndReportsLength()
    {
        var port = new MemoryPort();
        port.Str(0x100, 16, "cam");
        var body = "<String Name=\"UserId\"><pValue>UserIdReg</pValue></String>"
            + "<StringReg Name=\"UserIdReg\"><Address>0x100</Address><Length>16</Length><AccessMode>RW</AccessMode><pPort>Device</pPort></StringReg>";
        var s = Bind(body, port).GetString("UserId");

        Assert.Equal("cam", await s.GetAsync());
        Assert.Equal(16, await s.GetMaxLengthAsync());
        await s.SetAsync("bench");
        Assert.Equal("bench", port.Str(0x100, 16));
    }

    // ---------------------------------------------------------------- Register / Category / Port

    [Fact]
    public async Task Register_TooSmallBuffer_ThrowsBeforeReadingTheDevice()
    {
        // 짧은 버퍼는 왕복 전에 걸러야 한다 — 장치가 정한 길이만큼 읽어 놓고 버리면 그 자체가 비용이다.
        var port = new MemoryPort();
        var body = "<Register Name=\"Blob\"><Address>0x400</Address><Length>1024</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Cachable>NoCache</Cachable></Register>";
        var r = Bind(body, port).GetRegister("Blob");

        var ex = await Assert.ThrowsAsync<GenApiException>(() => r.GetAsync(new byte[8]).AsTask());
        Assert.Equal("Blob", ex.NodeName);
        Assert.Contains("1024", ex.Message);
        Assert.Equal(0, port.ReadCount);

        await r.GetAsync(new byte[1024]);
        Assert.Equal(1, port.ReadCount);
    }

    [Fact]
    public async Task Register_PLengthBeyondTheBound_ThrowsNamingTheBound()
    {
        // 길이를 장치가 정하는 자리 — 상한이 없으면 값 하나로 호스트 메모리를 통째로 잡는다.
        var port = new MemoryPort();
        port.U32(0x10, 0x40000000);                          // 1 GiB
        var body = "<Register Name=\"Blob\"><Address>0x400</Address><pLength>Len</pLength><AccessMode>RW</AccessMode><pPort>Device</pPort></Register>"
            + Integer("Len", "LenReg") + IntReg("LenReg", "0x10");
        var r = Bind(body, port).GetRegister("Blob");

        var ex = await Assert.ThrowsAsync<GenApiException>(() => r.GetAsync(new byte[16]).AsTask());
        Assert.Equal("Blob", ex.NodeName);
        Assert.Contains("1073741824", ex.Message);
        Assert.Contains(GevSharp.GenApi.Runtime.RegisterCore.MaxLength.ToString(), ex.Message);
        Assert.Equal(1, port.ReadCount);                     // 길이 노드 한 번만 — Blob 자체는 읽지 않았다
    }

    [Fact]
    public async Task StringReg_PLengthBeyondTheBound_ThrowsWithoutAllocating()
    {
        // 호출자 버퍼가 없는 읽기 경로 — StringReg·IntReg·FloatReg 는 장치가 정한 길이만큼 배열을 잡고 시작한다.
        // 버퍼 길이 검사가 걸러 줄 수 없는 자리라 상한이 유일한 방어다.
        var port = new MemoryPort();
        port.U32(0x10, 0x40000000);                          // 1 GiB
        var body = "<StringReg Name=\"S\"><Address>0x100</Address><pLength>Len</pLength><AccessMode>RW</AccessMode><pPort>Device</pPort></StringReg>"
            + Integer("Len", "LenReg") + IntReg("LenReg", "0x10");
        var s = Bind(body, port).GetString("S");

        var ex = await Assert.ThrowsAsync<GenApiException>(() => s.GetAsync().AsTask());
        Assert.Equal("S", ex.NodeName);
        Assert.Contains("1073741824", ex.Message);
        Assert.Contains(GevSharp.GenApi.Runtime.RegisterCore.MaxLength.ToString(), ex.Message);
        Assert.Equal(1, port.ReadCount);                     // 길이 노드 한 번만 — 문자열 자체는 읽지 않았다
    }

    [Fact]
    public async Task Register_RawBytes_RoundTrip()
    {
        var port = new MemoryPort();
        port.Poke(0x400, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
        var body = "<Register Name=\"Blob\"><Address>0x400</Address><Length>16</Length><AccessMode>RW</AccessMode><pPort>Device</pPort></Register>";
        var r = Bind(body, port).GetRegister("Blob");

        Assert.Equal(0x400ul, await r.GetAddressAsync());
        Assert.Equal(16, await r.GetLengthAsync());
        var buf = new byte[16];
        await r.GetAsync(buf);
        Assert.Equal(port.Peek(0x400, 16), buf);
        await Assert.ThrowsAsync<GenApiException>(() => r.GetAsync(new byte[8]).AsTask());

        var data = new byte[16];
        data.AsSpan().Fill((byte)0xEE);
        await r.SetAsync(data);
        Assert.Equal(data, port.Peek(0x400, 16));
        var wrong = await Assert.ThrowsAsync<GenApiException>(() => r.SetAsync(new byte[4]).AsTask());
        Assert.Contains("16", wrong.Message);
        Assert.Equal(NodeKind.Register, ((INode)r).Kind);
    }

    [Fact]
    public void Category_ListsFeaturesInOrder()
    {
        var body = "<Category Name=\"Sub\"><pFeature>B</pFeature><pFeature>A</pFeature><pFeature>Deeper</pFeature></Category><Category Name=\"Deeper\"/>"
            + "<Integer Name=\"A\"><Value>1</Value></Integer><Integer Name=\"B\"><Value>2</Value></Integer>";
        var map = Bind(body, new MemoryPort(), "Sub");

        Assert.Equal(new[] { "Sub" }, map.Root.Features.Select(f => f.Name).ToArray());
        var sub = map.GetCategory("Sub");
        Assert.Equal(new[] { "B", "A", "Deeper" }, sub.Features.Select(f => f.Name).ToArray());
        Assert.Empty(map.GetCategory("Deeper").Features);
        Assert.Equal(NodeKind.Category, ((INode)sub).Kind);
    }

    [Fact]
    public async Task RegisterOnAChunkPortIsNotAvailable_RatherThanReadingTheDeviceAddress()
    {
        // ChunkID 가 달린 포트는 "값이 장치가 아니라 프레임의 청크 데이터에 있다" 는 선언이다.
        // 그 배선이 없는데 장치 주소에서 읽어 주면, 쓰는 쪽은 그럴듯한 숫자를 받고 그것을 믿는다 —
        // 실기에서 실제로 그랬다: ChunkExposureTime 이 512, ChunkStride 가 512 로 나왔다(진짜 값은 3000 us, 4096).
        // 모르면 모른다고 해야 한다.
        var port = new MemoryPort();
        var body = "<Port Name=\"Chunk\"><ChunkID>4001</ChunkID><SwapEndianess>Yes</SwapEndianess></Port>"
            + "<IntReg Name=\"R\"><Address>0x10</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Chunk</pPort><Endianess>BigEndian</Endianess></IntReg>";
        var map = Bind(body, port);
        var p = map.GetNode<IPortNode>("Chunk");

        Assert.Same(port, p.Port);
        Assert.Equal(NodeKind.Port, p.Kind);

        port.U32(0x10, 9);                                       // 장치 주소에는 값이 있다 — 그래도 내주지 않는다
        var r = map.GetInteger("R");
        Assert.Equal(AccessMode.NotAvailable, await ((INode)r).GetAccessModeAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => r.GetAsync().AsTask());
        Assert.Contains("chunk data", ex.Message, StringComparison.Ordinal);
        Assert.Equal("R", ex.NodeName);
        await Assert.ThrowsAsync<GenApiException>(() => r.SetAsync(1).AsTask());
    }

    [Fact]
    public async Task ChunkValueDoesNotLeakThroughAFormula()
    {
        // 접근 모드로만 막으면 새는 자리가 있다 — 수식(SwissKnife)의 변수 읽기는 접근 모드를 거치지 않고 값을 바로 읽는다.
        // 실기에서 정확히 그 경로로 샜다: ChunkExposureTime → SwissKnife → 청크 IntReg 가 장치 주소 0x0 을 읽어 512 를 내놓았다.
        // 포트에 닿기 직전에서 막아야 하고, 그 노드의 가용 술어까지 청크를 읽어야 한다면 답을 낼 수 없으니 NotAvailable 이다.
        var port = new MemoryPort();
        port.U32(0x10, 512);                                     // 장치 주소에는 무관한 값이 있다
        var body = "<Port Name=\"Chunk\"><ChunkID>ef86b3e0</ChunkID></Port>"
            + "<IntReg Name=\"Raw\"><Address>0x10</Address><Length>4</Length><AccessMode>RO</AccessMode><pPort>Chunk</pPort><Endianess>BigEndian</Endianess></IntReg>"
            + "<IntSwissKnife Name=\"Avail\"><pVariable Name=\"P1\">Raw</pVariable><Formula>P1&lt;&gt;0xffffffff</Formula></IntSwissKnife>"
            + "<SwissKnife Name=\"Conv\"><pVariable Name=\"P1\">Raw</pVariable><Formula>P1</Formula></SwissKnife>"
            + "<Float Name=\"ChunkExposure\"><pIsAvailable>Avail</pIsAvailable><pValue>Conv</pValue></Float>";
        var map = Bind(body, port);

        // 가용 술어가 청크를 읽어야 답할 수 있다 — 답을 낼 수 없으므로 NotAvailable 이지, 접근 모드 조회가 던지지는 않는다.
        var node = map.GetFloat("ChunkExposure");
        Assert.Equal(AccessMode.NotAvailable, await ((INode)node).GetAccessModeAsync());

        // 수식을 직접 밟아도 512 가 값인 척 나오지 않는다.
        var ex = await Assert.ThrowsAsync<GenApiException>(() => map.GetInteger("Avail").GetAsync().AsTask());
        Assert.True(ex.Data.Contains(GenApiException.ChunkDataKey));
        Assert.Contains("chunk data", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterOnAPlainPortStillReadsTheDeviceAddress()
    {
        // 대조군 — ChunkID 가 없으면 예전과 똑같이 장치 레지스터 공간으로 간다.
        var port = new MemoryPort();
        var body = "<Port Name=\"Dev\"><SwapEndianess>Yes</SwapEndianess></Port>"
            + "<IntReg Name=\"R\"><Address>0x10</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Dev</pPort><Endianess>BigEndian</Endianess></IntReg>";
        var map = Bind(body, port);
        port.U32(0x10, 9);
        Assert.Equal(9, await map.GetInteger("R").GetAsync());
    }

    [Fact]
    public async Task GenericNode_HasNoValueButGuardsWork()
    {
        var body = "<Node Name=\"Meta\"><pIsImplemented>Impl</pIsImplemented></Node><Integer Name=\"Impl\"><Value>0</Value></Integer>";
        var n = Bind(body, new MemoryPort()).GetNode("Meta")!;

        Assert.Equal(NodeKind.Unknown, n.Kind);
        Assert.False(await n.IsImplementedAsync());
        Assert.Equal(AccessMode.NotImplemented, await n.GetAccessModeAsync());
    }
}
