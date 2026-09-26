using GevSharp.GenApi;
using static GevSharp.Tests.GenApi.Runtime.RuntimeFixture;

#pragma warning disable xUnit1051

namespace GevSharp.Tests.GenApi.Runtime;

/// <summary>
/// 값이 수식(SwissKnife/IntSwissKnife/Converter/IntConverter)의 pVariable 을 거쳐 레지스터에서 오는 노드의 무효화.
/// 손으로 쓴 XML 조각만 쓴다 — 시뮬레이터 XML 은 이 모양(래치 뒤 캐시되는 레지스터를 수식으로 읽는 값)을 갖지 않는다.
/// </summary>
public class FormulaInvalidationTests
{
    private const ulong HighAddr = 0x20;
    private const ulong LowAddr = 0x24;

    // 래치 명령이 두 레지스터(상위·하위 32비트)에 값을 붙잡아 두고, 값 노드는 IntSwissKnife 로 두 레지스터를 합쳐 읽는 모양.
    // 두 레지스터는 캐시되는(WriteThrough 기본) 레지스터이고 pInvalidator 도 없다 — 래치를 실행해도 저절로는 새로 읽히지 않는다.
    private static string LatchedTimestamp(string valueExtra = "")
        => "<Command Name=\"Latch\"><pValue>LatchReg</pValue><CommandValue>1</CommandValue></Command>" + IntReg("LatchReg", "0x10", access: "WO")
            + IntReg("TsHigh", "0x20", access: "RO") + IntReg("TsLow", "0x24", access: "RO")
            + "<IntSwissKnife Name=\"TsValueK\"><pVariable Name=\"HI\">TsHigh</pVariable><pVariable Name=\"LO\">TsLow</pVariable>"
            + "<Formula>(HI &lt;&lt; 32) | LO</Formula></IntSwissKnife>"
            + $"<Integer Name=\"TsValue\">{valueExtra}<pValue>TsValueK</pValue></Integer>";

    private static long Ts(uint high, uint low) => ((long)high << 32) | low;

    [Fact]
    public async Task Invalidate_OnValueNodeBehindIntSwissKnife_RereadsTheLatchedRegisters()
    {
        var port = new MemoryPort();
        port.U32(HighAddr, 1);
        port.U32(LowAddr, 2);
        var map = Bind(LatchedTimestamp(), port);
        var value = map.GetInteger("TsValue");

        Assert.Equal(Ts(1, 2), await value.GetAsync());

        port.U32(HighAddr, 3);                                  // 장치가 새 시각을 붙잡았다
        port.U32(LowAddr, 4);
        await map.GetCommand("Latch").ExecuteAsync();
        Assert.Equal(Ts(1, 2), await value.GetAsync());         // 대조군: 래치만으로는 캐시가 그대로다(pInvalidator 없음)
        Assert.Equal(1, port.ReadsAt(HighAddr));

        value.Invalidate();
        Assert.Equal(Ts(3, 4), await value.GetAsync());         // 무효화가 수식 변수 뒤의 레지스터까지 내려갔다
        Assert.Equal(2, port.ReadsAt(HighAddr));
        Assert.Equal(2, port.ReadsAt(LowAddr));
    }

    [Fact]
    public async Task Invalidate_OnTheIntSwissKnifeItself_RereadsItsVariables()
    {
        var port = new MemoryPort();
        port.U32(HighAddr, 1);
        port.U32(LowAddr, 2);
        var map = Bind(LatchedTimestamp(), port);
        var knife = map.GetInteger("TsValueK");

        Assert.Equal(Ts(1, 2), await knife.GetAsync());
        port.U32(LowAddr, 5);
        knife.Invalidate();
        Assert.Equal(Ts(1, 5), await knife.GetAsync());
        Assert.Equal(2, port.ReadsAt(LowAddr));
    }

    [Fact]
    public async Task PInvalidatorOnValueNodeBehindIntSwissKnife_RefreshesTheFormulaInputs()
    {
        // pInvalidator 가 레지스터가 아니라 값 노드에 붙은 모양 — 래치를 쓰면 값 노드가 낡았다고 선언된 것이므로
        // 그 값을 만드는 수식의 입력 레지스터까지 버려야 한다.
        var port = new MemoryPort();
        port.U32(HighAddr, 1);
        port.U32(LowAddr, 2);
        var map = Bind(LatchedTimestamp("<pInvalidator>Latch</pInvalidator>"), port);
        var value = map.GetInteger("TsValue");

        Assert.Equal(Ts(1, 2), await value.GetAsync());
        port.U32(HighAddr, 3);
        port.U32(LowAddr, 4);
        await map.GetCommand("Latch").ExecuteAsync();

        Assert.Equal(Ts(3, 4), await value.GetAsync());
        Assert.Equal(2, port.ReadsAt(HighAddr));
        Assert.Equal(2, port.ReadsAt(LowAddr));
    }

    [Fact]
    public async Task Invalidate_OnFloatBehindSwissKnife_RereadsTheRegister()
    {
        var port = new MemoryPort();
        port.U32(0x30, 46000);
        var body = IntReg("TempReg", "0x30", access: "RO")
            + "<SwissKnife Name=\"TempK\"><pVariable Name=\"T\">TempReg</pVariable><Formula>T / 1000</Formula></SwissKnife>"
            + "<Float Name=\"Temp\"><pValue>TempK</pValue></Float>";
        var map = Bind(body, port);
        var temp = map.GetFloat("Temp");

        Assert.Equal(46.0, await temp.GetAsync());
        port.U32(0x30, 47500);
        temp.Invalidate();
        Assert.Equal(47.5, await temp.GetAsync());
        Assert.Equal(2, port.ReadsAt(0x30));
    }

    [Fact]
    public async Task Invalidate_OnConverter_RereadsItsPVariableRegister()
    {
        // Converter 는 pValue 말고도 수식 변수로 레지스터를 읽는다(여기서는 오프셋) — 둘 다 값 사슬이다.
        var port = new MemoryPort();
        port.U32(0x40, 100);
        port.U32(0x44, 7);
        var body = "<Converter Name=\"G\"><pVariable Name=\"OFS\">OfsReg</pVariable><FormulaTo>FROM - OFS</FormulaTo><FormulaFrom>TO + OFS</FormulaFrom>"
            + "<pValue>RawReg</pValue><Slope>Increasing</Slope></Converter>"
            + IntReg("RawReg", "0x40") + IntReg("OfsReg", "0x44", access: "RO");
        var map = Bind(body, port);
        var g = map.GetFloat("G");

        Assert.Equal(107.0, await g.GetAsync());
        port.U32(0x40, 200);
        port.U32(0x44, 9);
        g.Invalidate();
        Assert.Equal(209.0, await g.GetAsync());
        Assert.Equal(2, port.ReadsAt(0x40));
        Assert.Equal(2, port.ReadsAt(0x44));
    }

    [Fact]
    public async Task Invalidate_OnIntConverter_RereadsItsPVariableRegister()
    {
        var port = new MemoryPort();
        port.U32(0x40, 100);
        port.U32(0x44, 7);
        var body = "<IntConverter Name=\"G\"><pVariable Name=\"OFS\">OfsReg</pVariable><FormulaTo>FROM - OFS</FormulaTo><FormulaFrom>TO + OFS</FormulaFrom>"
            + "<pValue>RawReg</pValue><Slope>Increasing</Slope></IntConverter>"
            + IntReg("RawReg", "0x40") + IntReg("OfsReg", "0x44", access: "RO");
        var map = Bind(body, port);
        var g = map.GetInteger("G");

        Assert.Equal(107, await g.GetAsync());
        port.U32(0x44, 9);
        g.Invalidate();
        Assert.Equal(109, await g.GetAsync());
        Assert.Equal(2, port.ReadsAt(0x44));
    }

    [Fact]
    public async Task Invalidate_OfOneFormulaInput_LeavesTheOtherInputCached()
    {
        // 수식에 의존하는 노드는 "낡은 입력 때문에" 낡은 것이다 — 그 입력만 버리면 되고 다른 입력은 그대로 믿는다.
        // 무효화를 수식 변수까지 내려 보내더라도 의존으로 닿은 노드에서까지 내려가면 무관한 레지스터를 다시 읽게 된다.
        var port = new MemoryPort();
        port.U32(HighAddr, 1);
        port.U32(LowAddr, 2);
        var map = Bind(LatchedTimestamp(), port);
        var value = map.GetInteger("TsValue");

        Assert.Equal(Ts(1, 2), await value.GetAsync());
        port.U32(HighAddr, 3);
        map.GetInteger("TsHigh").Invalidate();

        Assert.Equal(Ts(3, 2), await value.GetAsync());
        Assert.Equal(2, port.ReadsAt(HighAddr));
        Assert.Equal(1, port.ReadsAt(LowAddr));                 // 형제 입력은 캐시에서
    }
}
