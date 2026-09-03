using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GevSharp.Gvcp;
using GevSharp.Gvsp;

namespace GevSharp.Sim;

/// <summary>
/// GVSP 송신기. AcquisitionStart 로 시작한 스레드가 프레임마다 레지스터(SCP/SCDA/SCPS/SCPD/SCCFG, Width/Height/PixelFormat,
/// AcquisitionFrameRate, TriggerControl)를 다시 읽어 리더·페이로드·트레일러를 SCDA:SCP 로 보낸다.
/// 최근 프레임은 리센드 이력에 남기고, PACKETRESEND 는 같은 바이트를 status 0x0100 으로 다시 보낸다.
/// </summary>
public sealed partial class SimDevice
{
    /// <summary>한 프레임의 전송 기록 — 리센드에 필요한 모든 것.</summary>
    private sealed class SimFrameRecord
    {
        public ulong BlockId;
        public byte[] Data = Array.Empty<byte>();
        public int Width, Height, OffsetX, OffsetY;
        public uint PixelFormat;
        public ulong Timestamp;
        public int DataBytesPerPacket;
        public int PacketCount;
        public bool IsExtended;
    }

    private readonly object _acqGate = new();
    /// <summary>송신 스레드의 대기를 깨우는 잠금 — 정지 요청과 소프트웨어 트리거가 여기에 신호를 보낸다.</summary>
    private readonly object _senderWake = new();
    private readonly List<SimFrameRecord> _history = new();
    private Thread? _acqThread;
    private volatile bool _acqStop;
    /// <summary>
    /// 1 = 송신 루프가 돌고 있다. 루프를 벗어나면(정지·SingleFrame/MultiFrame 완료) finally 첫머리에서 0 으로 내린다.
    /// Thread.IsAlive 는 마무리 중인 스레드도 살아 있다고 답하므로 "아직 보내는 중" 과 "끝나고 정리 중" 을 이 값으로 가른다.
    /// </summary>
    private int _acqRunning;
    private int _softwareTriggerPending;
    private ulong _blockId;
    /// <summary>정지 요청이 세워진 시각(ns). 0 이면 지금 실행에서는 아직 없었다.</summary>
    private long _acqStopAtNs;
    /// <summary>마지막으로 송신 스레드를 깨운 시각(ns).</summary>
    private long _senderPulseAtNs;
    private int _senderWakeMissed;

    /// <summary>
    /// 정지 요청이 이미 서 있는데도 송신 스레드가 신호가 아니라 대기 시간 만료로 스스로 깬 횟수.
    /// 그런 일이 한 번이라도 있으면 AcquisitionStop 을 처리하는 응답기가 송신기의 잠 주기만큼 붙들렸다는 뜻이다.
    /// 시간이 아니라 사건을 세므로 호스트가 굶어도 값이 부풀지 않는다 — 부하 아래에서도 그대로 단언할 수 있는 잣대다.
    /// </summary>
    public int SenderWakeMissedCount => Volatile.Read(ref _senderWakeMissed);

    /// <summary>
    /// 송신 스레드가 끝나기를 기다리는 상한. 넘기면 상태를 거짓으로 꾸미지 않고 오류로 남긴다.
    /// 한 GVCP 명령(AcquisitionStart/Stop)이 장치 안에서 스스로 기다릴 수 있는 최대 시간이기도 하다 — 호스트의 응답 대기 상한은 이보다 커야 한다.
    /// </summary>
    public const int SenderJoinTimeoutMs = 3000;

    /// <summary>신호 없이 잠드는 최대 시간(ms). 신호로 깨지 않는 변화(TriggerMode 전환 등)를 알아채는 주기다.</summary>
    private const int SenderPollMs = 20;

    /// <summary>이 시간(ms) 아래로 남으면 잠들지 않고 짧게 바쁜 대기로 마친다 — OS 타이머 눈금보다 짧아 잠들면 오히려 크게 넘긴다.</summary>
    private const int SenderSpinBelowMs = 2;

    /// <summary>바쁜 대기 한 번의 길이. 짧게 끊어 정지 요청과 목표 시각을 자주 다시 본다.</summary>
    private const int SenderSpinIterations = 50;

    private void StartAcquisition()
    {
        Thread? previous;
        lock (_acqGate) previous = _acqThread;
        if (previous is { IsAlive: true } && previous != Thread.CurrentThread)
        {
            if (Volatile.Read(ref _acqRunning) == 1 && !_acqStop)
            {
                // 이미 획득 중 — 실제 장치처럼 무시하되 흔적은 남긴다
                SetError("AcquisitionStart ignored: acquisition is already running");
                return;
            }
            // 정지 중이거나 스스로 끝나 마무리 중 — 스레드가 빠져나갈 때까지 기다렸다가 새로 시작한다(정지 직후 시작이 유실되지 않게)
            if (!previous.Join(SenderJoinTimeoutMs))
            {
                SetError($"AcquisitionStart refused: the previous GVSP sender did not stop within {SenderJoinTimeoutMs} ms");
                return;
            }
        }

        lock (_acqGate)
        {
            _acqStop = false;
            Volatile.Write(ref _acqStopAtNs, 0);
            Interlocked.Exchange(ref _softwareTriggerPending, 0);   // 시작 전이나 이전 실행에서 무장된 트리거는 버린다
            Volatile.Write(ref _acqRunning, 1);
            Registers.WriteU32(SimFeatureAddr.AcquisitionStatus, 1);
            // 과부하 러너에서 프레임 중간에 선점되면 수신 측은 "장치가 보내다 말았다" 로 읽는다 — 수신기와 같은 우선순위로 둔다.
            _acqThread = new Thread(SenderLoop) { IsBackground = true, Name = "GevSharp.Sim.Gvsp", Priority = ThreadPriority.AboveNormal };
            _acqThread.Start();
        }
    }

    private void StopAcquisition(bool join)
    {
        Thread? thread;
        lock (_acqGate)
        {
            // 깃발을 세우고 곧바로 깨운다 — 송신 스레드가 잠에서 스스로 깨기를 기다리면 그 시간만큼 GVCP 응답기가 이 명령에 붙들린다.
            // 둘을 같은 잠금 안에서 하는 이유: 사이가 벌어지면 그 틈에 대기가 끝난 송신 스레드에게
            // "정지 요청은 섰는데 아무도 깨우지 않았다" 로 보인다. 굶은 호스트에서는 그 틈이 초 단위로 벌어진다.
            lock (_senderWake)
            {
                _acqStop = true;
                Volatile.Write(ref _acqStopAtNs, NowNs);
                Volatile.Write(ref _senderPulseAtNs, NowNs);
                Monitor.PulseAll(_senderWake);
            }
            thread = _acqThread;
        }
        if (join && thread is not null && thread != Thread.CurrentThread && !thread.Join(SenderJoinTimeoutMs))
        {
            // 스레드가 아직 살아 있다 — AcquisitionStatus 를 0 으로 꾸미지 않는다. 스레드가 끝나면 스스로 0 으로 내린다.
            SetError($"GVSP sender did not stop within {SenderJoinTimeoutMs} ms");
            return;
        }
        Registers.WriteU32(SimFeatureAddr.AcquisitionStatus, 0);
    }

    private void SenderLoop()
    {
        long next = _clock.ElapsedTicks;
        int framesThisRun = 0;
        try
        {
            while (!_acqStop && !_isStopping)
            {
                bool triggerOn = (Registers.ReadU32(SimFeatureAddr.TriggerControl) & SimFeatureAddr.TriggerModeMask) != 0;
                if (triggerOn)
                {
                    if (Interlocked.Exchange(ref _softwareTriggerPending, 0) == 0)
                    {
                        SleepSender(SenderPollMs, wakeOnTrigger: true);
                        continue;
                    }
                }
                else
                {
                    double fps = Registers.ReadF32(SimFeatureAddr.AcquisitionFrameRate);
                    if (!(fps > 0)) fps = 1;   // NaN·0·음수 → 1 Hz
                    long period = (long)(Stopwatch.Frequency / fps);
                    WaitUntil(next);
                    if (_acqStop || _isStopping) break;
                    next += period;
                    long now = _clock.ElapsedTicks;
                    if (next < now - period) next = now;   // 많이 늦었으면 따라잡지 않고 재동기화
                }

                uint scp = Registers.ReadU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset)) & 0xFFFF;
                uint scda = Registers.ReadU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScdaOffset));
                if (scp != 0 && scda != 0)
                {
                    var dst = new IPEndPoint(FromU32(scda), (int)scp);
                    ulong blockId = NextBlockId();
                    if (SendFrame(blockId, dst))
                    {
                        // FramesSent 와 FrameCounter 는 SendFrame 이 트레일러 직전에 올린다(관측 순서를 지키려고).
                        FrameSent?.Invoke(blockId);
                        framesThisRun++;
                    }
                }

                // SingleFrame/MultiFrame 은 실제로 나간 프레임만 센다 — 채널이 닫혀 있거나 전송이 실패한 주기는 횟수를 소진하지 않는다
                uint mode = Registers.ReadU32(SimFeatureAddr.AcquisitionMode);
                if (mode == SimFeatureAddr.AcquisitionModeSingleFrame && framesThisRun >= 1) break;
                if (mode == SimFeatureAddr.AcquisitionModeMultiFrame
                    && framesThisRun >= Math.Max(1, Registers.ReadU32(SimFeatureAddr.AcquisitionFrameCount))) break;
            }
        }
        catch (ObjectDisposedException)
        {
            // 정지 중 소켓이 닫혔다
        }
        catch (Exception ex)
        {
            SetError("GVSP sender failure: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _acqRunning, 0);
            Registers.WriteU32(SimFeatureAddr.AcquisitionStatus, 0);
            lock (_acqGate)
            {
                if (_acqThread == Thread.CurrentThread) _acqThread = null;
            }
        }
    }

    private ulong NextBlockId()
    {
        bool extended = (Registers.ReadU32(GvbsAddr.StreamChannel(0, GvbsAddr.SccfgOffset)) & SimStreamBits.SccfgExtendedIds) != 0;
        ulong next = Volatile.Read(ref _blockId) + 1;
        if (!extended && next > 0xFFFF) next = 1;   // 0 은 예약 — 65535 다음은 1
        Volatile.Write(ref _blockId, next);
        return next;
    }

    /// <summary>마지막으로 보낸 블록 ID. 아직 보낸 프레임이 없으면 0.</summary>
    public ulong LastBlockId => Volatile.Read(ref _blockId);

    /// <summary>
    /// 다음 프레임이 lastBlockId + 1 로 시작하도록 카운터를 심는다 — 65535→1 랩어라운드처럼 수만 프레임을 보내야 닿는 경계를
    /// 테스트가 바로 확인하기 위한 것이다. 획득 중에 부르면 다음 프레임부터 반영된다.
    /// </summary>
    public void SeedBlockId(ulong lastBlockId) => Volatile.Write(ref _blockId, lastBlockId);

    /// <summary>
    /// 목표 시각(Stopwatch 틱)까지 기다린다. 넉넉히 남았으면 잠들고(정지 요청이 오면 즉시 깬다) 마지막 짧은 구간만 바쁜 대기로 맞춘다.
    /// </summary>
    private void WaitUntil(long targetTicks)
    {
        while (!_acqStop && !_isStopping)
        {
            long remaining = targetTicks - _clock.ElapsedTicks;
            if (remaining <= 0) return;
            long remainingMs = remaining * 1000 / Stopwatch.Frequency;
            if (remainingMs > SenderSpinBelowMs) SleepSender((int)Math.Min(remainingMs - 1, SenderPollMs), wakeOnTrigger: false);
            else Thread.SpinWait(SenderSpinIterations);
        }
    }

    /// <summary>
    /// 송신 스레드를 최대 <paramref name="ms"/> 밀리초 재운다. 정지 요청이 오면 즉시 깨고, <paramref name="wakeOnTrigger"/> 면
    /// 소프트웨어 트리거가 무장돼도 깬다. 조건 검사와 대기를 같은 잠금 아래에서 하므로 신호가 대기 직전에 와도 놓치지 않는다.
    /// </summary>
    private void SleepSender(int ms, bool wakeOnTrigger)
    {
        if (ms <= 0) return;
        lock (_senderWake)
        {
            if (_acqStop || _isStopping) return;
            if (wakeOnTrigger && Volatile.Read(ref _softwareTriggerPending) != 0) return;
            long deadlineNs = NowNs + ms * 1_000_000L;
            if (Monitor.Wait(_senderWake, ms)) return;

            // 신호가 아니라 시간이 다 되어 스스로 깼다. 대기 중에 정지 요청이 섰다면 신호로 깨웠어야 하는 자리다.
            // 정지 깃발과 신호는 둘 다 이 잠금 안에서 쓰이고 여기서도 이 잠금을 쥔 채 읽으므로 함께 보이거나 함께 안 보인다 —
            // "정지 요청은 보이는데 그에 대한 신호가 없다" 는 곧 아무도 깨워 주지 않았다는 뜻이고, 그동안
            // AcquisitionStop 을 처리하는 응답기는 이 잠이 끝나기를 기다리며 붙들려 있었다.
            // 시간이 아니라 사건이라 호스트가 굶어도 판정이 흔들리지 않는다.
            long stopAtNs = Volatile.Read(ref _acqStopAtNs);
            if (stopAtNs == 0 || stopAtNs >= deadlineNs) return;      // 기한 뒤에 선 요청은 셀 일이 아니다
            if (Volatile.Read(ref _senderPulseAtNs) >= stopAtNs) return;
            Interlocked.Increment(ref _senderWakeMissed);
        }
    }

    /// <summary>잠들어 있는 송신 스레드를 깨운다. 깃발(<c>_acqStop</c>·트리거)을 세운 **뒤에** 부른다.</summary>
    private void SignalSender()
    {
        lock (_senderWake)
        {
            Volatile.Write(ref _senderPulseAtNs, NowNs);
            Monitor.PulseAll(_senderWake);
        }
    }

    private bool SendFrame(ulong blockId, IPEndPoint dst)
    {
        bool extended = (Registers.ReadU32(GvbsAddr.StreamChannel(0, GvbsAddr.SccfgOffset)) & SimStreamBits.SccfgExtendedIds) != 0;
        int scps = (int)(Registers.ReadU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset)) & GvbsAddr.ScpsSizeMask);
        int headerSize = extended ? GvspConst.ExtendedHeaderSize : GvspConst.HeaderSize;
        int dataBytes = scps - GvspConst.IpUdpOverhead - headerSize;
        if (dataBytes <= 0)
        {
            SetError($"SCPS {scps} leaves no room for payload data");
            return false;
        }

        int width = (int)Registers.ReadU32(SimFeatureAddr.Width);
        int height = (int)Registers.ReadU32(SimFeatureAddr.Height);
        uint pixelFormat = Registers.ReadU32(SimFeatureAddr.PixelFormat);
        int bpp = (int)((pixelFormat >> 16) & 0xFF);
        if (width <= 0 || height <= 0 || bpp <= 0)
        {
            SetError($"cannot build a frame from Width={width} Height={height} PixelFormat=0x{pixelFormat:X8}");
            return false;
        }

        var data = BuildImage(width, height, pixelFormat, bpp, blockId, Registers.ReadU32(SimFeatureAddr.TestPattern));

        var rec = new SimFrameRecord
        {
            BlockId = blockId,
            Data = data,
            Width = width,
            Height = height,
            OffsetX = (int)Registers.ReadU32(SimFeatureAddr.OffsetX),
            OffsetY = (int)Registers.ReadU32(SimFeatureAddr.OffsetY),
            PixelFormat = pixelFormat,
            Timestamp = TimestampTicks,
            DataBytesPerPacket = dataBytes,
            PacketCount = (data.Length + dataBytes - 1) / dataBytes,
            IsExtended = extended,
        };
        lock (_history)
        {
            _history.Add(rec);
            int keep = Math.Max(1, Opt.ResendHistoryFrames);
            while (_history.Count > keep) _history.RemoveAt(0);
        }

        uint scpd = Registers.ReadU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScpdOffset));
        var buf = new byte[headerSize + Math.Max(dataBytes, GvspConst.ImageLeaderDataSize)];
        var drop = Opt.DropPacket;

        int n = BuildLeader(buf, rec, GvspConst.StatusSuccess);
        if (!SendGvsp(buf, n, dst)) return false;
        Interlocked.Increment(ref _packetsSent);
        InterPacketDelay(scpd);

        for (uint p = 1; p <= (uint)rec.PacketCount; p++)
        {
            if (drop is not null && drop(blockId, p))
            {
                Interlocked.Increment(ref _packetsDropped);
                continue;
            }
            n = BuildPayload(buf, rec, p, GvspConst.StatusSuccess);
            if (!SendGvsp(buf, n, dst)) return false;
            Interlocked.Increment(ref _packetsSent);
            InterPacketDelay(scpd);
        }

        // 카운터는 트레일러가 나가기 **전에** 올린다. 트레일러가 회선에 오르는 순간 수신 측은 프레임을 완성으로 보는데,
        // 그 뒤에 세면 "프레임은 받았는데 FramesSent 는 아직 옛 값" 인 창이 생겨 테스트가 실제 순서를 못 믿게 된다.
        int sent = Interlocked.Increment(ref _framesSent);
        Registers.WriteU32(SimFeatureAddr.FrameCounter, (uint)sent);

        n = BuildTrailer(buf, rec, GvspConst.StatusSuccess);
        if (!SendGvsp(buf, n, dst)) return false;
        Interlocked.Increment(ref _packetsSent);
        return true;
    }

    /// <summary>
    /// 결정적 픽셀 내용. 줄 안의 바이트 인덱스 b, 줄 y, 프레임 id 로만 정해져 테스트가 바이트 단위로 검증할 수 있다.
    /// DiagonalRamp: (b + y + frameId) &amp; 0xFF — Mono8 이면 곧 (x + y + frameId) &amp; 0xFF. FrameCounter: 전부 frameId &amp; 0xFF. Off: 전부 0.
    /// </summary>
    internal static void FillPattern(byte[] data, int lineBytes, int height, ulong frameId, uint pattern)
    {
        switch (pattern)
        {
            case SimFeatureAddr.TestPatternOff:
                Array.Clear(data, 0, data.Length);
                break;
            case SimFeatureAddr.TestPatternFrameCounter:
                Array.Fill(data, (byte)frameId);
                break;
            default:
                for (int y = 0; y < height; y++)
                {
                    int row = y * lineBytes;
                    for (int b = 0; b < lineBytes; b++) data[row + b] = (byte)(b + y + (int)frameId);
                }
                break;
        }
    }

    /// <summary>
    /// 픽셀 <paramref name="width"/> 개를 이어 실었을 때의 바이트 수(한 줄이든 이미지 전체든 같은 규칙이다).
    /// 기본은 ceil(width × bpp / 8) 이지만, 묶음 단위로 실리는 포맷은 마지막 묶음을 통째로 보낸다:
    /// GVSP Packed(2픽셀 3바이트) 계열 0x010C0004·0x010C0006·0x010C0026..0x010C002D 는 (width + 1) / 2 × 3,
    /// 4:1:1 YUV(4픽셀 6바이트) 0x020C001E·0x020C003C 는 (width + 3) / 4 × 6.
    /// 장치 쪽 표라 라이브러리를 부르지 않는다 — 같은 규칙을 양쪽이 따로 구현해야 한쪽의 실수가 서로 상쇄되지 않는다.
    /// </summary>
    private static int SimLineBytes(int width, uint pixelFormat, int bpp)
    {
        switch (pixelFormat)
        {
            case 0x010C0004:    // Mono10Packed
            case 0x010C0006:    // Mono12Packed
            case 0x010C0026:    // BayerGR10Packed
            case 0x010C0027:    // BayerRG10Packed
            case 0x010C0028:    // BayerGB10Packed
            case 0x010C0029:    // BayerBG10Packed
            case 0x010C002A:    // BayerGR12Packed
            case 0x010C002B:    // BayerRG12Packed
            case 0x010C002C:    // BayerGB12Packed
            case 0x010C002D:    // BayerBG12Packed
                return (width + 1) / 2 * 3;
            case 0x020C001E:    // YUV411_8_UYYVYY
            case 0x020C003C:    // YCbCr411_8
                return (width + 3) / 4 * 6;
            default:
                return (width * bpp + 7) / 8;
        }
    }

    /// <summary>
    /// 이미지 전체 바이트 수. 이 장치는 줄 패딩을 넣지 않으므로 묶음 단위 포맷의 데이터는 줄에서 끊기지 않고 이어 붙는다 —
    /// 줄마다 마지막 묶음을 채우지 않고 전체 픽셀 수로 한 번만 올린다. 홀수 폭 packed 에서 줄 단위 계산과 갈린다
    /// (2591 × 64 12비트 packed = 248,736 바이트, 줄 단위로 세면 248,832).
    /// </summary>
    private static int SimImageBytes(int width, int height, uint pixelFormat, int bpp)
        => SimLineBytes(width * height, pixelFormat, bpp);

    /// <summary>
    /// 패턴이 채워진 이미지 한 장. 줄이 바이트 경계에서 끝나면 줄 단위로 채우고, 아니면 줄이라는 것이 없으므로 한 덩어리로 채운다.
    /// </summary>
    private static byte[] BuildImage(int width, int height, uint pixelFormat, int bpp, ulong frameId, uint pattern)
    {
        int lineBytes = SimLineBytes(width, pixelFormat, bpp);
        int imageBytes = SimImageBytes(width, height, pixelFormat, bpp);
        var data = new byte[imageBytes];
        if (imageBytes == lineBytes * height) FillPattern(data, lineBytes, height, frameId, pattern);
        else FillPattern(data, imageBytes, 1, frameId, pattern);
        return data;
    }

    /// <summary>테스트가 기대 바이트를 만들 때 쓰는 공개 패턴 함수(장치가 실제로 보내는 것과 동일).</summary>
    public static byte[] BuildPatternFrame(int width, int height, uint pixelFormat, ulong frameId, uint pattern = SimFeatureAddr.TestPatternDiagonalRamp)
        => BuildImage(width, height, pixelFormat, (int)((pixelFormat >> 16) & 0xFF), frameId, pattern);

    // ---- 패킷 조립 (라이브러리 파서와 독립) ----

    private static int WriteGvspHeader(byte[] buf, ushort status, ulong blockId, byte contentType, uint packetId, bool extended)
    {
        SimWire.WriteU16(buf, 0, status);
        if (!extended)
        {
            SimWire.WriteU16(buf, 2, (ushort)blockId);
            SimWire.WriteU32(buf, 4, ((uint)contentType << GvspConst.ContentTypeShift) | (packetId & GvspConst.PacketIdMask));
            return GvspConst.HeaderSize;
        }
        SimWire.WriteU16(buf, 2, 0);   // flags
        SimWire.WriteU32(buf, 4, GvspConst.ExtendedIdMask | ((uint)contentType << GvspConst.ContentTypeShift));
        SimWire.WriteU64(buf, 8, blockId);
        SimWire.WriteU32(buf, 16, packetId);
        return GvspConst.ExtendedHeaderSize;
    }

    private static int BuildLeader(byte[] buf, SimFrameRecord rec, ushort status)
    {
        int o = WriteGvspHeader(buf, status, rec.BlockId, GvspConst.ContentLeader, 0, rec.IsExtended);
        SimWire.WriteU16(buf, o + 0, 0);                              // flags
        SimWire.WriteU16(buf, o + 2, GvspConst.PayloadImage);
        SimWire.WriteU64(buf, o + 4, rec.Timestamp);
        SimWire.WriteU32(buf, o + 12, rec.PixelFormat);
        SimWire.WriteU32(buf, o + 16, (uint)rec.Width);
        SimWire.WriteU32(buf, o + 20, (uint)rec.Height);
        SimWire.WriteU32(buf, o + 24, (uint)rec.OffsetX);
        SimWire.WriteU32(buf, o + 28, (uint)rec.OffsetY);
        SimWire.WriteU16(buf, o + 32, 0);                             // padding x
        SimWire.WriteU16(buf, o + 34, 0);                             // padding y
        return o + GvspConst.ImageLeaderDataSize;
    }

    private static int BuildPayload(byte[] buf, SimFrameRecord rec, uint packetId, ushort status)
    {
        int o = WriteGvspHeader(buf, status, rec.BlockId, GvspConst.ContentPayload, packetId, rec.IsExtended);
        int offset = (int)(packetId - 1) * rec.DataBytesPerPacket;
        int len = Math.Min(rec.DataBytesPerPacket, rec.Data.Length - offset);
        Buffer.BlockCopy(rec.Data, offset, buf, o, len);
        return o + len;
    }

    private static int BuildTrailer(byte[] buf, SimFrameRecord rec, ushort status)
    {
        int o = WriteGvspHeader(buf, status, rec.BlockId, GvspConst.ContentTrailer, (uint)rec.PacketCount + 1, rec.IsExtended);
        SimWire.WriteU16(buf, o + 0, 0);
        SimWire.WriteU16(buf, o + 2, GvspConst.PayloadImage);
        SimWire.WriteU32(buf, o + 4, (uint)rec.Height);
        return o + GvspConst.TrailerDataSize;
    }

    private bool SendGvsp(byte[] buf, int length, IPEndPoint dst)
    {
        var sock = _gvspSocket;
        if (sock is null) return false;
        try
        {
            sock.SendTo(buf, 0, length, SocketFlags.None, dst);
            return true;
        }
        catch (SocketException ex)
        {
            Interlocked.Increment(ref _sendErrors);
            SetError($"GVSP send to {dst} failed: {ex.SocketErrorCode}");
            return false;
        }
        catch (ObjectDisposedException)
        {
            // Stop() 이 소켓을 닫는 사이 GVCP 스레드(리센드·파이어테스트)가 보내려 한 경우 — 오류가 아니라 정지다
            return false;
        }
    }

    /// <summary>
    /// SCPD(타임스탬프 틱 = ns) 만큼 패킷 사이를 띄운다. 넉넉히 남았으면 잠들고 마지막 짧은 구간은 바쁜 대기로 맞춘다.
    /// 여기서 <see cref="Thread.Yield"/> 를 쓰면 안 된다 — 코어가 모자란 상황에서 양보하면 다음 차례가 스케줄러 퀀텀만큼(수십~수백 ms)
    /// 밀려, 수십 마이크로초짜리 패킷 간격이 수백 배로 늘어나고 프레임 하나가 수백 ms 를 잡아먹는다.
    /// 바쁜 대기가 코어를 쥐는 시간은 요청받은 지연을 넘지 않는다.
    /// </summary>
    private void InterPacketDelay(uint scpdTicks)
    {
        if (scpdTicks == 0) return;
        long target = NowNs + scpdTicks;
        while (!_acqStop && !_isStopping)
        {
            long remainingNs = target - NowNs;
            if (remainingNs <= 0) return;
            if (remainingNs > SenderSpinBelowMs * 1_000_000L) SleepSender((int)Math.Min(remainingNs / 1_000_000 - 1, SenderPollMs), wakeOnTrigger: false);
            else Thread.SpinWait(SenderSpinIterations);
        }
    }

    // ---- 리센드 ----

    private void Resend(ulong blockId, uint first, uint last)
    {
        uint scp = Registers.ReadU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset)) & 0xFFFF;
        uint scda = Registers.ReadU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScdaOffset));
        if (scp == 0 || scda == 0)
        {
            SetError("PACKETRESEND ignored: stream channel is closed (SCP/SCDA = 0)");
            return;
        }
        var dst = new IPEndPoint(FromU32(scda), (int)scp);
        if (last < first) last = first;

        SimFrameRecord? rec = null;
        lock (_history)
        {
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].BlockId == blockId) { rec = _history[i]; break; }
            }
        }

        bool extended = rec?.IsExtended
            ?? ((Registers.ReadU32(GvbsAddr.StreamChannel(0, GvbsAddr.SccfgOffset)) & SimStreamBits.SccfgExtendedIds) != 0);
        var errBuf = new byte[GvspConst.ExtendedHeaderSize];

        if (rec is null)
        {
            // 이력에 없는 블록: 요청된 패킷 id 마다 PACKET_UNAVAILABLE 오류 패킷(범위는 256 개로 제한)
            SetError($"PACKETRESEND for block {blockId} cannot be served: not in the resend history");
            ulong cappedLast = Math.Min(last, (ulong)first + 255);
            for (ulong id = first; id <= cappedLast; id++)
            {
                int n = WriteGvspHeader(errBuf, GvspConst.StatusPacketUnavailable, blockId, GvspConst.ContentPayload, (uint)id, extended);
                if (!SendGvsp(errBuf, n, dst)) return;
                Interlocked.Increment(ref _resendErrorPackets);
            }
            return;
        }

        var buf = new byte[(rec.IsExtended ? GvspConst.ExtendedHeaderSize : GvspConst.HeaderSize) + Math.Max(rec.DataBytesPerPacket, GvspConst.ImageLeaderDataSize)];
        ulong lastValid = (ulong)rec.PacketCount + 1;
        ulong cappedEnd = Math.Min(last, Math.Max(lastValid, (ulong)first) + 255);
        for (ulong id = first; id <= cappedEnd; id++)
        {
            int n;
            if (id == 0) n = BuildLeader(buf, rec, GvspConst.StatusPacketResend);
            else if (id <= (ulong)rec.PacketCount) n = BuildPayload(buf, rec, (uint)id, GvspConst.StatusPacketResend);
            else if (id == lastValid) n = BuildTrailer(buf, rec, GvspConst.StatusPacketResend);
            else
            {
                n = WriteGvspHeader(errBuf, GvspConst.StatusPacketUnavailable, blockId, GvspConst.ContentPayload, (uint)id, rec.IsExtended);
                if (!SendGvsp(errBuf, n, dst)) return;
                Interlocked.Increment(ref _resendErrorPackets);
                continue;
            }
            if (!SendGvsp(buf, n, dst)) return;
            Interlocked.Increment(ref _packetsResent);
        }
    }

    // ---- SCPS 파이어테스트 ----

    /// <summary>
    /// 요청 크기(IP 헤더 포함)의 테스트 패킷 하나를 SCDA:SCP 로 보낸다. 상한 초과면 보내지 않는다.
    /// SCDA/SCP 가 0 이면 보낼 곳이 없으므로 의도적으로 무시한다(<see cref="TestPacketsIgnored"/>) — 실제 장치와 같으며,
    /// 호스트는 채널(SCDA = 자기 주소, SCP = 자기 포트)을 먼저 열고 나서 SCPS 를 협상해야 한다.
    /// </summary>
    private void FireTestPacket(uint sizeWithIpHeader)
    {
        if (Opt.MaxPacketSize is int cap && sizeWithIpHeader > (uint)cap)
        {
            Interlocked.Increment(ref _testPacketsIgnored);
            return;
        }
        int udpPayload = (int)sizeWithIpHeader - GvspConst.IpUdpOverhead;
        if (udpPayload < GvspConst.HeaderSize)
        {
            Interlocked.Increment(ref _testPacketsIgnored);
            SetError($"SCPS fire test with size {sizeWithIpHeader} is too small for a GVSP packet");
            return;
        }
        uint scp = Registers.ReadU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset)) & 0xFFFF;
        uint scda = Registers.ReadU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScdaOffset));
        if (scp == 0 || scda == 0)
        {
            Interlocked.Increment(ref _testPacketsIgnored);
            SetError("SCPS fire test ignored: SCDA/SCP not set");
            return;
        }
        var test = new byte[udpPayload];   // 헤더까지 전부 0 — status 0, block 0, content type 0
        if (SendGvsp(test, test.Length, new IPEndPoint(FromU32(scda), (int)scp)))
            Interlocked.Increment(ref _testPacketsSent);
    }
}
