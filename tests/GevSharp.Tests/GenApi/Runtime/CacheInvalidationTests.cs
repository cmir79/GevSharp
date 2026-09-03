using GevSharp.GenApi;
using GevSharp.GenApi.Runtime;
using GevSharp.Sim;
using static GevSharp.Tests.GenApi.Runtime.RuntimeFixture;

#pragma warning disable xUnit1051

namespace GevSharp.Tests.GenApi.Runtime;

/// <summary>레지스터 캐시 정책과 무효화 전파 — 포트 읽기 횟수로 확인한다.</summary>
public class CacheInvalidationTests
{
    [Fact]
    public async Task WriteThrough_Default_CachesReadsAndKeepsWrittenValue()
    {
        var port = new MemoryPort();
        port.U32(0x10, 5);
        var r = Bind(IntReg("R", "0x10"), port).GetInteger("R");

        Assert.Equal(5, await r.GetAsync());
        Assert.Equal(5, await r.GetAsync());
        Assert.Equal(1, port.ReadCount);

        await r.SetAsync(6);
        Assert.Equal(6, await r.GetAsync());
        Assert.Equal(1, port.ReadCount);                    // 쓴 값이 캐시에 남았다
        Assert.Equal(1, port.WriteCount);
    }

    [Fact]
    public async Task WriteAround_DropsCacheOnWrite()
    {
        var port = new MemoryPort();
        var r = Bind(IntReg("R", "0x10", "<Cachable>WriteAround</Cachable>"), port).GetInteger("R");

        await r.GetAsync();
        await r.GetAsync();
        Assert.Equal(1, port.ReadCount);
        await r.SetAsync(6);
        Assert.Equal(6, await r.GetAsync());
        Assert.Equal(2, port.ReadCount);
        await r.GetAsync();
        Assert.Equal(2, port.ReadCount);
    }

    [Fact]
    public async Task NoCache_ReadsEveryTime()
    {
        var port = new MemoryPort();
        var r = Bind(IntReg("R", "0x10", "<Cachable>NoCache</Cachable>"), port).GetInteger("R");

        await r.GetAsync();
        await r.GetAsync();
        await r.SetAsync(1);
        await r.GetAsync();
        Assert.Equal(3, port.ReadCount);
    }

    [Fact]
    public async Task PollingTime_ReadsEveryTime()
    {
        var port = new MemoryPort();
        var r = Bind(IntReg("R", "0x10", "<PollingTime>100</PollingTime>"), port).GetInteger("R");

        await r.GetAsync();
        port.U32(0x10, 42);
        Assert.Equal(42, await r.GetAsync());
        Assert.Equal(2, port.ReadCount);
    }

    [Fact]
    public async Task Selector_WriteDropsSelectedFeatureCache()
    {
        // SimCamera 와 같은 구조: GainSelector(pSelected Gain, GainRaw) → GainSelectorReg 가 GainRawReg 의 pIndex
        var port = new MemoryPort();
        port.U32(0x1001C, 15);
        port.U32(0x10020, 25);
        port.U32(0x10024, 35);
        var map = GenApiNodeMap.Parse(SimDevice.DefaultGenApiXml, port);
        var selector = map.GetEnumeration("GainSelector");
        var gain = map.GetFloat("Gain");
        var rawReg = (IntRegNode)map.GetNode("GainRawReg")!;

        Assert.Equal(1.5, await gain.GetAsync());
        var reads = port.ReadCount;
        Assert.Equal(1.5, await gain.GetAsync());
        Assert.Equal(reads, port.ReadCount);                // 캐시
        Assert.True(rawReg.Core.HasCache);

        await selector.SetAsync("DigitalAll");
        Assert.False(rawReg.Core.HasCache);                 // 셀렉터 쓰기가 선택 피처의 캐시를 버렸다
        Assert.Equal(2.5, await gain.GetAsync());
        Assert.Equal(reads + 1, port.ReadCount);            // 새 주소를 한 번 읽었다(셀렉터 레지스터는 WriteThrough 캐시)
        Assert.Equal(0x10020ul, port.Reads[^1].Address);

        await selector.SetAsync("DigitalRed");
        Assert.Equal(3.5, await gain.GetAsync());
        Assert.Equal(reads + 2, port.ReadCount);
        Assert.Equal(3.5, await map.GetInteger("GainRaw").GetAsync() / 10.0);
        Assert.Equal(reads + 2, port.ReadCount);
    }

    [Fact]
    public async Task Selector_WithFixedAddressTarget_StillDropsCache()
    {
        // 장치가 내부적으로 다중화하는 벤더 패턴: 셀렉터가 호스트 측 정수이고 선택 피처의 레지스터 주소는 고정
        var port = new MemoryPort();
        var body = "<Integer Name=\"Sel\"><Value>0</Value><pSelected>Gain</pSelected></Integer>"
            + Integer("Gain", "GainReg") + IntReg("GainReg", "0x10");
        var map = Bind(body, port);
        var gain = map.GetInteger("Gain");

        await gain.GetAsync();
        await gain.GetAsync();
        Assert.Equal(1, port.ReadCount);

        await map.GetInteger("Sel").SetAsync(1);
        port.U32(0x10, 99);
        Assert.Equal(99, await gain.GetAsync());
        Assert.Equal(2, port.ReadCount);
    }

    [Fact]
    public async Task Selector_TransitiveThroughSelectedNodes()
    {
        // Sel → pSelected A; A 를 참조하는 K(pVariable), K 를 pValue 로 쓰는 B — 전부 무효화된다
        var port = new MemoryPort();
        var body = "<Integer Name=\"Sel\"><Value>0</Value><pSelected>A</pSelected></Integer>"
            + Integer("A", "AReg") + IntReg("AReg", "0x10")
            + "<IntSwissKnife Name=\"K\"><pVariable Name=\"X\">A</pVariable><Formula>X + 1</Formula></IntSwissKnife>"
            + Integer("B", "K")
            + Integer("Other", "OtherReg") + IntReg("OtherReg", "0x20");
        var map = Bind(body, port);

        Assert.Equal(1, await map.GetInteger("B").GetAsync());
        await map.GetInteger("Other").GetAsync();
        Assert.Equal(2, port.ReadCount);

        await map.GetInteger("Sel").SetAsync(1);
        port.U32(0x10, 10);
        Assert.Equal(11, await map.GetInteger("B").GetAsync());
        Assert.Equal(3, port.ReadCount);
        await map.GetInteger("Other").GetAsync();
        Assert.Equal(3, port.ReadCount);                    // 무관한 노드는 그대로
    }

    [Fact]
    public async Task PInvalidator_WriteOfListedNodeDropsCache()
    {
        var port = new MemoryPort();
        var body = Integer("Latch", "LatchReg") + IntReg("LatchReg", "0x10")
            + "<Integer Name=\"Value\"><pInvalidator>Latch</pInvalidator><pValue>ValueReg</pValue></Integer>" + IntReg("ValueReg", "0x20")
            + Integer("Unrelated", "UnrelatedReg") + IntReg("UnrelatedReg", "0x30");
        var map = Bind(body, port);

        await map.GetInteger("Value").GetAsync();
        await map.GetInteger("Unrelated").GetAsync();
        Assert.Equal(2, port.ReadCount);

        await map.GetInteger("Latch").SetAsync(1);
        port.U32(0x20, 7);
        Assert.Equal(7, await map.GetInteger("Value").GetAsync());
        Assert.Equal(3, port.ReadCount);
        await map.GetInteger("Unrelated").GetAsync();
        Assert.Equal(3, port.ReadCount);
    }

    [Fact]
    public async Task PInvalidator_OnRegisterNode_DropsItsOwnCache()
    {
        // SimCamera: TimestampLatchValueReg 는 NoCache 이지만, 캐시되는 레지스터에 pInvalidator 가 붙은 경우
        var port = new MemoryPort();
        var body = "<Command Name=\"Latch\"><pValue>CtlReg</pValue><CommandValue>1</CommandValue></Command>" + IntReg("CtlReg", "0x10", access: "WO")
            + IntReg("ValueReg", "0x20", "<pInvalidator>Latch</pInvalidator>");
        var map = Bind(body, port);

        await map.GetInteger("ValueReg").GetAsync();
        await map.GetCommand("Latch").ExecuteAsync();
        port.U32(0x20, 123);
        Assert.Equal(123, await map.GetInteger("ValueReg").GetAsync());
        Assert.Equal(2, port.ReadCount);
    }

    [Fact]
    public async Task Dependency_TransitiveInvalidationThroughPValueAndPVariable()
    {
        // A(pValue AReg) ← K(pVariable A) ← B(pValue K) ← C(pInvalidator B, pValue CReg)
        var port = new MemoryPort();
        var body = Integer("A", "AReg") + IntReg("AReg", "0x10")
            + "<IntSwissKnife Name=\"K\"><pVariable Name=\"X\">A</pVariable><Formula>X * 2</Formula></IntSwissKnife>"
            + Integer("B", "K")
            + "<Integer Name=\"C\"><pInvalidator>B</pInvalidator><pValue>CReg</pValue></Integer>" + IntReg("CReg", "0x20");
        var map = Bind(body, port);

        await map.GetInteger("C").GetAsync();
        Assert.Equal(1, port.ReadCount);
        await map.GetInteger("A").SetAsync(3);              // A 가 쓰이면 K → B → (pInvalidator) C 까지
        port.U32(0x20, 8);
        Assert.Equal(8, await map.GetInteger("C").GetAsync());
        Assert.Equal(2, port.ReadCount);
        Assert.Equal(6, await map.GetInteger("B").GetAsync());
        Assert.Equal(2, port.ReadCount);                    // AReg 는 WriteThrough — 쓴 값이 캐시에 남아 다시 읽지 않는다
    }

    [Fact]
    public async Task Write_DoesNotInvalidateUnrelatedNodes()
    {
        var port = new MemoryPort();
        var body = Integer("X", "XReg") + IntReg("XReg", "0x10") + Integer("Y", "YReg") + IntReg("YReg", "0x20");
        var map = Bind(body, port);

        await map.GetInteger("X").GetAsync();
        await map.GetInteger("Y").GetAsync();
        await map.GetInteger("X").SetAsync(1);
        await map.GetInteger("Y").GetAsync();
        Assert.Equal(2, port.ReadCount);
    }

    [Fact]
    public async Task Invalidate_DropsOwnValueChainAndDependents()
    {
        var port = new MemoryPort();
        var body = Integer("Width", "WidthReg") + IntReg("WidthReg", "0x10")
            + "<IntSwissKnife Name=\"Payload\"><pVariable Name=\"W\">Width</pVariable><pVariable Name=\"H\">Height</pVariable><Formula>W * H</Formula></IntSwissKnife>"
            + Integer("Height", "HeightReg") + IntReg("HeightReg", "0x14");
        var map = Bind(body, port);
        port.U32(0x10, 4);
        port.U32(0x14, 3);

        Assert.Equal(12, await map.GetInteger("Payload").GetAsync());
        Assert.Equal(2, port.ReadCount);

        port.U32(0x10, 5);
        map.GetInteger("Width").Invalidate();
        Assert.Equal(15, await map.GetInteger("Payload").GetAsync());
        Assert.Equal(3, port.ReadCount);                    // WidthReg 만 다시 읽었다
    }

    [Fact]
    public async Task Invalidate_DropsIndexedSlotRegisters()
    {
        // pIndex 로 고른 슬롯 레지스터도 값 사슬이다 — 노드를 무효화하면(직접이든 닫힘으로든) 슬롯 전부를 버린다
        var port = new MemoryPort();
        port.U32(0x10, 100);
        port.U32(0x20, 200);
        var body = "<Integer Name=\"Sel\"><Value>0</Value></Integer>"
            + "<Integer Name=\"A\"><pIndex>Sel</pIndex><pValueIndexed Index=\"0\">R0</pValueIndexed><pValueIndexed Index=\"1\">R1</pValueIndexed></Integer>"
            + IntReg("R0", "0x10") + IntReg("R1", "0x20")
            + "<Integer Name=\"Latch\"><Value>0</Value></Integer>"
            + "<Integer Name=\"B\"><pInvalidator>Latch</pInvalidator><pValue>A</pValue></Integer>";
        var map = Bind(body, port);
        var a = map.GetInteger("A");

        Assert.Equal(100, await a.GetAsync());
        await map.GetInteger("Sel").SetAsync(1);
        Assert.Equal(200, await a.GetAsync());
        Assert.Equal(2, port.ReadCount);

        port.U32(0x10, 101);
        port.U32(0x20, 201);
        a.Invalidate();
        Assert.Equal(201, await a.GetAsync());               // 지금 고른 슬롯이 다시 읽혔다
        Assert.Equal(3, port.ReadCount);
        await map.GetInteger("Sel").SetAsync(0);
        Assert.Equal(101, await a.GetAsync());               // 셀렉터 쓰기도 슬롯 전부를 버린다 — 낡은 R0 이 아니다
        Assert.Equal(4, port.ReadCount);

        port.U32(0x10, 102);
        await map.GetInteger("Latch").SetAsync(1);           // Latch → (pInvalidator) B → (값 사슬) A → 슬롯 전부
        Assert.Equal(102, await map.GetInteger("B").GetAsync());
        Assert.Equal(5, port.ReadCount);
    }

    [Fact]
    public async Task InvalidateAll_DropsEverything()
    {
        var port = new MemoryPort();
        var body = Integer("X", "XReg") + IntReg("XReg", "0x10") + Integer("Y", "YReg") + IntReg("YReg", "0x20");
        var map = Bind(body, port);

        await map.GetInteger("X").GetAsync();
        await map.GetInteger("Y").GetAsync();
        map.InvalidateAll();
        port.U32(0x10, 1);
        port.U32(0x20, 2);
        Assert.Equal(1, await map.GetInteger("X").GetAsync());
        Assert.Equal(2, await map.GetInteger("Y").GetAsync());
        Assert.Equal(4, port.ReadCount);
    }

    [Fact]
    public async Task OverlappingMaskedRegisters_DropEachOtherOnWrite()
    {
        var port = new MemoryPort();
        var body = MaskedIntReg("Mode", "0x10", "<Bit>31</Bit>") + MaskedIntReg("Source", "0x10", "<LSB>27</LSB><MSB>24</MSB>");
        var map = Bind(body, port);
        var mode = map.GetInteger("Mode");
        var source = map.GetInteger("Source");

        await mode.GetAsync();
        await source.GetAsync();
        Assert.Equal(2, port.ReadCount);

        await source.SetAsync(2);                            // Source 의 캐시로 읽기-수정-쓰기 → Mode 의 캐시는 낡았으므로 버려진다
        Assert.Equal(0x20u, port.U32(0x10));
        Assert.Equal(0, await mode.GetAsync());
        Assert.Equal(3, port.ReadCount);
        await mode.SetAsync(1);
        Assert.Equal(0x21u, port.U32(0x10));
        Assert.Equal(2, await source.GetAsync());
        Assert.Equal(4, port.ReadCount);
    }

    [Fact]
    public async Task WriteThroughConverter_DoesNotRereadAfterWrite()
    {
        var port = new MemoryPort();
        var body = "<Converter Name=\"Gain\"><FormulaTo>FROM * 10</FormulaTo><FormulaFrom>TO / 10.0</FormulaFrom><pValue>Raw</pValue><Slope>Increasing</Slope></Converter>"
            + Integer("Raw", "RawReg", "<Min>0</Min><Max>1023</Max>") + IntReg("RawReg", "0x10");
        var gain = Bind(body, port).GetFloat("Gain");

        await gain.SetAsync(2.5);
        Assert.Equal(2.5, await gain.GetAsync());
        Assert.Equal(0, port.ReadCount);
        Assert.Equal(1, port.WriteCount);
    }

    [Fact]
    public async Task ConcurrentReads_AreSafe()
    {
        var port = new MemoryPort();
        port.U32(0x10, 640);
        var body = Integer("Width", "WidthReg") + IntReg("WidthReg", "0x10")
            + "<IntSwissKnife Name=\"K\"><pVariable Name=\"W\">Width</pVariable><Formula>W * 2</Formula></IntSwissKnife>";
        var map = Bind(body, port);

        var tasks = new List<Task<long>>();
        for (var i = 0; i < 64; i++)
        {
            tasks.Add(Task.Run(() => map.GetInteger("Width").GetAsync().AsTask()));
            tasks.Add(Task.Run(() => map.GetInteger("K").GetAsync().AsTask()));
        }
        var results = await Task.WhenAll(tasks);

        Assert.All(results, v => Assert.True(v == 640 || v == 1280));
        Assert.True(port.ReadCount >= 1);
        await map.GetInteger("Width").GetAsync();
        var settled = port.ReadCount;
        await map.GetInteger("K").GetAsync();
        Assert.Equal(settled, port.ReadCount);              // 경합이 끝나면 캐시 하나로 수렴
    }
}
