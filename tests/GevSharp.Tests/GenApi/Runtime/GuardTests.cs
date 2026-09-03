using GevSharp.GenApi;
using static GevSharp.Tests.GenApi.Runtime.RuntimeFixture;

#pragma warning disable xUnit1051

namespace GevSharp.Tests.GenApi.Runtime;

/// <summary>pIsImplemented/pIsAvailable/pIsLocked 와 AccessMode/ImposedAccessMode 의 합성, 위반 시 사유가 담긴 예외.</summary>
public class GuardTests
{
    [Fact]
    public async Task NoGuards_MeansImplementedAvailableUnlocked()
    {
        var map = Bind(Integer("W", "R") + IntReg("R", "0x10"), new MemoryPort());
        var w = map.GetInteger("W");

        Assert.True(await w.IsImplementedAsync());
        Assert.True(await w.IsAvailableAsync());
        Assert.False(await w.IsLockedAsync());
        Assert.Equal(AccessMode.ReadWrite, await w.GetAccessModeAsync());
    }

    [Fact]
    public async Task PIsImplemented_False_BlocksEverything()
    {
        var port = new MemoryPort();
        var body = Integer("W", "R", "<pIsImplemented>Impl</pIsImplemented>") + IntReg("R", "0x10") + "<Integer Name=\"Impl\"><Value>0</Value></Integer>";
        var map = Bind(body, port);
        var w = map.GetInteger("W");

        Assert.False(await w.IsImplementedAsync());
        Assert.Equal(AccessMode.NotImplemented, await w.GetAccessModeAsync());
        var read = await Assert.ThrowsAsync<GenApiException>(() => w.GetAsync().AsTask());
        Assert.Contains("not implemented", read.Message);
        Assert.Contains("'W'", read.Message);
        Assert.Equal("W", read.NodeName);
        var write = await Assert.ThrowsAsync<GenApiException>(() => w.SetAsync(1).AsTask());
        Assert.Contains("not implemented", write.Message);
        Assert.Equal(0, port.ReadCount + port.WriteCount);

        await map.GetInteger("Impl").SetAsync(1);
        Assert.Equal(AccessMode.ReadWrite, await w.GetAccessModeAsync());
    }

    [Fact]
    public async Task PIsAvailable_ViaBoolean_BlocksAccess()
    {
        var body = Integer("W", "R", "<pIsAvailable>Avail</pIsAvailable>") + IntReg("R", "0x10") + "<Boolean Name=\"Avail\"><Value>false</Value></Boolean>";
        var map = Bind(body, new MemoryPort());
        var w = map.GetInteger("W");

        Assert.True(await w.IsImplementedAsync());
        Assert.False(await w.IsAvailableAsync());
        Assert.Equal(AccessMode.NotAvailable, await w.GetAccessModeAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => w.GetAsync().AsTask());
        Assert.Contains("not available", ex.Message);

        await map.GetBoolean("Avail").SetAsync(true);
        Assert.True(await w.IsAvailableAsync());
    }

    [Fact]
    public async Task PIsLocked_ViaSwissKnife_RemovesWriteOnly()
    {
        var port = new MemoryPort();
        var body = Integer("Width", "WidthReg", "<pIsLocked>Active</pIsLocked>") + IntReg("WidthReg", "0x10")
            + "<IntSwissKnife Name=\"Active\"><pVariable Name=\"S\">StatusReg</pVariable><Formula>S = 1</Formula></IntSwissKnife>"
            + IntReg("StatusReg", "0x20", "<Cachable>NoCache</Cachable>", "RO");
        var w = Bind(body, port).GetInteger("Width");

        Assert.False(await w.IsLockedAsync());
        Assert.Equal(AccessMode.ReadWrite, await w.GetAccessModeAsync());
        await w.SetAsync(4);

        port.U32(0x20, 1);
        Assert.True(await w.IsLockedAsync());
        Assert.True(await w.IsAvailableAsync());
        Assert.Equal(AccessMode.ReadOnly, await w.GetAccessModeAsync());
        Assert.Equal(4, await w.GetAsync());                // 읽기는 된다
        var ex = await Assert.ThrowsAsync<GenApiException>(() => w.SetAsync(8).AsTask());
        Assert.Contains("locked", ex.Message);
        Assert.Equal("Width", ex.NodeName);
        Assert.Equal(4u, port.U32(0x10));

        port.U32(0x20, 0);
        Assert.Equal(AccessMode.ReadWrite, await w.GetAccessModeAsync());
        await w.SetAsync(8);
    }

    [Fact]
    public async Task PIsLocked_OnWriteOnlyNode_BecomesNotAvailableWithLockedReason()
    {
        var body = Integer("Cmd", "R", "<pIsLocked>L</pIsLocked>") + IntReg("R", "0x10", access: "WO") + "<Integer Name=\"L\"><Value>1</Value></Integer>";
        var c = Bind(body, new MemoryPort()).GetInteger("Cmd");

        Assert.Equal(AccessMode.NotAvailable, await c.GetAccessModeAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => c.SetAsync(1).AsTask());
        Assert.Contains("locked", ex.Message);
    }

    [Fact]
    public async Task RegisterAccessMode_ReadOnly_RejectsWrite()
    {
        var port = new MemoryPort();
        var r = Bind(IntReg("R", "0x10", access: "RO"), port).GetInteger("R");

        Assert.Equal(AccessMode.ReadOnly, await r.GetAccessModeAsync());
        await r.GetAsync();
        var ex = await Assert.ThrowsAsync<GenApiException>(() => r.SetAsync(1).AsTask());
        Assert.Contains("read-only", ex.Message);
        Assert.Equal("R", ex.NodeName);
        Assert.Equal(0, port.WriteCount);
    }

    [Fact]
    public async Task RegisterAccessMode_WriteOnly_RejectsRead()
    {
        var port = new MemoryPort();
        var body = Integer("I", "R") + IntReg("R", "0x10", access: "WO");
        var map = Bind(body, port);
        var i = map.GetInteger("I");

        Assert.Equal(AccessMode.WriteOnly, await i.GetAccessModeAsync());
        await i.SetAsync(3);
        Assert.Equal(3u, port.U32(0x10));
        var ex = await Assert.ThrowsAsync<GenApiException>(() => i.GetAsync().AsTask());
        Assert.Contains("write-only", ex.Message);
        Assert.Equal("I", ex.NodeName);
        await Assert.ThrowsAsync<GenApiException>(() => map.GetInteger("R").GetAsync().AsTask());
        Assert.Equal(0, port.ReadCount);
    }

    [Fact]
    public async Task ImposedAccessMode_NarrowsTheRegisterMode()
    {
        var port = new MemoryPort();
        var body = Integer("I", "R", "<ImposedAccessMode>RO</ImposedAccessMode>") + IntReg("R", "0x10")
            + "<StringReg Name=\"S\"><ImposedAccessMode>WO</ImposedAccessMode><Address>0x100</Address><Length>8</Length><AccessMode>RW</AccessMode><pPort>Device</pPort></StringReg>";
        var map = Bind(body, port);

        Assert.Equal(AccessMode.ReadOnly, await map.GetInteger("I").GetAccessModeAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => map.GetInteger("I").SetAsync(1).AsTask());
        Assert.Contains("read-only", ex.Message);
        Assert.Equal(AccessMode.ReadWrite, await map.GetInteger("R").GetAccessModeAsync());   // 레지스터 자체는 그대로

        Assert.Equal(AccessMode.WriteOnly, await map.GetString("S").GetAccessModeAsync());
        await Assert.ThrowsAsync<GenApiException>(() => map.GetString("S").GetAsync().AsTask());
    }

    [Fact]
    public async Task Guards_PropagateThroughPValueTarget()
    {
        var body = Integer("Outer", "Inner") + Integer("Inner", "R", "<pIsAvailable>Avail</pIsAvailable>") + IntReg("R", "0x10", access: "RO")
            + "<Integer Name=\"Avail\"><Value>0</Value></Integer>";
        var map = Bind(body, new MemoryPort());
        var outer = map.GetInteger("Outer");

        Assert.False(await outer.IsAvailableAsync());
        Assert.Equal(AccessMode.NotAvailable, await outer.GetAccessModeAsync());
        await map.GetInteger("Avail").SetAsync(1);
        Assert.Equal(AccessMode.ReadOnly, await outer.GetAccessModeAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => outer.SetAsync(1).AsTask());
        Assert.Contains("read-only", ex.Message);
        Assert.Equal("Outer", ex.NodeName);
    }

    [Fact]
    public async Task IsAvailable_FollowsTheComposedMode_NotJustPIsAvailable()
    {
        // 구현되지 않은 노드는 가용하지도 않다 — 술어를 따로 세면 모드는 NotImplemented 인데 가용은 참이라고 답한다.
        var body = Integer("W", "R", "<pIsImplemented>Impl</pIsImplemented>") + IntReg("R", "0x10")
            + "<Integer Name=\"Impl\"><Value>0</Value></Integer>";
        var map = Bind(body, new MemoryPort());
        var w = map.GetInteger("W");

        Assert.Equal(AccessMode.NotImplemented, await w.GetAccessModeAsync());
        Assert.False(await w.IsAvailableAsync());

        await map.GetInteger("Impl").SetAsync(1);
        Assert.True(await w.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_FollowsTheComposedMode_ThroughAPValueTarget()
    {
        // 대상이 구현되지 않았을 때도 마찬가지 — 사슬 위쪽 노드의 가용 여부가 모드와 어긋나면 안 된다.
        var body = Integer("Outer", "Inner") + Integer("Inner", "R", "<pIsImplemented>Impl</pIsImplemented>") + IntReg("R", "0x10")
            + "<Integer Name=\"Impl\"><Value>0</Value></Integer>";
        var map = Bind(body, new MemoryPort());
        var outer = map.GetInteger("Outer");

        Assert.Equal(AccessMode.NotImplemented, await outer.GetAccessModeAsync());
        Assert.False(await outer.IsImplementedAsync());
        Assert.False(await outer.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_IsFalseForALockedWriteOnlyFeature()
    {
        // 장치 문서가 흔히 쓰는 모양 — 쓰기 전용(ImposedAccessMode=WO) 명령 피처를 전송 계층 잠금이 열어 준다.
        // 잠금이 쓰기를 떼면 남는 권한이 없어 모드는 NotAvailable 이고, 가용 여부도 그 답을 그대로 따라야 한다.
        var port = new MemoryPort();
        var body = Integer("Start", "R", "<ImposedAccessMode>WO</ImposedAccessMode><pIsLocked>L</pIsLocked>") + IntReg("R", "0x10")
            + "<Integer Name=\"L\"><Value>1</Value><Min>0</Min><Max>1</Max></Integer>";
        var map = Bind(body, port);
        var start = map.GetInteger("Start");

        Assert.True(await start.IsLockedAsync());
        Assert.Equal(AccessMode.NotAvailable, await start.GetAccessModeAsync());
        Assert.False(await start.IsAvailableAsync());

        await map.GetInteger("L").SetAsync(0);
        Assert.False(await start.IsLockedAsync());
        Assert.Equal(AccessMode.WriteOnly, await start.GetAccessModeAsync());
        Assert.True(await start.IsAvailableAsync());
        await start.SetAsync(7);
        Assert.Equal(7u, port.U32(0x10));
    }

    [Fact]
    public async Task IsLocked_PropagatesThroughPValueTarget()
    {
        var body = Integer("Outer", "Inner") + Integer("Inner", "R", "<pIsLocked>L</pIsLocked>") + IntReg("R", "0x10")
            + "<Integer Name=\"L\"><Value>1</Value><Min>0</Min><Max>1</Max></Integer>";
        var map = Bind(body, new MemoryPort());
        var outer = map.GetInteger("Outer");

        Assert.True(await outer.IsLockedAsync());
        Assert.True(await outer.IsAvailableAsync());                       // 잠긴 것은 읽기까지 막지 않는다
        Assert.Equal(AccessMode.ReadOnly, await outer.GetAccessModeAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => outer.SetAsync(1).AsTask());
        Assert.Contains("read-only", ex.Message);

        await map.GetInteger("L").SetAsync(0);
        Assert.False(await outer.IsLockedAsync());
        Assert.Equal(AccessMode.ReadWrite, await outer.GetAccessModeAsync());
        await outer.SetAsync(1);
    }

    [Fact]
    public async Task GuardNode_IsReadThroughInternalPathEvenWhenItselfIsGuarded()
    {
        // 술어 노드에 또 술어가 걸려 있어도 술어 평가는 값만 읽는다(접근 검사로 되돌아오지 않는다)
        var body = Integer("W", "R", "<pIsAvailable>A</pIsAvailable>") + IntReg("R", "0x10")
            + "<Integer Name=\"A\"><pIsAvailable>B</pIsAvailable><Value>1</Value></Integer><Integer Name=\"B\"><Value>0</Value></Integer>";
        var map = Bind(body, new MemoryPort());

        Assert.True(await map.GetInteger("W").IsAvailableAsync());
        Assert.False(await map.GetInteger("A").IsAvailableAsync());
    }

    [Fact]
    public async Task Command_NotAvailable_RefusesExecute()
    {
        var port = new MemoryPort();
        var body = "<Command Name=\"Trig\"><pIsAvailable>Avail</pIsAvailable><pValue>R</pValue><CommandValue>1</CommandValue></Command>"
            + IntReg("R", "0x10") + "<Boolean Name=\"Avail\"><Value>false</Value></Boolean>";
        var map = Bind(body, port);

        var ex = await Assert.ThrowsAsync<GenApiException>(() => map.GetCommand("Trig").ExecuteAsync().AsTask());
        Assert.Contains("not available", ex.Message);
        Assert.Equal(0, port.WriteCount);
    }

    [Fact]
    public async Task Enumeration_LockedByLiteralInteger()
    {
        var port = new MemoryPort();
        var body = "<Enumeration Name=\"E\"><pIsLocked>TLParamsLocked</pIsLocked><EnumEntry Name=\"Off\"><Value>0</Value></EnumEntry><EnumEntry Name=\"On\"><Value>1</Value></EnumEntry><pValue>R</pValue></Enumeration>"
            + IntReg("R", "0x10") + "<Integer Name=\"TLParamsLocked\"><Value>0</Value><Min>0</Min><Max>1</Max></Integer>";
        var map = Bind(body, port);
        var e = map.GetEnumeration("E");

        await e.SetAsync("On");
        await map.GetInteger("TLParamsLocked").SetAsync(1);
        var ex = await Assert.ThrowsAsync<GenApiException>(() => e.SetAsync("Off").AsTask());
        Assert.Contains("locked", ex.Message);
        Assert.Equal("On", await e.GetAsync());
    }
}
