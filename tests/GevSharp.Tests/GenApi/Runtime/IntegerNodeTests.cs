using GevSharp.GenApi;
using GevSharp.GenApi.Runtime;
using static GevSharp.Tests.GenApi.Runtime.RuntimeFixture;

#pragma warning disable xUnit1051

namespace GevSharp.Tests.GenApi.Runtime;

/// <summary>정수 계열 — IntReg 바이트 순서·부호, Integer 의 값 출처와 범위 검사, MaskedIntReg 비트 번호 규약, 주소 계산, 큰 레지스터.</summary>
public class IntegerNodeTests
{
    // ---------------------------------------------------------------- IntReg

    [Fact]
    public async Task IntReg_BigEndian_RoundTrips()
    {
        var port = new MemoryPort();
        port.U32(0x1000, 0x12345678);
        var map = Bind(IntReg("R", "0x1000"), port);
        var r = map.GetInteger("R");

        Assert.Equal(0x12345678, await r.GetAsync());

        await r.SetAsync(0xAABBCCDD);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, port.Peek(0x1000, 4));
        Assert.Equal(0xAABBCCDD, await r.GetAsync());
    }

    [Fact]
    public async Task IntReg_LittleEndian_RoundTrips()
    {
        var port = new MemoryPort();
        port.Poke(0x1000, new byte[] { 0x78, 0x56, 0x34, 0x12 });
        var body = "<IntReg Name=\"R\"><Address>0x1000</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Endianess>LittleEndian</Endianess></IntReg>";
        var r = Bind(body, port).GetInteger("R");

        Assert.Equal(0x12345678, await r.GetAsync());

        await r.SetAsync(0x01020304);
        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, port.Peek(0x1000, 4));
    }

    [Fact]
    public async Task IntReg_DefaultEndianessIsLittle()
    {
        var port = new MemoryPort();
        port.Poke(0x1000, new byte[] { 0x01, 0x00, 0x00, 0x00 });
        var body = "<IntReg Name=\"R\"><Address>0x1000</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort></IntReg>";

        Assert.Equal(1, await Bind(body, port).GetInteger("R").GetAsync());
    }

    [Fact]
    public async Task IntReg_SignedShortRegister_SignExtends()
    {
        var port = new MemoryPort();
        port.Poke(0x1000, new byte[] { 0xFF, 0xFE });
        var body = IntReg("S", "0x1000", "<Sign>Signed</Sign>", length: 2) + IntReg("U", "0x1000", "<Sign>Unsigned</Sign>", length: 2);
        var map = Bind(body, port);
        var s = map.GetInteger("S");
        var u = map.GetInteger("U");

        Assert.Equal(-2, await s.GetAsync());
        Assert.Equal(65534, await u.GetAsync());
        Assert.Equal(-32768, await s.GetMinAsync());
        Assert.Equal(32767, await s.GetMaxAsync());
        Assert.Equal(0, await u.GetMinAsync());
        Assert.Equal(65535, await u.GetMaxAsync());
        Assert.Equal(1, await u.GetIncAsync());

        await s.SetAsync(-1);
        Assert.Equal(new byte[] { 0xFF, 0xFF }, port.Peek(0x1000, 2));
        await Assert.ThrowsAsync<GenApiException>(() => u.SetAsync(70000).AsTask());
        await Assert.ThrowsAsync<GenApiException>(() => s.SetAsync(40000).AsTask());
    }

    [Fact]
    public async Task IntReg_EightBytes_RoundTrips()
    {
        var port = new MemoryPort();
        port.U64(0x2000, 0x0123456789ABCDEFul);
        var r = Bind(IntReg("R", "0x2000", length: 8), port).GetInteger("R");

        Assert.Equal(0x0123456789ABCDEF, await r.GetAsync());
        Assert.Equal(long.MaxValue, await r.GetMaxAsync());

        await r.SetAsync(1L << 40);
        Assert.Equal(1ul << 40, port.U64(0x2000));
    }

    [Fact]
    public async Task IntReg_UnsignedEightBytes_AboveTheSignedRange_ThrowsNamingTheNode()
    {
        // 읽은 값과 노드가 알리는 범위(0..long.MaxValue)는 어긋나면 안 된다 — 조용히 음수로 감기면
        // 자기 범위 밖의 값을 내놓고 그 값을 다시 쓸 수도 없다.
        var port = new MemoryPort();
        port.U64(0x2000, 0xFFFFFFFFFFFFFFFFul);
        var r = Bind(IntReg("R", "0x2000", "<Sign>Unsigned</Sign>", length: 8), port).GetInteger("R");

        Assert.Equal(0, await r.GetMinAsync());
        Assert.Equal(long.MaxValue, await r.GetMaxAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => r.GetAsync().AsTask());
        Assert.Equal("R", ex.NodeName);
        Assert.Contains("18446744073709551615", ex.Message);

        port.U64(0x2000, (ulong)long.MaxValue);              // 상한 자체는 읽힌다
        r.Invalidate();
        Assert.Equal(long.MaxValue, await r.GetAsync());
    }

    [Fact]
    public async Task MaskedIntReg_UnsignedFullWidthField_AboveTheSignedRange_ThrowsNamingTheNode()
    {
        var port = new MemoryPort();
        port.U64(0x2000, 0x8000000000000000ul);
        var body = "<MaskedIntReg Name=\"M\"><Address>0x2000</Address><Length>8</Length><AccessMode>RW</AccessMode><pPort>Device</pPort>"
            + "<LSB>0</LSB><MSB>63</MSB><Sign>Unsigned</Sign><Endianess>BigEndian</Endianess></MaskedIntReg>";
        var m = Bind(body, port).GetInteger("M");

        var ex = await Assert.ThrowsAsync<GenApiException>(() => m.GetAsync().AsTask());
        Assert.Equal("M", ex.NodeName);
        Assert.Contains("9223372036854775808", ex.Message);
    }

    [Fact]
    public void IntReg_LengthOutsideOneToEight_FailsAtBind()
    {
        var ex = Assert.Throws<GenApiException>(() => Bind(IntReg("R", "0x2000", length: 16), new MemoryPort()));
        Assert.Equal("R", ex.NodeName);
    }

    // ---------------------------------------------------------------- Integer

    [Fact]
    public async Task Integer_LiteralValue_IsHostSideVariable()
    {
        var port = new MemoryPort();
        var i = Bind("<Integer Name=\"I\"><Value>5</Value><Min>0</Min><Max>10</Max></Integer>", port).GetInteger("I");

        Assert.Equal(5, await i.GetAsync());
        await i.SetAsync(7);
        Assert.Equal(7, await i.GetAsync());
        Assert.Equal(0, port.ReadCount + port.WriteCount);
        Assert.Equal(0, await i.GetMinAsync());
        Assert.Equal(10, await i.GetMaxAsync());
        Assert.Equal(1, await i.GetIncAsync());
    }

    [Fact]
    public async Task Integer_PValueChain_DelegatesReadsAndWrites()
    {
        var port = new MemoryPort();
        port.U32(0x10, 40);
        var body = Integer("A", "B") + Integer("B", "R") + IntReg("R", "0x10");
        var a = Bind(body, port).GetInteger("A");

        Assert.Equal(40, await a.GetAsync());
        await a.SetAsync(41);
        Assert.Equal(41u, port.U32(0x10));
        // 위임 대상의 한계값이 전파된다(4 바이트 unsigned)
        Assert.Equal(0, await a.GetMinAsync());
        Assert.Equal(0xFFFFFFFF, await a.GetMaxAsync());
    }

    [Fact]
    public async Task Integer_PValueCopy_WritesEveryTarget()
    {
        var port = new MemoryPort();
        var body = "<Integer Name=\"A\"><pValue>R1</pValue><pValueCopy>R2</pValueCopy><pValueCopy>R3</pValueCopy></Integer>"
            + IntReg("R1", "0x10") + IntReg("R2", "0x20") + IntReg("R3", "0x30");
        var a = Bind(body, port).GetInteger("A");

        await a.SetAsync(9);

        Assert.Equal(9u, port.U32(0x10));
        Assert.Equal(9u, port.U32(0x20));
        Assert.Equal(9u, port.U32(0x30));
        Assert.Equal(3, port.WriteCount);
    }

    [Fact]
    public async Task Integer_PIndex_SelectsIndexedValueOrDefault()
    {
        var port = new MemoryPort();
        port.U32(0x10, 100);
        port.U32(0x20, 300);
        var body = "<Integer Name=\"Sel\"><Value>0</Value></Integer>"
            + "<Integer Name=\"A\"><pIndex>Sel</pIndex><pValueIndexed Index=\"0\">R0</pValueIndexed><ValueIndexed Index=\"1\">42</ValueIndexed><pValueDefault>RD</pValueDefault></Integer>"
            + IntReg("R0", "0x10") + IntReg("RD", "0x20");
        var map = Bind(body, port);
        var sel = map.GetInteger("Sel");
        var a = map.GetInteger("A");

        Assert.Equal(100, await a.GetAsync());
        await a.SetAsync(101);
        Assert.Equal(101u, port.U32(0x10));

        await sel.SetAsync(1);
        Assert.Equal(42, await a.GetAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => a.SetAsync(1).AsTask());
        Assert.Contains("literal", ex.Message);

        await sel.SetAsync(5);
        Assert.Equal(300, await a.GetAsync());
        await a.SetAsync(301);
        Assert.Equal(301u, port.U32(0x20));
    }

    [Fact]
    public async Task Integer_PValueDefaultWithoutPIndex_IsTheValueSource()
    {
        var port = new MemoryPort();
        port.U32(0x10, 5);
        var body = "<Integer Name=\"A\"><pValueDefault>R</pValueDefault></Integer><Float Name=\"F\"><pValueDefault>R</pValueDefault></Float>" + IntReg("R", "0x10");
        var map = Bind(body, port);

        Assert.Equal(5, await map.GetInteger("A").GetAsync());
        Assert.Equal(5.0, await map.GetFloat("F").GetAsync());
        await map.GetInteger("A").SetAsync(6);
        Assert.Equal(6u, port.U32(0x10));
        await map.GetFloat("F").SetAsync(7.0);
        Assert.Equal(7u, port.U32(0x10));
        Assert.Equal(0xFFFFFFFF, await map.GetInteger("A").GetMaxAsync());
    }

    [Fact]
    public async Task Integer_IndexedSlotsWithoutPIndex_ReadTheirOtherValueSource()
    {
        // pIndex 가 없으면 인덱스 슬롯은 하나도 열리지 않는다. 그래도 Value·pValue·ValueDefault·pValueDefault 가 있으면
        // 읽기는 그쪽을 타 제 값을 낸다 — 슬롯이 유일한 값 출처일 때만 거절해야 하는 이유다(파싱이 던지면 노드맵 전체가 무너진다).
        var port = new MemoryPort();
        port.U32(0x10, 5);
        port.U32(0x20, 3);
        var body = IntReg("R", "0x10") + IntReg("D", "0x20")
            + "<Integer Name=\"ByPValue\"><pValue>R</pValue><ValueIndexed Index=\"0\">9</ValueIndexed></Integer>"
            + "<Integer Name=\"ByValueDefault\"><ValueDefault>3</ValueDefault><ValueIndexed Index=\"0\">9</ValueIndexed></Integer>"
            + "<Integer Name=\"ByPValueDefault\"><pValueDefault>D</pValueDefault><pValueIndexed Index=\"0\">R</pValueIndexed></Integer>"
            + "<Float Name=\"FloatByPValue\"><pValue>R</pValue><ValueIndexed Index=\"0\">9.5</ValueIndexed></Float>";
        var map = Bind(body, port);

        Assert.Equal(5, await map.GetInteger("ByPValue").GetAsync());
        Assert.Equal(3, await map.GetInteger("ByValueDefault").GetAsync());
        Assert.Equal(3, await map.GetInteger("ByPValueDefault").GetAsync());
        Assert.Equal(5.0, await map.GetFloat("FloatByPValue").GetAsync());

        // 슬롯만 있고 pIndex 도 다른 출처도 없는 모양만 값을 낼 길이 없다 — 조용히 0 을 내놓던 그 자리다.
        var ex = Assert.Throws<GenApiException>(
            () => Bind("<Integer Name=\"Orphan\"><ValueIndexed Index=\"0\">9</ValueIndexed></Integer>", new MemoryPort()));
        Assert.Equal("Orphan", ex.NodeName);
        Assert.Contains("only value source", ex.Message);
    }

    [Fact]
    public async Task Integer_PIndex_WithoutMatchOrDefault_IsNotAvailableAndThrowsOnRead()
    {
        var body = "<Integer Name=\"Sel\"><Value>3</Value></Integer>"
            + "<Integer Name=\"A\"><pIndex>Sel</pIndex><ValueIndexed Index=\"0\">1</ValueIndexed></Integer>";
        var map = Bind(body, new MemoryPort());
        var a = map.GetInteger("A");

        // 접근 모드 조회는 던지지 않는다 — 피처 트리를 훑는 GUI 가 여기서 죽으면 안 된다
        Assert.Equal(AccessMode.NotAvailable, await a.GetAccessModeAsync());
        Assert.True(await a.IsImplementedAsync());
        Assert.False(await a.IsAvailableAsync());
        Assert.False(await a.IsLockedAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => a.GetAsync().AsTask());
        Assert.Equal("A", ex.NodeName);
        Assert.Contains("index 3", ex.Message);
        Assert.Contains("Sel", ex.Message);
        var write = await Assert.ThrowsAsync<GenApiException>(() => a.SetAsync(1).AsTask());
        Assert.Contains("index 3", write.Message);

        await map.GetInteger("Sel").SetAsync(0);
        Assert.Equal(AccessMode.ReadWrite, await a.GetAccessModeAsync());
        Assert.Equal(1, await a.GetAsync());
    }

    [Fact]
    public async Task Integer_IncWithoutMin_IsEnforcedFromZero()
    {
        // Min 이 없어도 Inc 격자는 검사한다(0 기준) — 격자 밖 값을 조용히 받지 않는다
        var i = Bind("<Integer Name=\"I\"><Value>0</Value><Inc>4</Inc></Integer>", new MemoryPort()).GetInteger("I");

        Assert.Equal(long.MinValue, await i.GetMinAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => i.SetAsync(6).AsTask());
        Assert.Contains("increment", ex.Message);
        await i.SetAsync(8);
        await i.SetAsync(-12);
        Assert.Equal(-12, await i.GetAsync());
    }

    [Fact]
    public async Task Integer_IncGrid_IsAnchoredAtNegativeMinWithoutOverflow()
    {
        var i = Bind("<Integer Name=\"I\"><Value>-6</Value><Min>-6</Min><Max>10</Max><Inc>4</Inc></Integer>", new MemoryPort()).GetInteger("I");

        await i.SetAsync(-2);
        await i.SetAsync(6);
        await Assert.ThrowsAsync<GenApiException>(() => i.SetAsync(5).AsTask());
        await Assert.ThrowsAsync<GenApiException>(() => i.SetAsync(0).AsTask());
        Assert.True(IntegerNodeBase.IsOnGrid(long.MaxValue, long.MinValue + 1, 2));    // 뺄셈으로 계산하면 넘치는 조합
        Assert.False(IntegerNodeBase.IsOnGrid(long.MaxValue, long.MinValue + 2, 2));
    }

    [Fact]
    public async Task Integer_WriteOutsideRangeOrOffGrid_ThrowsWithoutClamping()
    {
        var port = new MemoryPort();
        port.U32(0x10, 16);
        var body = Integer("W", "R", "<Min>8</Min><Max>100</Max><Inc>4</Inc>") + IntReg("R", "0x10");
        var w = Bind(body, port).GetInteger("W");

        var low = await Assert.ThrowsAsync<GenApiException>(() => w.SetAsync(7).AsTask());
        Assert.Contains("range", low.Message);
        Assert.Equal("W", low.NodeName);
        var high = await Assert.ThrowsAsync<GenApiException>(() => w.SetAsync(101).AsTask());
        Assert.Contains("8..100", high.Message);
        var grid = await Assert.ThrowsAsync<GenApiException>(() => w.SetAsync(10).AsTask());
        Assert.Contains("increment", grid.Message);
        Assert.Equal(0, port.WriteCount);      // 어떤 실패도 장치에 닿지 않았다
        Assert.Equal(16u, port.U32(0x10));

        await w.SetAsync(12);
        Assert.Equal(12u, port.U32(0x10));
    }

    [Fact]
    public async Task Integer_PMinPMaxPInc_ComeFromNodes()
    {
        var port = new MemoryPort();
        var body = Integer("W", "R", "<pMin>MinN</pMin><pMax>MaxN</pMax><pInc>IncN</pInc>") + IntReg("R", "0x10")
            + "<Integer Name=\"MinN\"><Value>10</Value></Integer><Integer Name=\"MaxN\"><Value>64</Value></Integer><Integer Name=\"IncN\"><Value>2</Value></Integer>";
        var map = Bind(body, port);
        var w = map.GetInteger("W");

        Assert.Equal(10, await w.GetMinAsync());
        Assert.Equal(64, await w.GetMaxAsync());
        Assert.Equal(2, await w.GetIncAsync());
        await Assert.ThrowsAsync<GenApiException>(() => w.SetAsync(68).AsTask());
        await Assert.ThrowsAsync<GenApiException>(() => w.SetAsync(13).AsTask());
        await w.SetAsync(14);

        await map.GetInteger("MaxN").SetAsync(12);
        await Assert.ThrowsAsync<GenApiException>(() => w.SetAsync(14).AsTask());
    }

    [Fact]
    public async Task Integer_ValidValueSet_RejectsOtherValues()
    {
        var port = new MemoryPort();
        var body = Integer("B", "R", "<Min>1</Min><Max>4</Max><ValidValueSet>1;2;4</ValidValueSet>") + IntReg("R", "0x10");
        var b = Bind(body, port).GetInteger("B");

        var ex = await Assert.ThrowsAsync<GenApiException>(() => b.SetAsync(3).AsTask());
        Assert.Contains("valid value set", ex.Message);
        await b.SetAsync(4);
        Assert.Equal(4u, port.U32(0x10));
    }

    [Fact]
    public void Integer_RepresentationAndUnit_PassThrough()
    {
        var body = Integer("A", "R", "<Unit>px</Unit><Representation>HexNumber</Representation>") + IntReg("R", "0x10") + Integer("B", "R");
        var map = Bind(body, new MemoryPort());

        Assert.Equal("px", map.GetInteger("A").Unit);
        Assert.Equal(Representation.HexNumber, map.GetInteger("A").Representation);
        Assert.Null(map.GetInteger("B").Unit);
        Assert.Equal(Representation.PureNumber, map.GetInteger("B").Representation);
    }

    [Fact]
    public async Task Integer_OverEnumerationAndBoolean_ReadsTheirIntegerValue()
    {
        var port = new MemoryPort();
        port.U32(0x10, 2);
        port.U32(0x20, 1);
        var body = Integer("IE", "E") + Integer("IB", "B")
            + "<Enumeration Name=\"E\"><EnumEntry Name=\"A\"><Value>0</Value></EnumEntry><EnumEntry Name=\"C\"><Value>2</Value></EnumEntry><pValue>RE</pValue></Enumeration>"
            + "<Boolean Name=\"B\"><pValue>RB</pValue></Boolean>"
            + IntReg("RE", "0x10") + IntReg("RB", "0x20");
        var map = Bind(body, port);

        Assert.Equal(2, await map.GetInteger("IE").GetAsync());
        Assert.Equal(1, await map.GetInteger("IB").GetAsync());
        await map.GetInteger("IE").SetAsync(0);
        Assert.Equal(0u, port.U32(0x10));
        var ex = await Assert.ThrowsAsync<GenApiException>(() => map.GetInteger("IE").SetAsync(1).AsTask());
        Assert.Contains("matches no entry", ex.Message);
        await map.GetInteger("IB").SetAsync(0);
        Assert.Equal(0u, port.U32(0x20));
    }

    // ---------------------------------------------------------------- MaskedIntReg

    [Fact]
    public async Task MaskedIntReg_BigEndian_Lsb31Msb16_IsLowHalfWord()
    {
        var port = new MemoryPort();
        port.U32(0x2000, 0xAABB1234);
        var m = Bind(MaskedIntReg("M", "0x2000", "<LSB>31</LSB><MSB>16</MSB>"), port).GetInteger("M");

        Assert.Equal(0x1234, await m.GetAsync());
        Assert.Equal(0, await m.GetMinAsync());
        Assert.Equal(0xFFFF, await m.GetMaxAsync());

        await m.SetAsync(0x5678);
        Assert.Equal(0xAABB5678u, port.U32(0x2000));      // 다른 비트는 보존
        await Assert.ThrowsAsync<GenApiException>(() => m.SetAsync(0x10000).AsTask());
    }

    [Fact]
    public async Task MaskedIntReg_BigEndian_Bit31_IsIntegerBit0()
    {
        var port = new MemoryPort();
        port.U32(0x2000, 0xFFFFFFFE);
        var m = Bind(MaskedIntReg("M", "0x2000", "<Bit>31</Bit>"), port).GetInteger("M");

        Assert.Equal(0, await m.GetAsync());
        Assert.Equal(1, await m.GetMaxAsync());

        await m.SetAsync(1);
        Assert.Equal(0xFFFFFFFFu, port.U32(0x2000));
        await m.SetAsync(0);
        Assert.Equal(0xFFFFFFFEu, port.U32(0x2000));
    }

    [Fact]
    public async Task MaskedIntReg_BigEndian_Bit0_IsMostSignificantBit()
    {
        var port = new MemoryPort();
        port.U32(0x2000, 0x80000000);
        var m = Bind(MaskedIntReg("M", "0x2000", "<Bit>0</Bit>"), port).GetInteger("M");

        Assert.Equal(1, await m.GetAsync());
        await m.SetAsync(0);
        Assert.Equal(0u, port.U32(0x2000));
    }

    [Fact]
    public async Task MaskedIntReg_LittleEndian_Lsb0Msb7_IsLowByte()
    {
        var port = new MemoryPort();
        port.U32Le(0x2000, 0x12345678);
        var body = MaskedIntReg("Lo", "0x2000", "<LSB>0</LSB><MSB>7</MSB>", "LittleEndian")
            + MaskedIntReg("Mid", "0x2000", "<LSB>8</LSB><MSB>15</MSB>", "LittleEndian")
            + MaskedIntReg("B0", "0x2000", "<Bit>0</Bit>", "LittleEndian");
        var map = Bind(body, port);

        Assert.Equal(0x78, await map.GetInteger("Lo").GetAsync());
        Assert.Equal(0x56, await map.GetInteger("Mid").GetAsync());
        Assert.Equal(0, await map.GetInteger("B0").GetAsync());

        await map.GetInteger("Mid").SetAsync(0xAB);
        Assert.Equal(0x1234AB78u, port.U32Le(0x2000));
        await map.GetInteger("B0").SetAsync(1);
        Assert.Equal(0x1234AB79u, port.U32Le(0x2000));
    }

    [Fact]
    public async Task MaskedIntReg_SignedField_SignExtends()
    {
        var port = new MemoryPort();
        port.U32(0x2000, 0x000000FF);
        var body = MaskedIntReg("S", "0x2000", "<LSB>31</LSB><MSB>24</MSB><Sign>Signed</Sign>");
        var s = Bind(body, port).GetInteger("S");

        Assert.Equal(-1, await s.GetAsync());
        Assert.Equal(-128, await s.GetMinAsync());
        Assert.Equal(127, await s.GetMaxAsync());
        await s.SetAsync(-2);
        Assert.Equal(0x000000FEu, port.U32(0x2000));
    }

    [Fact]
    public void MaskedIntReg_BitBeyondRegister_FailsAtBind()
    {
        var ex = Assert.Throws<GenApiException>(() => Bind(MaskedIntReg("M", "0x2000", "<LSB>32</LSB><MSB>40</MSB>"), new MemoryPort()));
        Assert.Equal("M", ex.NodeName);
    }

    [Fact]
    public async Task MaskedIntReg_WriteOnlySharedRegister_PreservesSiblingFields()
    {
        // 쓰기 전용 레지스터는 읽을 수 없다 — 형제 필드의 마지막 쓰기(그림자)가 읽기-수정-쓰기의 바탕이 된다
        var port = new MemoryPort();
        port.U32(0x2000, 0xFFFFFFFF);
        var body = MaskedIntReg("A", "0x2000", "<LSB>31</LSB><MSB>16</MSB>", access: "WO") + MaskedIntReg("B", "0x2000", "<LSB>15</LSB><MSB>0</MSB>", access: "WO");
        var map = Bind(body, port);

        await map.GetInteger("A").SetAsync(0x1234);
        Assert.Equal(0, port.ReadCount);                    // 쓰기 전용은 읽지 않는다
        Assert.Equal(0x00001234u, port.U32(0x2000));        // 한 번도 쓰지 않은 비트는 0 으로

        await map.GetInteger("B").SetAsync(0xABCD);
        Assert.Equal(0xABCD1234u, port.U32(0x2000));        // A 가 쓴 비트가 남는다
        await map.GetInteger("A").SetAsync(0x5678);
        Assert.Equal(0xABCD5678u, port.U32(0x2000));        // B 가 쓴 비트도 남는다
        Assert.Equal(0, port.ReadCount);

        map.InvalidateAll();                                // 무효화는 그림자를 지우지 않는다 — 쓰기 전용 내용을 아는 길은 마지막 쓰기뿐이다
        await map.GetInteger("B").SetAsync(0x1);
        Assert.Equal(0x00015678u, port.U32(0x2000));
        Assert.Equal(0, port.ReadCount);
    }

    [Fact]
    public async Task StructReg_WriteOnly_EntriesPreserveEachOther()
    {
        var port = new MemoryPort();
        var body = "<StructReg Comment=\"control\"><Address>0x3000</Address><Length>4</Length><AccessMode>WO</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess>"
            + "<StructEntry Name=\"Mode\"><Bit>31</Bit></StructEntry><StructEntry Name=\"Source\"><LSB>27</LSB><MSB>24</MSB></StructEntry></StructReg>";
        var map = Bind(body, port);

        await map.GetInteger("Source").SetAsync(2);
        Assert.Equal(0x20u, port.U32(0x3000));
        await map.GetInteger("Mode").SetAsync(1);
        Assert.Equal(0x21u, port.U32(0x3000));              // Source 의 비트가 지워지지 않는다
        await map.GetInteger("Source").SetAsync(5);
        Assert.Equal(0x51u, port.U32(0x3000));
        Assert.Equal(0, port.ReadCount);
    }

    [Fact]
    public async Task MaskedIntReg_WriteOnlyWithPIndex_ShadowFollowsTheResolvedAddress()
    {
        // 그림자는 해석된 주소를 키로 삼는다 — 인덱스가 바뀌면 그 주소에는 쓴 적이 없으니 바탕은 0, 돌아오면 이전 쓰기가 남아 있다
        var port = new MemoryPort();
        var body = "<Integer Name=\"Sel\"><Value>0</Value></Integer>"
            + MaskedIntReg("A", "0x2000", "<LSB>31</LSB><MSB>16</MSB>", extra: "<pIndex Offset=\"4\">Sel</pIndex>", access: "WO")
            + MaskedIntReg("B", "0x2000", "<LSB>15</LSB><MSB>0</MSB>", extra: "<pIndex Offset=\"4\">Sel</pIndex>", access: "WO");
        var map = Bind(body, port);

        await map.GetInteger("A").SetAsync(0x1111);
        await map.GetInteger("B").SetAsync(0x2222);
        Assert.Equal(0x22221111u, port.U32(0x2000));

        await map.GetInteger("Sel").SetAsync(1);
        await map.GetInteger("B").SetAsync(0x3333);
        Assert.Equal(0x33330000u, port.U32(0x2004));
        Assert.Equal(0x22221111u, port.U32(0x2000));

        await map.GetInteger("Sel").SetAsync(0);
        await map.GetInteger("A").SetAsync(0x4444);
        Assert.Equal(0x22224444u, port.U32(0x2000));
        Assert.Equal(0, port.ReadCount);
    }

    [Fact]
    public async Task StructRegEntries_PreserveEachOther()
    {
        var port = new MemoryPort();
        var body = "<StructReg Comment=\"trigger\"><Address>0x3000</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess>"
            + "<StructEntry Name=\"Mode\"><Bit>31</Bit></StructEntry><StructEntry Name=\"Source\"><LSB>27</LSB><MSB>24</MSB></StructEntry></StructReg>";
        var map = Bind(body, port);
        var mode = map.GetInteger("Mode");
        var source = map.GetInteger("Source");

        await source.SetAsync(2);
        Assert.Equal(0x20u, port.U32(0x3000));
        await mode.SetAsync(1);
        Assert.Equal(0x21u, port.U32(0x3000));
        Assert.Equal(2, await source.GetAsync());
        Assert.Equal(15, await source.GetMaxAsync());
        await Assert.ThrowsAsync<GenApiException>(() => source.SetAsync(16).AsTask());
    }

    // ---------------------------------------------------------------- 주소 계산

    [Fact]
    public async Task Address_SumsLiteralPAddressPIndexAndInlineSwissKnife()
    {
        var port = new MemoryPort();
        var body = "<Integer Name=\"Base\"><Value>0x1000</Value></Integer><Integer Name=\"Sel\"><Value>2</Value></Integer><Integer Name=\"Off\"><Value>8</Value></Integer>"
            + "<IntReg Name=\"R\"><Address>0x100</Address><Address>0x10</Address><pAddress>Base</pAddress><pIndex Offset=\"4\">Sel</pIndex><pIndex pOffset=\"Off\">Sel</pIndex>"
            + "<IntSwissKnife><pVariable Name=\"IDX\">Sel</pVariable><Formula>IDX * 16</Formula></IntSwissKnife>"
            + "<Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess></IntReg>";
        var map = Bind(body, port);
        const ulong expected = 0x100 + 0x10 + 0x1000 + 2 * 4 + 2 * 8 + 2 * 16;
        port.U32(expected, 77);

        Assert.Equal(77, await map.GetInteger("R").GetAsync());
        Assert.Single(port.Reads);
        Assert.Equal(expected, port.Reads[0].Address);

        await map.GetInteger("Sel").SetAsync(3);
        Assert.Equal(0, await map.GetInteger("R").GetAsync());
        Assert.Equal(expected + 4 + 8 + 16, port.Reads[^1].Address);
    }

    [Fact]
    public async Task Address_PIndexWithoutOffset_UsesRegisterLength()
    {
        var port = new MemoryPort();
        port.U64(0x1010, 5);
        var body = "<Integer Name=\"Sel\"><Value>2</Value></Integer>"
            + "<IntReg Name=\"R\"><Address>0x1000</Address><pIndex>Sel</pIndex><Length>8</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess></IntReg>";

        Assert.Equal(5, await Bind(body, port).GetInteger("R").GetAsync());
        Assert.Equal(0x1010ul, port.Reads[0].Address);
    }

    [Fact]
    public async Task Address_NegativeResult_Throws()
    {
        var body = "<Integer Name=\"Neg\"><Value>-32</Value></Integer>"
            + "<IntReg Name=\"R\"><Address>0x10</Address><pAddress>Neg</pAddress><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort></IntReg>";

        var ex = await Assert.ThrowsAsync<GenApiException>(() => Bind(body, new MemoryPort()).GetInteger("R").GetAsync().AsTask());
        Assert.Contains("negative", ex.Message);
    }

    [Fact]
    public async Task Register_PLength_ComesFromNode()
    {
        var port = new MemoryPort();
        port.Poke(0x1000, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var body = "<Integer Name=\"Len\"><Value>6</Value></Integer>"
            + "<Register Name=\"R\"><Address>0x1000</Address><pLength>Len</pLength><AccessMode>RW</AccessMode><pPort>Device</pPort></Register>";
        var map = Bind(body, port);
        var r = map.GetRegister("R");

        Assert.Equal(6, await r.GetLengthAsync());
        var buf = new byte[8];
        await r.GetAsync(buf);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 0, 0 }, buf);

        await map.GetInteger("Len").SetAsync(0);
        await Assert.ThrowsAsync<GenApiException>(() => r.GetLengthAsync().AsTask());
    }

    [Fact]
    public async Task Register_LongerThan512Bytes_IsOnePortCall()
    {
        var port = new MemoryPort();
        var pattern = new byte[1024];
        for (var i = 0; i < pattern.Length; i++) pattern[i] = (byte)(i * 7);
        port.Poke(0x8000, pattern);
        var body = "<Register Name=\"LUT\"><Address>0x8000</Address><Length>1024</Length><AccessMode>RW</AccessMode><pPort>Device</pPort></Register>";
        var lut = Bind(body, port).GetRegister("LUT");

        var buf = new byte[1024];
        await lut.GetAsync(buf);
        Assert.Equal(pattern, buf);
        Assert.Single(port.Reads);
        Assert.Equal(1024, port.Reads[0].Data.Length);

        Array.Reverse(pattern);
        await lut.SetAsync(pattern);
        Assert.Single(port.Writes);
        Assert.Equal(1024, port.Writes[0].Data.Length);
        Assert.Equal(pattern, port.Peek(0x8000, 1024));
        Assert.Equal(0x8000ul, await lut.GetAddressAsync());
    }

    [Fact]
    public async Task IntSwissKnife_IsReadOnly()
    {
        var body = "<IntSwissKnife Name=\"K\"><Constant Name=\"C\">3</Constant><Formula>C * 2</Formula></IntSwissKnife>";
        var k = Bind(body, new MemoryPort()).GetInteger("K");

        Assert.Equal(6, await k.GetAsync());
        Assert.Equal(AccessMode.ReadOnly, await k.GetAccessModeAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => k.SetAsync(1).AsTask());
        Assert.Contains("read-only", ex.Message);
        Assert.Equal("K", ex.NodeName);
    }
}
