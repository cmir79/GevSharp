using GevSharp.GenApi;
using GevSharp.GenApi.Model;
using GevSharp.GenApi.Runtime;
using static GevSharp.Tests.GenApi.Runtime.RuntimeFixture;

#pragma warning disable xUnit1051

namespace GevSharp.Tests.GenApi.Runtime;

/// <summary>실수 계열 — FloatReg IEEE 인코딩, Float 의 값 출처, Converter/IntConverter 왕복과 Slope 한계값, SwissKnife 평가.</summary>
public class FloatNodeTests
{
    private static string FloatReg(string name, string address, int length, string endianess = "BigEndian", string access = "RW")
        => $"<FloatReg Name=\"{name}\"><Address>{address}</Address><Length>{length}</Length><AccessMode>{access}</AccessMode><pPort>Device</pPort><Endianess>{endianess}</Endianess></FloatReg>";

    // ---------------------------------------------------------------- FloatReg

    [Fact]
    public async Task FloatReg_Single_BigEndian_RoundTrips()
    {
        var port = new MemoryPort();
        port.F32(0x3000, 1.5f);
        var f = Bind(FloatReg("F", "0x3000", 4), port).GetFloat("F");

        Assert.Equal(1.5, await f.GetAsync());
        Assert.Equal(-float.MaxValue, await f.GetMinAsync());
        Assert.Equal(float.MaxValue, await f.GetMaxAsync());
        Assert.Null(await f.GetIncAsync());

        await f.SetAsync(2.25);
        Assert.Equal(2.25f, port.F32(0x3000));
        Assert.Equal(new byte[] { 0x40, 0x10, 0x00, 0x00 }, port.Peek(0x3000, 4));
    }

    [Fact]
    public async Task FloatReg_Double_LittleEndian_RoundTrips()
    {
        var port = new MemoryPort();
        var bits = BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(-3.75));   // 호스트가 리틀엔디언이면 LE 바이트
        if (!BitConverter.IsLittleEndian) Array.Reverse(bits);
        port.Poke(0x3000, bits);
        var f = Bind(FloatReg("F", "0x3000", 8, "LittleEndian"), port).GetFloat("F");

        Assert.Equal(-3.75, await f.GetAsync());
        Assert.Equal(double.MaxValue, await f.GetMaxAsync());

        await f.SetAsync(1e300);
        var back = port.Peek(0x3000, 8);
        if (!BitConverter.IsLittleEndian) Array.Reverse(back);
        Assert.Equal(1e300, BitConverter.Int64BitsToDouble(BitConverter.ToInt64(back, 0)));
    }

    [Fact]
    public void FloatReg_LengthNotFourOrEight_FailsAtBind()
    {
        var ex = Assert.Throws<GenApiException>(() => Bind(FloatReg("F", "0x3000", 2), new MemoryPort()));
        Assert.Equal("F", ex.NodeName);
    }

    // ---------------------------------------------------------------- Float

    [Fact]
    public async Task Float_Literal_IsHostSideVariable()
    {
        var port = new MemoryPort();
        var body = "<Float Name=\"F\"><Value>0.5</Value><Min>0</Min><Max>1</Max><Inc>0.25</Inc><Unit>x</Unit><Representation>Logarithmic</Representation><DisplayNotation>Scientific</DisplayNotation><DisplayPrecision>3</DisplayPrecision></Float>";
        var map = Bind(body, port);
        var f = map.GetFloat("F");

        Assert.Equal(0.5, await f.GetAsync());
        await f.SetAsync(0.75);
        Assert.Equal(0.75, await f.GetAsync());
        Assert.Equal(0.25, await f.GetIncAsync());
        Assert.Equal("x", f.Unit);
        Assert.Equal(Representation.Logarithmic, f.Representation);
        var node = (FloatNodeBase)map.GetNode("F")!;
        Assert.Equal(DisplayNotation.Scientific, node.DisplayNotation);
        Assert.Equal(3, node.DisplayPrecision);
        Assert.Equal(0, port.ReadCount + port.WriteCount);

        await Assert.ThrowsAsync<GenApiException>(() => f.SetAsync(1.5).AsTask());
        await Assert.ThrowsAsync<GenApiException>(() => f.SetAsync(double.NaN).AsTask());
        Assert.Equal(0.75, await f.GetAsync());
    }

    [Fact]
    public async Task Float_OverInteger_ConvertsBothWays()
    {
        var port = new MemoryPort();
        port.U32(0x10, 100);
        var body = "<Float Name=\"F\"><pValue>I</pValue></Float>" + Integer("I", "R", "<Min>0</Min><Max>200</Max>") + IntReg("R", "0x10");
        var f = Bind(body, port).GetFloat("F");

        Assert.Equal(100.0, await f.GetAsync());
        Assert.Equal(0.0, await f.GetMinAsync());
        Assert.Equal(200.0, await f.GetMaxAsync());
        Assert.Equal(1.0, await f.GetIncAsync());

        await f.SetAsync(12.6);
        Assert.Equal(13u, port.U32(0x10));                  // 반올림, 절삭이 아니다
        await Assert.ThrowsAsync<GenApiException>(() => f.SetAsync(201.0).AsTask());
    }

    [Fact]
    public async Task Float_OverFloatReg_UsesLiteralLimits()
    {
        var port = new MemoryPort();
        port.F32(0x3000, 30f);
        var body = "<Float Name=\"Rate\"><pValue>RateReg</pValue><Min>1.0</Min><Max>1000.0</Max><Unit>Hz</Unit></Float>" + FloatReg("RateReg", "0x3000", 4);
        var rate = Bind(body, port).GetFloat("Rate");

        Assert.Equal(30.0, await rate.GetAsync());
        Assert.Equal(1.0, await rate.GetMinAsync());
        Assert.Equal(1000.0, await rate.GetMaxAsync());
        Assert.Null(await rate.GetIncAsync());
        await rate.SetAsync(60.5);
        Assert.Equal(60.5f, port.F32(0x3000));
        await Assert.ThrowsAsync<GenApiException>(() => rate.SetAsync(0.5).AsTask());
    }

    [Fact]
    public async Task Float_PValueCopy_WritesCopies()
    {
        var port = new MemoryPort();
        var body = "<Float Name=\"F\"><pValue>A</pValue><pValueCopy>B</pValueCopy></Float>" + FloatReg("A", "0x3000", 4) + FloatReg("B", "0x3010", 8);
        await Bind(body, port).GetFloat("F").SetAsync(4.5);

        Assert.Equal(4.5f, port.F32(0x3000));
        Assert.Equal(4.5, port.F64(0x3010));
    }

    [Fact]
    public async Task Float_PIndex_WithoutMatchOrDefault_IsNotAvailable()
    {
        var body = "<Integer Name=\"Sel\"><Value>2</Value></Integer>"
            + "<Float Name=\"F\"><pIndex>Sel</pIndex><ValueIndexed Index=\"0\">0.5</ValueIndexed></Float>";
        var f = Bind(body, new MemoryPort()).GetFloat("F");

        Assert.Equal(AccessMode.NotAvailable, await f.GetAccessModeAsync());
        Assert.False(await f.IsAvailableAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => f.GetAsync().AsTask());
        Assert.Contains("index 2", ex.Message);
        Assert.Equal("F", ex.NodeName);
    }

    // ---------------------------------------------------------------- Converter

    private const string ExposureBody =
        "<Converter Name=\"ExposureTime\"><pVariable Name=\"TICKFREQ\">TickFreq</pVariable><FormulaTo>FROM * TICKFREQ / 1000000</FormulaTo><FormulaFrom>TO * 1000000.0 / TICKFREQ</FormulaFrom><pValue>Raw</pValue><Unit>us</Unit><Representation>Linear</Representation><DisplayNotation>Fixed</DisplayNotation><DisplayPrecision>1</DisplayPrecision><Slope>Increasing</Slope><IsLinear>Yes</IsLinear></Converter>"
        + "<Integer Name=\"TickFreq\"><Value>1000000000</Value></Integer>"
        + "<Integer Name=\"Raw\"><pValue>RawReg</pValue><Min>1000</Min><Max>2000000000</Max><Inc>1</Inc></Integer>";

    [Fact]
    public async Task Converter_RoundTripsThroughIntegerTarget()
    {
        var port = new MemoryPort();
        port.U32(0x14, 10_000_000);
        var map = Bind(ExposureBody + IntReg("RawReg", "0x14"), port);
        var exp = map.GetFloat("ExposureTime");

        Assert.Equal(10_000.0, await exp.GetAsync());
        await exp.SetAsync(2000.0);
        Assert.Equal(2_000_000u, port.U32(0x14));
        Assert.Equal(2000.0, await exp.GetAsync());
        Assert.Equal("us", exp.Unit);
        Assert.Equal(Representation.Linear, exp.Representation);
        Assert.Null(await exp.GetIncAsync());

        // Converter 자신의 한계값(대상 Min/Max 를 FormulaFrom 으로 넘긴 것)이 먼저 걸린다
        var ex = await Assert.ThrowsAsync<GenApiException>(() => exp.SetAsync(0.5).AsTask());
        Assert.Equal("ExposureTime", ex.NodeName);
        Assert.Contains("range", ex.Message);
        Assert.Equal(2_000_000u, port.U32(0x14));
    }

    [Fact]
    public async Task Converter_TargetRangeCheck_AppliesToConvertedValue()
    {
        // Converter 의 범위 안이지만 변환된 정수가 대상의 Inc 격자에서 벗어나는 값: 사슬 아래 Integer 의 검사가 변환된 값으로 이뤄진다
        var port = new MemoryPort();
        var body = "<Converter Name=\"C\"><FormulaTo>FROM * 10</FormulaTo><FormulaFrom>TO / 10.0</FormulaFrom><pValue>Raw</pValue></Converter>"
            + "<Integer Name=\"Raw\"><pValue>RawReg</pValue><Min>0</Min><Max>1022</Max><Inc>2</Inc></Integer>" + IntReg("RawReg", "0x14");
        var c = Bind(body, port).GetFloat("C");

        Assert.Equal(0.0, await c.GetMinAsync());
        Assert.Equal(102.2, await c.GetMaxAsync(), 10);
        var ex = await Assert.ThrowsAsync<GenApiException>(() => c.SetAsync(102.1).AsTask());   // 1021 → 격자(2) 밖
        Assert.Equal("Raw", ex.NodeName);
        Assert.Contains("increment", ex.Message);
        Assert.Equal(0, port.WriteCount);
        await c.SetAsync(102.0);
        Assert.Equal(1020u, port.U32(0x14));
    }

    [Fact]
    public async Task Converter_SlopeIncreasing_MapsTargetLimits()
    {
        var exp = Bind(ExposureBody + IntReg("RawReg", "0x14"), new MemoryPort()).GetFloat("ExposureTime");

        Assert.Equal(1.0, await exp.GetMinAsync());
        Assert.Equal(2_000_000.0, await exp.GetMaxAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => exp.SetAsync(3_000_000.0).AsTask());
        Assert.Equal("ExposureTime", ex.NodeName);
    }

    [Theory]
    [InlineData("Decreasing")]
    [InlineData("Automatic")]
    public async Task Converter_SlopeDecreasingOrAutomatic_OrdersLimits(string slope)
    {
        var body = $"<Converter Name=\"Period\"><FormulaTo>1000000.0 / FROM</FormulaTo><FormulaFrom>1000000.0 / TO</FormulaFrom><pValue>Hz</pValue><Slope>{slope}</Slope></Converter>"
            + "<Integer Name=\"Hz\"><pValue>HzReg</pValue><Min>1</Min><Max>1000</Max></Integer>" + IntReg("HzReg", "0x10");
        var port = new MemoryPort();
        port.U32(0x10, 50);
        var period = Bind(body, port).GetFloat("Period");

        Assert.Equal(20_000.0, await period.GetAsync());
        Assert.Equal(1_000.0, await period.GetMinAsync());          // From(max)
        Assert.Equal(1_000_000.0, await period.GetMaxAsync());      // From(min)
        await period.SetAsync(4000.0);
        Assert.Equal(250u, port.U32(0x10));
    }

    [Fact]
    public async Task Converter_SlopeVarying_DeclaresNoLimits()
    {
        // Varying 은 "이 수식은 단조롭지 않다" 는 선언이다 — 대상의 양 끝이 이 노드의 양 끝이라는 근거가 없으므로
        // 그 값으로 한계를 지어서는 안 된다. 값과 쓰기는 그대로 통해야 한다.
        var body = "<Converter Name=\"Period\"><FormulaTo>1000000.0 / FROM</FormulaTo><FormulaFrom>1000000.0 / TO</FormulaFrom><pValue>Hz</pValue><Slope>Varying</Slope></Converter>"
            + "<Integer Name=\"Hz\"><pValue>HzReg</pValue><Min>1</Min><Max>1000</Max></Integer>" + IntReg("HzReg", "0x10");
        var port = new MemoryPort();
        port.U32(0x10, 50);
        var period = Bind(body, port).GetFloat("Period");

        Assert.Equal(20_000.0, await period.GetAsync());
        Assert.Equal(-double.MaxValue, await period.GetMinAsync());
        Assert.Equal(double.MaxValue, await period.GetMaxAsync());
        await period.SetAsync(4000.0);
        Assert.Equal(250u, port.U32(0x10));
        // 범위를 지키는 것은 대상 노드 자신의 검사뿐이다 — 그것은 그대로 살아 있어야 한다.
        var ex = await Assert.ThrowsAsync<GenApiException>(() => period.SetAsync(0.5).AsTask());
        Assert.Equal("Hz", ex.NodeName);
    }

    [Fact]
    public async Task Converter_SlopeVarying_LookupTable_StaysWritable()
    {
        // 룩업 테이블형 IntConverter: 표에 없는 값은 조건 사슬 끝의 "해당 없음" 상수로 떨어진다.
        // 대상의 Min(0)·Max(3) 이 둘 다 그 상수로 가므로, 그 둘로 한계를 지으면 Min == Max 가 되어
        // 정작 표에 있는 값까지 전부 범위 밖으로 거절된다. 실기에서 픽셀 포맷을 바꿀 수 없던 모양 그대로다.
        const long None = 4294967295;
        var body = "<IntConverter Name=\"Fmt\">"
            + "<FormulaTo>(FROM = 17301505) ? 1 : ((FROM = 17825799) ? 2 : " + None + ")</FormulaTo>"
            + "<FormulaFrom>(TO = 1) ? 17301505 : ((TO = 2) ? 17825799 : " + None + ")</FormulaFrom>"
            + "<pValue>Raw</pValue><Slope>Varying</Slope></IntConverter>"
            + "<Integer Name=\"Raw\"><pValue>RawReg</pValue><Min>0</Min><Max>3</Max></Integer>" + IntReg("RawReg", "0x10");
        var port = new MemoryPort();
        port.U32(0x10, 1);
        var map = Bind(body, port);
        var fmt = map.GetInteger("Fmt");

        // 대상이 선언한 두 끝 — FormulaFrom 에 넣으면 둘 다 표에 없어 같은 상수로 접힌다. 그것으로 한계를 지으면 노드가 잠긴다.
        Assert.Equal(0, await map.GetInteger("Raw").GetMinAsync());
        Assert.Equal(3, await map.GetInteger("Raw").GetMaxAsync());
        Assert.Equal(long.MinValue, await fmt.GetMinAsync());
        Assert.Equal(long.MaxValue, await fmt.GetMaxAsync());

        Assert.Equal(17301505, await fmt.GetAsync());
        await fmt.SetAsync(17825799);
        Assert.Equal(2u, port.U32(0x10));
        Assert.Equal(17825799, await fmt.GetAsync());
    }

    [Fact]
    public async Task Converter_WithConstantAndExpression_Evaluates()
    {
        var body = "<Converter Name=\"C\"><Constant Name=\"K\">2.5</Constant><Expression Name=\"K2\">K * 2</Expression><FormulaTo>FROM / K2</FormulaTo><FormulaFrom>TO * K2</FormulaFrom><pValue>R</pValue><Slope>Increasing</Slope></Converter>"
            + IntReg("R", "0x10");
        var port = new MemoryPort();
        port.U32(0x10, 4);
        var c = Bind(body, port).GetFloat("C");

        Assert.Equal(20.0, await c.GetAsync());
        await c.SetAsync(30.0);
        Assert.Equal(6u, port.U32(0x10));
    }

    [Fact]
    public async Task Converter_RoundsHalfwayValuesAwayFromZero()
    {
        // 변환 결과가 정확히 .5 인 자리 — 짝수로 붙이는 기본 반올림을 쓰면 2.5 가 2 로, -2.5 가 -2 로 떨어진다.
        var port = new MemoryPort();
        var body = "<Converter Name=\"C\"><FormulaTo>FROM / 2.0</FormulaTo><FormulaFrom>TO * 2.0</FormulaFrom><pValue>R</pValue></Converter>"
            + IntReg("R", "0x10", "<Sign>Signed</Sign>");
        var c = Bind(body, port).GetFloat("C");

        await c.SetAsync(5.0);
        Assert.Equal(3, unchecked((int)port.U32(0x10)));      // 2.5 → 3
        await c.SetAsync(7.0);
        Assert.Equal(4, unchecked((int)port.U32(0x10)));      // 3.5 → 4
        await c.SetAsync(-5.0);
        Assert.Equal(-3, unchecked((int)port.U32(0x10)));     // -2.5 → -3
        await c.SetAsync(-7.0);
        Assert.Equal(-4, unchecked((int)port.U32(0x10)));     // -3.5 → -4
    }

    [Fact]
    public async Task Converter_TargetWithoutDeclaredLimits_StaysWritable()
    {
        // 대상이 Min/Max 를 선언하지 않으면 그 자리에 long 의 양끝이 온다 — 그대로 수식에 넣으면 곱셈 한 번에 넘쳐
        // 한계값 조회가 실패하고, 쓰기마다 한계를 묻는 탓에 노드 전체를 못 쓰게 된다.
        var port = new MemoryPort();
        var body = "<IntConverter Name=\"C\"><FormulaTo>FROM / 3</FormulaTo><FormulaFrom>TO * 3</FormulaFrom><pValue>Raw</pValue></IntConverter>"
            + "<Integer Name=\"Raw\"><Value>0</Value></Integer>";
        var c = Bind(body, port).GetInteger("C");

        Assert.Equal(long.MinValue, await c.GetMinAsync());
        Assert.Equal(long.MaxValue, await c.GetMaxAsync());
        await c.SetAsync(30);
        Assert.Equal(30, await c.GetAsync());
    }

    [Fact]
    public async Task Converter_AutomaticSlope_OneOpenEndOpensBoth()
    {
        // 기울기를 모르면 계산된 두 끝 중 어느 쪽이 Min 인지 알 수 없다 — 한쪽이 열려 있으면 양쪽을 연다.
        // 부호 없는 8바이트 레지스터는 Min 0 을 선언하지만 Max 가 "선언 안 됨" 센티널과 같은 값이라 그 Min 도 함께 사라진다.
        // 범위를 지키는 것은 대상 노드 자신의 검사뿐이라는 뜻이므로, 그것이 실제로 남아 있는지 함께 못박는다.
        var port = new MemoryPort();
        var body = "<IntConverter Name=\"C\"><FormulaTo>FROM</FormulaTo><FormulaFrom>TO</FormulaFrom><pValue>R</pValue></IntConverter>"
            + IntReg("R", "0x10", "<Sign>Unsigned</Sign>", length: 8);
        var map = Bind(body, port);
        var c = map.GetInteger("C");

        Assert.Equal(0, await map.GetInteger("R").GetMinAsync());
        Assert.Equal(long.MaxValue, await map.GetInteger("R").GetMaxAsync());
        Assert.Equal(long.MinValue, await c.GetMinAsync());
        Assert.Equal(long.MaxValue, await c.GetMaxAsync());

        var ex = await Assert.ThrowsAsync<GenApiException>(() => c.SetAsync(-1).AsTask());
        Assert.Equal("R", ex.NodeName);
        Assert.Contains("outside the range", ex.Message);
    }

    [Fact]
    public async Task Converter_OverBooleanTarget_HasLimitsAndWrites()
    {
        // Boolean 을 pValue 로 삼은 Converter — 한계값을 못 물으면 바인딩만 되고 쓰기가 통째로 막힌다.
        var port = new MemoryPort();
        var body = "<IntConverter Name=\"C\"><FormulaTo>FROM / 10</FormulaTo><FormulaFrom>TO * 10</FormulaFrom><pValue>B</pValue><Slope>Increasing</Slope></IntConverter>"
            + "<Boolean Name=\"B\"><pValue>R</pValue><OnValue>0xFF</OnValue><OffValue>0</OffValue></Boolean>" + IntReg("R", "0x10");
        var c = Bind(body, port).GetInteger("C");

        Assert.Equal(0, await c.GetMinAsync());
        Assert.Equal(10, await c.GetMaxAsync());
        await c.SetAsync(10);
        Assert.Equal(0xFFu, port.U32(0x10));
        Assert.Equal(10, await c.GetAsync());
        await c.SetAsync(0);
        Assert.Equal(0u, port.U32(0x10));
    }

    [Fact]
    public async Task Converter_OverEnumerationTarget_TakesLimitsFromEntryValues()
    {
        var port = new MemoryPort();
        var body = "<IntConverter Name=\"C\"><FormulaTo>FROM / 2</FormulaTo><FormulaFrom>TO * 2</FormulaFrom><pValue>E</pValue><Slope>Increasing</Slope></IntConverter>"
            + "<Enumeration Name=\"E\"><EnumEntry Name=\"Low\"><Value>2</Value></EnumEntry><EnumEntry Name=\"High\"><Value>8</Value></EnumEntry>"
            + "<EnumEntry Name=\"Mid\"><Value>4</Value></EnumEntry><pValue>R</pValue></Enumeration>" + IntReg("R", "0x10");
        var map = Bind(body, port);
        var c = map.GetInteger("C");

        Assert.Equal(4, await c.GetMinAsync());              // 가장 작은 항목 값 2 → From(2)
        Assert.Equal(16, await c.GetMaxAsync());             // 가장 큰 항목 값 8 → From(8)
        await c.SetAsync(8);
        Assert.Equal(4u, port.U32(0x10));
        Assert.Equal("Mid", await map.GetEnumeration("E").GetAsync());
        // 항목이 없는 값은 대상이 다시 거절한다(한계 안이어도).
        var ex = await Assert.ThrowsAsync<GenApiException>(() => c.SetAsync(12).AsTask());
        Assert.Equal("E", ex.NodeName);
    }

    [Fact]
    public async Task SwissKnife_LimitSuffixes_OnBooleanAndEnumeration()
    {
        var port = new MemoryPort();
        var body = "<IntSwissKnife Name=\"K\"><pVariable Name=\"B.Min\">B</pVariable><pVariable Name=\"B.Max\">B</pVariable><pVariable Name=\"B.Inc\">B</pVariable>"
            + "<pVariable Name=\"E.Min\">E</pVariable><pVariable Name=\"E.Max\">E</pVariable>"
            + "<Formula>B.Min + B.Max * 10 + B.Inc * 100 + E.Min * 1000 + E.Max * 10000</Formula></IntSwissKnife>"
            + "<Boolean Name=\"B\"><Value>false</Value></Boolean>"
            + "<Enumeration Name=\"E\"><EnumEntry Name=\"Low\"><Value>3</Value></EnumEntry><EnumEntry Name=\"High\"><Value>7</Value></EnumEntry><Value>3</Value></Enumeration>";
        var k = Bind(body, port).GetInteger("K");

        Assert.Equal(0 + 1 * 10 + 1 * 100 + 3 * 1000 + 7 * 10000, await k.GetAsync());
    }

    [Fact]
    public void Converter_WithoutPValue_FailsAtBind()
    {
        var body = "<Converter Name=\"C\"><FormulaTo>FROM</FormulaTo><FormulaFrom>TO</FormulaFrom></Converter>";
        var ex = Assert.Throws<GenApiException>(() => Bind(body, new MemoryPort()));
        Assert.Equal("C", ex.NodeName);
    }

    [Fact]
    public void Formula_SyntaxError_FailsAtBindWithNodeName()
    {
        var body = "<SwissKnife Name=\"K\"><Formula>1 +</Formula></SwissKnife>";
        var ex = Assert.Throws<GenApiException>(() => Bind(body, new MemoryPort()));
        Assert.Equal("K", ex.NodeName);
        Assert.Contains("Formula", ex.Message);
    }

    // ---------------------------------------------------------------- IntConverter

    [Fact]
    public async Task IntConverter_RoundTrips()
    {
        var port = new MemoryPort();
        port.U32(0x10, 100);
        var body = "<IntConverter Name=\"Bps\"><Constant Name=\"BITS\">8</Constant><FormulaTo>FROM / BITS</FormulaTo><FormulaFrom>TO * BITS</FormulaFrom><pValue>R</pValue><Slope>Increasing</Slope><Unit>Bps</Unit></IntConverter>"
            + Integer("R", "RReg", "<Min>0</Min><Max>1000</Max>") + IntReg("RReg", "0x10");
        var bps = Bind(body, port).GetInteger("Bps");

        Assert.Equal(800, await bps.GetAsync());
        await bps.SetAsync(1600);
        Assert.Equal(200u, port.U32(0x10));
        Assert.Equal(0, await bps.GetMinAsync());
        Assert.Equal(8000, await bps.GetMaxAsync());
        Assert.Equal(1, await bps.GetIncAsync());
        Assert.Equal("Bps", bps.Unit);
        await Assert.ThrowsAsync<GenApiException>(() => bps.SetAsync(8001).AsTask());
    }

    [Fact]
    public async Task IntConverter_RoundsFractionalDeviceValue()
    {
        var port = new MemoryPort();
        var body = "<IntConverter Name=\"C\"><FormulaTo>FROM / 3.0</FormulaTo><FormulaFrom>TO * 3</FormulaFrom><pValue>R</pValue></IntConverter>" + IntReg("R", "0x10");
        var c = Bind(body, port).GetInteger("C");

        await c.SetAsync(10);            // 10 / 3.0 = 3.33 → 3
        Assert.Equal(3u, port.U32(0x10));
        await c.SetAsync(11);            // 3.67 → 4
        Assert.Equal(4u, port.U32(0x10));
        Assert.Equal(12, await c.GetAsync());
    }

    // ---------------------------------------------------------------- SwissKnife

    [Fact]
    public async Task SwissKnife_EvaluatesVariablesConstantsAndExpressions()
    {
        var port = new MemoryPort();
        port.U32(0x10, 100);
        port.U32(0x14, 10);
        var body = "<SwissKnife Name=\"MaxRate\"><pVariable Name=\"W\">Width</pVariable><pVariable Name=\"H\">Height</pVariable><Constant Name=\"PIXCLK\">100000000.0</Constant><Expression Name=\"PIXELS\">W * H</Expression><Formula>PIXCLK / PIXELS</Formula><Unit>Hz</Unit></SwissKnife>"
            + Integer("Width", "WReg") + Integer("Height", "HReg") + IntReg("WReg", "0x10") + IntReg("HReg", "0x14");
        var map = Bind(body, port);
        var k = map.GetFloat("MaxRate");

        Assert.Equal(100_000.0, await k.GetAsync());
        Assert.Equal("Hz", k.Unit);
        Assert.Equal(AccessMode.ReadOnly, await k.GetAccessModeAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => k.SetAsync(1.0).AsTask());
        Assert.Contains("read-only", ex.Message);

        await map.GetInteger("Height").SetAsync(20);
        Assert.Equal(50_000.0, await k.GetAsync());
    }

    [Fact]
    public async Task SwissKnife_DivisionByZero_Throws()
    {
        var port = new MemoryPort();
        var body = "<SwissKnife Name=\"K\"><pVariable Name=\"D\">R</pVariable><Formula>10 / D</Formula></SwissKnife>" + IntReg("R", "0x10");
        var ex = await Assert.ThrowsAsync<GenApiException>(() => Bind(body, port).GetFloat("K").GetAsync().AsTask());
        Assert.Contains("zero", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwissKnife_VariableSuffixes_ReadLimits()
    {
        var port = new MemoryPort();
        port.U32(0x10, 640);
        var body = "<IntSwissKnife Name=\"K\"><pVariable Name=\"W\">Width</pVariable><pVariable Name=\"W.Min\">Width</pVariable><pVariable Name=\"W.Max\">Width</pVariable><pVariable Name=\"W.Inc\">Width</pVariable><Formula>W + W.Min * 1000 + W.Max * 1000000 + W.Inc * 1000000000</Formula></IntSwissKnife>"
            + Integer("Width", "WReg", "<Min>8</Min><Max>4096</Max><Inc>4</Inc>") + IntReg("WReg", "0x10");

        Assert.Equal(640L + 8_000L + 4_096_000_000L + 4_000_000_000L, await Bind(body, port).GetInteger("K").GetAsync());
    }

    [Fact]
    public async Task IntSwissKnife_RoundsFloatResult()
    {
        var body = "<IntSwissKnife Name=\"K\"><Constant Name=\"X\">2.6</Constant><Formula>X * 2</Formula></IntSwissKnife>";
        Assert.Equal(5, await Bind(body, new MemoryPort()).GetInteger("K").GetAsync());
    }

    [Fact]
    public void SwissKnife_ExpressionCycle_FailsAtBind()
    {
        var body = "<SwissKnife Name=\"K\"><Expression Name=\"A\">B + 1</Expression><Expression Name=\"B\">A + 1</Expression><Formula>A</Formula></SwissKnife>";
        var ex = Assert.Throws<GenApiException>(() => Bind(body, new MemoryPort()));
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public async Task SwissKnife_UnknownVariable_ThrowsAtRead()
    {
        // 파서는 수식의 변수 이름을 모르므로 바인딩은 통과하고 평가에서 노드 이름을 담아 던진다
        var body = "<SwissKnife Name=\"K\"><Formula>NOPE + 1</Formula></SwissKnife>";
        var ex = await Assert.ThrowsAsync<GenApiException>(() => Bind(body, new MemoryPort()).GetFloat("K").GetAsync().AsTask());
        Assert.Equal("K", ex.NodeName);
        Assert.Contains("NOPE", ex.Message);
    }
}
