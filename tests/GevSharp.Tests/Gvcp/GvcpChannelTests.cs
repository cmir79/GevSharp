using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GevSharp.Gvcp;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Gvcp;

/// <summary>루프백 응답기로 채널의 대조·재시도·PENDING_ACK·타임아웃·오류·정리 동작을 확인한다.</summary>
public class GvcpChannelTests
{
    private static GvcpChannel Open(GvcpTestResponder r, int timeoutMs = 300, int retries = 0, int maxPendingMs = 10000)
        => new(r.EndPoint, IPAddress.Loopback, new GvcpChannelOpt { TimeoutMs = timeoutMs, Retries = retries, MaxPendingAckWaitMs = maxPendingMs });

    /// <summary>
    /// 조건이 설 때까지 기다린다. 시간 제한은 "멈춘 시험을 끝낸다" 는 뜻뿐이라 넉넉히 둔다 — 조건이 서기까지의 시간을
    /// 재는 자리가 아니다. 응답을 일부러 몇 초 미뤄 두는 시험도 이 헬퍼로 기다린다.
    /// </summary>
    internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000, string? what = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"condition not met within {timeoutMs} ms{(what is null ? "" : ": " + what)}");
            await Task.Delay(5);
        }
    }

    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task ReadAndWriteRegRoundTripThroughLoopback()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r);
        Assert.Equal(IPAddress.Loopback, ch.LocalEndPoint.Address);
        Assert.Equal(r.EndPoint, ch.DeviceEndPoint);

        r.WriteU32(0x1000, 0xCAFEBABE);
        var ack = await ch.RequestAsync(GvcpCmd.ReadReg(0x1000));
        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
        Assert.False(ack.IsError);
        Assert.Equal(0xCAFEBABEu, ack.GetRegValue(0));

        var wack = await ch.RequestAsync(GvcpCmd.WriteReg(0x1004, 42));
        Assert.Equal(1, wack.WriteIndex);
        Assert.Equal(42u, r.ReadU32(0x1004));

        var data = Enumerable.Range(0, 16).Select(i => (byte)(i * 3)).ToArray();
        await ch.RequestAsync(GvcpCmd.WriteMem(0x2000, data));
        var mack = await ch.RequestAsync(GvcpCmd.ReadMem(0x2000, 16));
        Assert.Equal(0x2000u, mack.MemAddress);
        Assert.Equal(data, mack.MemData.ToArray());

        Assert.Equal(0, ch.StaleAckCount);
        Assert.Equal(0, ch.ForeignPacketCount);
        Assert.Equal(0, ch.MalformedPacketCount);
        Assert.Equal(0, ch.PendingAckCount);
    }

    [Fact]
    public async Task ReqIdsAreNonZeroDistinctAndEchoedByTheDevice()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r);
        var ids = new List<ushort>();
        for (var i = 0; i < 5; i++)
            ids.Add((await ch.RequestAsync(GvcpCmd.ReadReg(0))).ReqId);

        Assert.All(ids, id => Assert.NotEqual(0, id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(ids, r.Requests.Select(q => q.ReqId).ToList());
    }

    [Fact]
    public async Task ConcurrentRequestsAreSerializedAndNeverCrossTalk()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r, timeoutMs: 1000, retries: 1);
        const int n = 32;
        for (var i = 0; i < n; i++) r.WriteU32((uint)(0x1000 + i * 4), (uint)(0xA000 + i));

        var tasks = Enumerable.Range(0, n).Select(async i =>
        {
            var ack = await ch.RequestAsync(GvcpCmd.ReadReg((uint)(0x1000 + i * 4)));
            return ack.GetRegValue(0) == (uint)(0xA000 + i);
        }).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, ok => Assert.True(ok));
        Assert.Equal(n, r.CountOf(GvcpConst.ReadRegCmd));
        Assert.Equal(0, ch.StaleAckCount);
    }

    // ---------------------------------------------------------------- correlation

    [Fact]
    public async Task ReplyWithWrongReqIdIsDroppedAndCounted()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r);
        r.WriteU32(0x1000, 7);
        r.WrongReqIdNext(1);

        var ack = await ch.RequestAsync(GvcpCmd.ReadReg(0x1000));

        Assert.Equal(7u, ack.GetRegValue(0));
        Assert.Equal(1, ch.StaleAckCount);
        Assert.Equal(1, r.CountOf(GvcpConst.ReadRegCmd));
    }

    [Fact]
    public async Task ReplyWithWrongAckCommandIsDroppedThenRetrySucceeds()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r, timeoutMs: 100, retries: 1);
        r.WrongCommandNext(1);

        var ack = await ch.RequestAsync(GvcpCmd.ReadReg(0));

        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
        Assert.Equal(1, ch.StaleAckCount);
        Assert.Equal(2, r.CountOf(GvcpConst.ReadRegCmd));
    }

    [Fact]
    public async Task TruncatedReplyIsCountedMalformedThenRetrySucceeds()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r, timeoutMs: 100, retries: 1);
        r.TruncateReplyNext(1);

        var ack = await ch.RequestAsync(GvcpCmd.ReadReg(0));

        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
        Assert.Equal(1, ch.MalformedPacketCount);
        Assert.Equal(2, r.CountOf(GvcpConst.ReadRegCmd));
    }

    [Fact]
    public async Task LateReplyAfterTimeoutIsCountedStaleAndChannelStaysUsable()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r, timeoutMs: 40, retries: 0);
        // 응답을 기한(40 ms)보다 한참 뒤로 미룬다. 두 시각의 차이가 작으면 재는 것이 러너가 된다 —
        // 굶주린 스케줄러에서는 기한을 재는 타이머가 수백 ms 늦게 깨어, 200 ms 뒤의 응답이 기한보다 먼저 도착해 버렸다.
        r.ReplyDelayMs = 2000;

        await Assert.ThrowsAsync<GevTimeoutException>(() => ch.RequestAsync(GvcpCmd.ReadReg(0)));
        await WaitUntilAsync(() => ch.StaleAckCount == 1, what: "late reply counted");

        r.ReplyDelayMs = 0;
        var ack = await ch.RequestAsync(GvcpCmd.ReadReg(0));
        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
    }

    [Fact]
    public async Task PacketsFromAnotherEndpointAreCountedForeign()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r);
        using var stranger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        var bogus = new byte[12];
        await stranger.SendAsync(bogus, bogus.Length, ch.LocalEndPoint);
        await WaitUntilAsync(() => ch.ForeignPacketCount == 1, what: "foreign packet counted");

        Assert.Equal(0, ch.StaleAckCount);
        var ack = await ch.RequestAsync(GvcpCmd.ReadReg(0));
        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
    }

    // ---------------------------------------------------------------- retry / timeout

    [Fact]
    public async Task DroppedRequestIsResentWithTheSameReqId()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r, timeoutMs: 80, retries: 2);
        r.DropNext(1);

        var ack = await ch.RequestAsync(GvcpCmd.ReadReg(0));

        var reqs = r.Requests;
        Assert.Equal(2, reqs.Count);
        Assert.Equal(reqs[0].ReqId, reqs[1].ReqId);
        Assert.Equal(ack.ReqId, reqs[0].ReqId);
    }

    [Fact]
    public async Task SilentDeviceTimesOutAfterFirstSendPlusRetries()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r, timeoutMs: 40, retries: 2);
        r.IsSilent = true;

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<GevTimeoutException>(() => ch.RequestAsync(GvcpCmd.ReadReg(0)));
        sw.Stop();

        Assert.Contains("READREG", ex.Message);
        Assert.True(sw.ElapsedMilliseconds >= 100, $"gave up after only {sw.ElapsedMilliseconds} ms");
        Assert.Equal(3, r.CountOf(GvcpConst.ReadRegCmd));
    }

    [Fact]
    public async Task CancellationAbortsTheWaitAndReleasesTheChannel()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r, timeoutMs: 5000, retries: 0);
        r.IsSilent = true;
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ch.RequestAsync(GvcpCmd.ReadReg(0), cts.Token));

        r.IsSilent = false;
        var ack = await ch.RequestAsync(GvcpCmd.ReadReg(0));
        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
    }

    // ---------------------------------------------------------------- status / pending ack

    [Fact]
    public async Task ErrorStatusRaisesStatusExceptionWithoutRetry()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r, timeoutMs: 200, retries: 2);
        r.ErrorAddr = 0x2000;
        r.ErrorStatus = GvcpConst.StatusWriteProtect;

        var ex = await Assert.ThrowsAsync<GevStatusException>(() => ch.RequestAsync(GvcpCmd.WriteReg(0x2000, 1)));

        Assert.Equal("WRITEREG", ex.Operation);
        Assert.Equal(GvcpConst.StatusWriteProtect, ex.Status);
        Assert.Contains("WRITE_PROTECT", ex.Message);
        Assert.Equal(1, r.CountOf(GvcpConst.WriteRegCmd));

        var rex = await Assert.ThrowsAsync<GevStatusException>(() => ch.RequestAsync(GvcpCmd.ReadReg(0x2000)));
        Assert.Equal("READREG", rex.Operation);
    }

    [Fact]
    public async Task ErrorAckCarriesTheFailedWriteIndex()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r);
        const int n = 67;
        var writes = new KeyValuePair<uint, uint>[n];
        for (var i = 0; i < n; i++) writes[i] = new KeyValuePair<uint, uint>((uint)(0x6000 + i * 4), (uint)(i + 1));
        r.ErrorAddr = writes[39].Key;
        r.ErrorStatus = GvcpConst.StatusWriteProtect;

        var ex = await Assert.ThrowsAsync<GevStatusException>(() => ch.RequestAsync(GvcpCmd.WriteRegs(writes)));

        Assert.Equal(GvcpConst.StatusWriteProtect, ex.Status);
        Assert.Equal(39, Assert.IsType<int>(ex.Data[GvcpChannel.FailedIndexKey]));
        for (var i = 0; i < 39; i++) Assert.Equal((uint)(i + 1), r.ReadU32(writes[i].Key));
        for (var i = 39; i < n; i++) Assert.Equal(0u, r.ReadU32(writes[i].Key));

        // index 를 싣지 않는 오류 응답(READREG)에는 키가 없다.
        var rex = await Assert.ThrowsAsync<GevStatusException>(() => ch.RequestAsync(GvcpCmd.ReadReg(writes[39].Key)));
        Assert.False(rex.Data.Contains(GvcpChannel.FailedIndexKey));
    }

    [Fact]
    public async Task PendingAckExtendsTheWaitBeyondTheTimeout()
    {
        using var r = new GvcpTestResponder();
        // 타임아웃 250 ms — 연장이 없으면 여기서 포기한다. 그러면서도 PENDING_ACK 자체가 이 창 안에 닿을 만큼은 넉넉하다:
        // 굶주린 스케줄러에서는 응답기 스레드와 채널 수신 스레드가 깨는 것만으로도 수십 ms 가 든다(창이 60 ms 였을 때 실제로 놓쳤다).
        using var ch = Open(r, timeoutMs: 250, retries: 0);
        r.PendingAckMs = 1500;
        r.PendingAckDelayMs = 600;

        var sw = Stopwatch.StartNew();
        var ack = await ch.RequestAsync(GvcpCmd.ReadReg(0));
        sw.Stop();

        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
        // 아래 경계가 지키는 것은 "타임아웃(250 ms)을 넘겨서까지 기다렸다" = 연장이 있었다 이다. 부하는 시간을 늘릴 뿐이라 흔들리지 않고,
        // 연장이 사라지면 응답은 250 ms 안에 오지 못해 GevTimeoutException 으로 위에서 먼저 깨진다.
        Assert.True(sw.ElapsedMilliseconds >= 400, $"answered after only {sw.ElapsedMilliseconds} ms; the channel timeout is 250 ms");
        Assert.Equal(1, ch.PendingAckCount);
        Assert.Equal(1, r.CountOf(GvcpConst.ReadRegCmd));
        Assert.Equal(0, ch.StaleAckCount);
    }

    [Fact]
    public async Task PendingAckWaitIsCappedByMaxPendingAckWaitMs()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r, timeoutMs: 40, retries: 0, maxPendingMs: 60);
        // 장치는 20 s 를 예고하고 3 s 뒤에 진짜 ACK 를 보낸다 — 상한(40 + 60 ms)이 예고를 이겨야 한다.
        // 진짜 ACK 를 상한보다 한참 뒤로 미룬다: 두 시각이 가까우면 재는 것이 러너가 된다 —
        // 굶주린 스케줄러에서는 기한을 재는 타이머가 늦게 깨어, 600 ms 뒤의 ACK 가 100 ms 상한보다 먼저 받아들여졌다.
        r.PendingAckMs = 20_000;
        r.PendingAckDelayMs = 3000;

        await Assert.ThrowsAsync<GevTimeoutException>(() => ch.RequestAsync(GvcpCmd.ReadReg(0)));

        // 시간은 재지 않는다. 상한을 잊는 회귀는 어느 쪽으로 가든 시계 없이 걸리기 때문이다 —
        // 예고된 20 s 를 기다리든 600 ms 뒤의 진짜 ACK 를 받아들이든 결과는 "성공" 이라 위의 ThrowsAsync 가 먼저 깨진다.
        // (진짜 ACK 가 반드시 오므로 20 s 를 실제로 기다리는 일 자체가 없다 — 시간 상한을 두어도 발동할 수 없었다.)
        Assert.Equal(1, ch.PendingAckCount);
    }

    // ---------------------------------------------------------------- fire-and-forget

    [Fact]
    public async Task PacketResendIsSentWithoutAckInBothIdModes()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r);
        IGvcpResendPort port = ch;

        port.SendPacketResend(0x1234, 3, 7, extendedIds: false, streamChannel: 0);
        ch.SendPacketResend(0x1_0000_0001, 10, 12, extendedIds: true, streamChannel: 2);
        await WaitUntilAsync(() => r.CountOf(GvcpConst.PacketResendCmd) == 2, what: "resend commands received");

        var reqs = r.Requests.Where(q => q.Command == GvcpConst.PacketResendCmd).ToList();
        Assert.Equal(0x00, reqs[0].Flags);
        Assert.Equal(12, reqs[0].Payload.Length);
        GvcpPacket.ReadPacketResend(reqs[0].Payload, false, out var ch0, out var b0, out var f0, out var l0);
        Assert.Equal((0, 0x1234ul, 3u, 7u), (ch0, b0, f0, l0));

        Assert.Equal(GvcpConst.FlagExtendedIds, reqs[1].Flags);
        Assert.Equal(20, reqs[1].Payload.Length);
        GvcpPacket.ReadPacketResend(reqs[1].Payload, true, out var ch1, out var b1, out var f1, out var l1);
        Assert.Equal((2, 0x1_0000_0001ul, 10u, 12u), (ch1, b1, f1, l1));
        Assert.All(reqs, q => Assert.NotEqual(0, q.ReqId));

        var ack = await ch.RequestAsync(GvcpCmd.ReadReg(0));
        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
        Assert.Equal(0, ch.StaleAckCount);
    }

    [Fact]
    public async Task PacketResendDoesNotAllocateOnTheHotPath()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r);
        // JIT·소켓 첫 호출의 할당을 먼저 치운다.
        for (var i = 0; i < 50; i++) ch.SendPacketResend((ulong)i, 1, 2, extendedIds: (i & 1) == 0);
        await WaitUntilAsync(() => r.CountOf(GvcpConst.PacketResendCmd) >= 50, what: "warm-up resends received");

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) ch.SendPacketResend((ulong)(100 + i), 1, 2, extendedIds: (i & 1) == 0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        var ack = await ch.RequestAsync(GvcpCmd.ReadReg(0));
        Assert.Equal(GvcpConst.ReadRegAck, ack.Command);
    }

    [Fact]
    public async Task TheBufferedSendPathDoesNotAllocateEither()
    {
        // net8 은 Span·SocketAddress 오버로드로 보내지만 다른 대상은 버퍼 경로로 보낸다.
        // 시험은 한 대상에서만 도니 그 경로를 직접 불러 확인한다 — 소켓의 byte[] 오버로드에 EndPoint 를 그대로 넘기면
        // 호출마다 주소가 새로 직렬화되어, 손실이 잦을 때 재전송 한 장마다 쓰레기가 쌓인다.
        using var r = new GvcpTestResponder();
        using var ch = Open(r);
        var packet = GvcpCmd.PacketResend(1, 1, 2, extendedIds: false).ToArray(0x0202);
        for (var i = 0; i < 50; i++) ch.SendNoAckBuffered(packet);
        await WaitUntilAsync(() => r.CountOf(GvcpConst.PacketResendCmd) >= 50, what: "warm-up resends received");

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) ch.SendNoAckBuffered(packet);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task SendNoAckSendsRawPacketAndValidatesLength()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r);
        var packet = GvcpCmd.PacketResend(5, 1, 2, extendedIds: false).ToArray(0x0101);

        ch.SendNoAck(packet);
        await WaitUntilAsync(() => r.CountOf(GvcpConst.PacketResendCmd) == 1);
        Assert.Equal(0x0101, r.Requests[0].ReqId);

        Assert.Throws<GevException>(() => ch.SendNoAck(new byte[7]));
        await Assert.ThrowsAsync<ArgumentException>(() => ch.RequestAsync(GvcpCmd.PacketResend(1, 1, 2, false)));
    }

    // ---------------------------------------------------------------- lifecycle

    [Fact]
    public async Task DisposeWhileWaitingFailsTheRequestAndRejectsFurtherUse()
    {
        using var r = new GvcpTestResponder();
        var ch = Open(r, timeoutMs: 5000, retries: 0);
        r.IsSilent = true;
        var pending = ch.RequestAsync(GvcpCmd.ReadReg(0));
        await Task.Delay(50);

        ch.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        Assert.True(ch.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ch.RequestAsync(GvcpCmd.ReadReg(0)));
        Assert.Throws<ObjectDisposedException>(() => ch.SendPacketResend(1, 1, 1, false));
        ch.Dispose();
    }

    [Fact]
    public void TheChannelKeepsItsOwnCopyOfTheOptions()
    {
        // 세션은 하트비트를 알고 난 뒤 채널의 PENDING_ACK 상한을 다시 정한다. 그 쓰기가 호출자의 객체로 새어 나가면
        // 같은 객체로 만든 다음 채널이 앞 채널의 값을 물려받는다.
        using var r = new GvcpTestResponder();
        var shared = new GvcpChannelOpt { TimeoutMs = 120, Retries = 0, MaxPendingAckWaitMs = 1234 };
        using var first = new GvcpChannel(r.EndPoint, IPAddress.Loopback, shared);
        first.SetMaxPendingAckWaitMs(7);

        Assert.Equal(7, first.Opt.MaxPendingAckWaitMs);
        Assert.Equal(1234, shared.MaxPendingAckWaitMs);
        using var second = new GvcpChannel(r.EndPoint, IPAddress.Loopback, shared);
        Assert.Equal(1234, second.Opt.MaxPendingAckWaitMs);
        Assert.Equal((120, 0), (second.Opt.TimeoutMs, second.Opt.Retries));
    }

    [Fact]
    public void ConstructorValidatesEndpointAndOptions()
    {
        var ep = new IPEndPoint(IPAddress.Loopback, 3956);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GvcpChannel(ep, null, new GvcpChannelOpt { TimeoutMs = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GvcpChannel(ep, null, new GvcpChannelOpt { Retries = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GvcpChannel(ep, null, new GvcpChannelOpt { MaxPendingAckWaitMs = -1 }));
        Assert.Throws<GevException>(() => new GvcpChannel(new IPEndPoint(IPAddress.IPv6Loopback, 3956)));
        Assert.Throws<ArgumentNullException>(() => new GvcpChannel(null!));
    }

    [Fact]
    public async Task ReadFromDeviceFillsDeviceInfoFieldByField()
    {
        using var r = new GvcpTestResponder();
        using var ch = Open(r);

        var info = await GevDeviceInfo.ReadFromDeviceAsync(ch, IPAddress.Loopback);

        Assert.Equal("Responder", info.Model);
        Assert.Equal("SN0001", info.SerialNumber);
        Assert.Equal(IPAddress.Loopback, info.Address);
        Assert.Equal(IPAddress.Loopback, info.InterfaceAddress);
        // 블록 전체를 한 번에 READMEM 하지 않는다 — 레지스터는 READREG, 문자열은 그 주소에서 READMEM (요청 목록은 GevDeviceInfoReadTests).
        Assert.DoesNotContain(r.Requests, q => q.Command == GvcpConst.ReadMemCmd && q.Addresses[0] == 0);
        Assert.Equal(9, r.CountOf(GvcpConst.ReadRegCmd));
        Assert.Equal(6, r.CountOf(GvcpConst.ReadMemCmd));
    }
}
