using GevSharp.Gvsp;

#pragma warning disable xUnit1051

namespace GevSharp.Tests.Gvsp;

/// <summary>
/// 수신 대기 방식이 데이터그램을 잃지 않는지 — 부하 아래에서만 드러나는 성질이라 환경변수 <c>GEVSHARP_STRESS=1</c> 일 때만 돈다.
/// <para>
/// 수신 스레드는 프레임을 조립하는 동안 짧은 간격(max(1, min(InitialPacketTimeoutMs, PacketTimeoutMs)) ms)으로 깨어나 구멍을 본다.
/// 그 기다림을 소켓 수신 시한(SO_RCVTIMEO)으로 만들면, 윈도우에서는 시한이 만료되는 순간 막 도착한 데이터그램이 사라질 수 있다 —
/// 시한 만료 뒤 소켓 상태는 정해지지 않는다. 패킷 사이 간격이 그 시한보다 긴 송신(느린 장치, 리더가 먼저 오는 긴 노출,
/// 대역을 나눠 쓰는 여러 카메라)에서 그 경계를 자주 밟고, CPU 가 바쁘면 더 자주 밟는다. 재전송이 켜져 있으면 요청 한 번으로
/// 가려지므로 여기서는 끄고 보낸 수와 받은 수를 그대로 맞춘다.
/// </para>
/// <para>
/// 이 루프백 시험은 옛 수신 대기(수신 시한)에서도 2,800 프레임 동안 유실을 재현하지 못했다 — 근거는 실기 측정이다
/// (docs/evaluation.md 「Receive wait on Windows」: 패킷 간격 2.4 ms·재전송 끔·부하에서 옛 대기 불완전 9/46·8/39, 지금 0/39·0/39).
/// 여기 남기는 것은 같은 조건을 다시 걸어 볼 수 있는 자리다.
/// </para>
/// </summary>
public class ReceiveWaitLossTests
{
    private const uint Mono8 = 0x01080001;

    [Fact]
    public async Task SlowSenderUnderCpuLoad_LosesNoDatagram()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GEVSHARP_STRESS") == "1", "GEVSHARP_STRESS is not set to 1; the load test is skipped.");

        var opt = StreamRig.DefaultOpt();
        opt.ResendEnabled = false;          // 유실이 재전송으로 가려지지 않게
        opt.InitialPacketTimeoutMs = 2;     // 조립 중 수신 대기가 2 ms 간격으로 깨어난다
        opt.PacketTimeoutMs = 200;          // 5 ms 간격의 패킷을 침묵으로 오판해 프레임을 닫지 않게
        opt.BufferCount = 16;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        var received = 0;
        using var consumerStop = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var f = await rig.Stream.ReceiveAsync(consumerStop.Token);
                    Interlocked.Increment(ref received);
                }
            }
            catch (OperationCanceledException) { }
            catch (GevStreamClosedException) { }
        });

        var stopBurn = false;
        var burners = new Thread[Environment.ProcessorCount];
        for (var i = 0; i < burners.Length; i++)
        {
            burners[i] = new Thread(() => { while (!Volatile.Read(ref stopBurn)) Thread.SpinWait(2000); }) { IsBackground = true };
            burners[i].Start();
        }

        const int Frames = 400;
        try
        {
            for (var i = 0; i < Frames; i++)
            {
                var frame = rig.Sender.BuildFrame((ulong)(i % 65000) + 1, 64, 40, Mono8, seed: (byte)i);   // 2560 바이트 = 페이로드 2 패킷
                for (uint id = 0; id <= frame.TrailerId; id++)
                {
                    rig.Sender.SendPacket(frame, id, GvspConst.StatusSuccess);
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            Volatile.Write(ref stopBurn, true);
            foreach (var t in burners) t.Join();
        }

        await rig.WaitUntilAsync(() => rig.Stream.Stats.PacketsReceived >= rig.Sender.PacketsSent, 3000).ContinueWith(_ => { });
        await Task.Delay(300);
        var s = rig.Stream.Stats.Snapshot();
        consumerStop.Cancel();
        await consumer;

        Assert.True(s.PacketsReceived == rig.Sender.PacketsSent && s.FramesIncomplete == 0,
            $"sent {rig.Sender.PacketsSent} datagrams, received {s.PacketsReceived}; frames completed {s.FramesCompleted}, incomplete {s.FramesIncomplete}, delivered {Volatile.Read(ref received)} of {Frames}");
    }
}
