using System.Net;
using GevSharp.Gvcp;

namespace GevSharp;

/// <summary>
/// 장치 제어 세션 — GVCP 채널, CCP 제어권, 하트비트, 레지스터/메모리 접근, <see cref="IGevPort"/>.
/// 파티션: 이 파일(열기·하트비트·닫기), GevDevice.Access.cs(레지스터/메모리/포트).
/// XML(GetXmlAsync)·노드맵(GetNodeMapAsync)·스트림(OpenStreamAsync)은 각 모듈이 partial 파티션으로 덧붙인다.
/// </summary>
public sealed partial class GevDevice : IGevPort, IAsyncDisposable
{
    private const string LogSrc = "GevDevice";
    /// <summary>연속 하트비트 실패 이 횟수 = 제어권 상실.</summary>
    internal const int HeartbeatMaxFailures = 3;
    /// <summary>닫을 때 CCP = 0 쓰기에 주는 최대 시간 — 채널 재시도 예산 전부를 닫기에 쓰지 않는다.</summary>
    internal const int CcpReleaseMaxMs = 2000;

    private const int StateOpening = 0;
    private const int StateOpen = 1;
    private const int StateControlLost = 2;
    private const int StateDisposed = 3;

    private readonly GevDeviceOpt _opt;
    private readonly CancellationTokenSource _heartbeatCts = new();
    private Task? _heartbeatTask;
    private GevDeviceInfo _info = null!;
    private int _state = StateOpening;
    private volatile bool _isControlling;
    /// <summary>CCP 쓰기를 실제로 내보냈다 — ACK 를 못 봤어도(취소·유실) 장치는 이미 적용했을 수 있으므로 닫을 때 놓아 줘야 한다.</summary>
    private volatile bool _ccpWriteSent;

    private GevDevice(IPEndPoint device, IPAddress localAddress, GevDeviceOpt opt)
    {
        Address = device.Address;
        LocalAddress = localAddress;
        _opt = opt;
        Gvcp = new GvcpChannel(device, localAddress, new GvcpChannelOpt
        {
            TimeoutMs = opt.GvcpTimeoutMs,
            Retries = opt.GvcpRetries,
            // 여는 동안에는 하트비트가 아직 돌지 않는다 — 좁힐 이유가 없고, 좁히면 열기 순서에서 PENDING_ACK 로 시간을 버는
            // 장치가 열리지 못한다. 채널 기본값으로 열고, 하트비트를 시작하기 직전에 InitAsync 가 실제 값으로 좁힌다.
            MaxPendingAckWaitMs = opt.MaxPendingAckWaitMs ?? GvcpChannelOpt.DefaultMaxPendingAckWaitMs,
        });
    }

    /// <summary>열 때 부트스트랩 블록에서 다시 읽은 식별 정보.</summary>
    public GevDeviceInfo Info => _info;
    public IPAddress Address { get; }
    /// <summary>GVCP 소켓이 묶인 호스트 주소. 스트림의 SCDA 로도 쓴다.</summary>
    public IPAddress LocalAddress { get; }
    public GevAccessMode AccessMode => _opt.AccessMode;
    /// <summary>열려 있고 제어권을 잃지 않았다.</summary>
    public bool IsOpen => Volatile.Read(ref _state) == StateOpen;
    /// <summary>GVBS 0x0934.</summary>
    public uint GvcpCapability { get; private set; }
    /// <summary>GVBS 0x093C/0x0940 (Hz). 읽지 못하면 0.</summary>
    public ulong TimestampTickFrequency { get; private set; }
    /// <summary>장치가 실제로 적용한 하트비트 타임아웃(GVBS 0x0938 을 다시 읽은 값).</summary>
    public int DeviceHeartbeatTimeoutMs { get; private set; }
    /// <summary>하트비트 주기. 읽기 전용 세션은 0.</summary>
    public int HeartbeatPeriodMs { get; private set; }
    /// <summary>저수준 채널 — 스트림이 PACKETRESEND 에 쓴다.</summary>
    public GvcpChannel Gvcp { get; }
    /// <summary>하트비트가 연속 실패했거나 CCP 가 풀렸다. 라이브러리 스레드에서 불린다 — 가볍게 처리한다.</summary>
    public event Action<GevDevice, Exception?>? ControlLost;

    internal GevDeviceOpt Opt => _opt;

    // ------------------------------------------------------------------ open

    /// <summary>탐색 결과로 연다. 로컬 주소는 옵션 → 응답을 들은 인터페이스 순으로 정한다.</summary>
    public static Task<GevDevice> OpenAsync(GevDeviceInfo info, GevDeviceOpt? opt = null, CancellationToken ct = default)
    {
        if (info is null) throw new ArgumentNullException(nameof(info));
        var local = opt?.LocalAddress ?? (IsUsableLocal(info.InterfaceAddress) ? info.InterfaceAddress : null);
        return OpenCoreAsync(new IPEndPoint(info.Address, GvcpConst.Port), local, opt, ct);
    }

    /// <summary>주소로 연다. 로컬 주소는 옵션 → 같은 서브넷 인터페이스 → OS 라우팅 순으로 정한다.</summary>
    public static Task<GevDevice> OpenAsync(IPAddress address, GevDeviceOpt? opt = null, CancellationToken ct = default)
    {
        if (address is null) throw new ArgumentNullException(nameof(address));
        return OpenCoreAsync(new IPEndPoint(address, GvcpConst.Port), opt?.LocalAddress, opt, ct);
    }

    /// <summary>포트를 지정해 연다 — 표준 포트가 아닌 시뮬레이터용.</summary>
    internal static Task<GevDevice> OpenAsync(IPEndPoint device, GevDeviceOpt? opt = null, CancellationToken ct = default)
    {
        if (device is null) throw new ArgumentNullException(nameof(device));
        return OpenCoreAsync(device, opt?.LocalAddress, opt, ct);
    }

    /// <summary>
    /// 하트비트가 도는 세션에서 PENDING_ACK 대기에 줄 수 있는 최대치.
    /// 장치가 PENDING_ACK 로 답한 요청은 재전송되지 않으므로(<see cref="GvcpChannel"/>), 그런 요청 하나가 GVCP 줄을 붙드는
    /// 시간은 응답 창(GvcpTimeoutMs) + 이 값이다. 하트비트는 그 줄에 같이 서므로 마지막 CCP 읽기 이후 최악의 공백은
    /// (주기) + (붙들린 시간) + (CCP 읽기 한 왕복) 이 되고, 이것이 장치 타임아웃 안에 들어와야 아무것도 실패하지 않았는데
    /// 장치가 제어권을 거둬 가는 일이 없다. 그 식을 이 값에 대해 푼 것이다.
    /// (응답이 아예 없어 재전송으로 넘어가는 요청은 여기서 다루지 않는다 — 그때는 하트비트도 닿지 않으므로
    /// 제어권 상실이 거짓 경보가 아니다.)
    /// 여유가 남지 않는 설정에서도 PENDING_ACK 이 최소한 응답 창 하나만큼은 연장할 수 있게 바닥을 두고, 그 바닥이
    /// 이겼다는 것은 위 식이 성립하지 않는다는 뜻이므로 경고로 알린다.
    /// </summary>
    internal static int AutoPendingAckWaitMs(int deviceTimeoutMs, int periodMs, int gvcpTimeoutMs)
    {
        var budgetMs = deviceTimeoutMs - periodMs - 2 * gvcpTimeoutMs;
        if (budgetMs >= gvcpTimeoutMs) return budgetMs;
        GevLog.Warn(LogSrc, $"GVCP response window {gvcpTimeoutMs} ms leaves no PENDING_ACK budget inside the device heartbeat timeout {deviceTimeoutMs} ms (heartbeat period {periodMs} ms); capping the PENDING_ACK wait at {gvcpTimeoutMs} ms, control may drop on a slow command");
        return gvcpTimeoutMs;
    }

    private static bool IsUsableLocal(IPAddress? a)
        => a is not null && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.None);

    private static async Task<GevDevice> OpenCoreAsync(IPEndPoint endpoint, IPAddress? local, GevDeviceOpt? opt, CancellationToken ct)
    {
        opt ??= new GevDeviceOpt();
        opt.Validate();
        local ??= GevNet.ResolveLocalAddress(endpoint.Address);

        var device = new GevDevice(endpoint, local, opt);
        try
        {
            await device.InitAsync(ct).ConfigureAwait(false);
            return device;
        }
        catch
        {
            await device.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>열기 순서: 부트스트랩 블록 → 능력·틱 주파수 → CCP → 하트비트 타임아웃 → 하트비트 시작.</summary>
    private async Task InitAsync(CancellationToken ct)
    {
        _info = await GevDeviceInfo.ReadFromDeviceAsync(Gvcp, LocalAddress, ct).ConfigureAwait(false);
        GvcpCapability = await ReadRegCoreAsync(GvbsAddr.GvcpCapability, ct).ConfigureAwait(false);
        DeviceHeartbeatTimeoutMs = (int)await ReadRegCoreAsync(GvbsAddr.HeartbeatTimeout, ct).ConfigureAwait(false);
        TimestampTickFrequency = await ReadTickFrequencyAsync(ct).ConfigureAwait(false);
        GevLog.Info(LogSrc, $"opened {_info.Manufacturer} {_info.Model} [{_info.SerialNumber}] at {Address} via {LocalAddress} (spec {_info.SpecMajor}.{_info.SpecMinor}, cap 0x{GvcpCapability:X8}, tick {TimestampTickFrequency} Hz)");

        if (_opt.AccessMode == GevAccessMode.ReadOnly)
        {
            Volatile.Write(ref _state, StateOpen);
            return;
        }

        var ccp = GvbsAddr.CcpControl;
        if (_opt.AccessMode == GevAccessMode.Exclusive) ccp |= GvbsAddr.CcpExclusive;
        if (_opt.AllowSwitchover) ccp |= GvbsAddr.CcpSwitchoverEnable;
        // 보내기 전에 표시한다 — 취소되거나 ACK 가 유실되어도 장치는 CCP 를 적용했을 수 있고,
        // 그러면 실패한 열기가 놓아 주지 않는 한 장치는 자기 하트비트 타임아웃까지 잠긴 채로 남는다(R21).
        _ccpWriteSent = true;
        try
        {
            await WriteRegCoreAsync(GvbsAddr.Ccp, ccp, ct).ConfigureAwait(false);
        }
        catch (GevStatusException ex) when (ex.Status == GvcpConst.StatusAccessDenied)
        {
            // 장치가 거절했다 = 다른 애플리케이션의 제어권이다. 남의 것을 0 으로 지우면 안 된다.
            _ccpWriteSent = false;
            throw new GevControlLostException("device is controlled by another application");
        }
        _isControlling = true;

        try
        {
            await WriteRegCoreAsync(GvbsAddr.HeartbeatTimeout, (uint)_opt.HeartbeatTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (GevStatusException ex)
        {
            GevLog.Warn(LogSrc, $"device rejected heartbeat timeout {_opt.HeartbeatTimeoutMs} ms ({GvcpConst.StatusName(ex.Status)}); keeping the device value");
        }
        DeviceHeartbeatTimeoutMs = (int)await ReadRegCoreAsync(GvbsAddr.HeartbeatTimeout, ct).ConfigureAwait(false);

        var effectiveTimeout = DeviceHeartbeatTimeoutMs > 0 ? DeviceHeartbeatTimeoutMs : _opt.HeartbeatTimeoutMs;
        HeartbeatPeriodMs = _opt.HeartbeatPeriodMs ?? Math.Max(1, effectiveTimeout / 3);
        if (HeartbeatPeriodMs >= effectiveTimeout)
            GevLog.Warn(LogSrc, $"heartbeat period {HeartbeatPeriodMs} ms is not shorter than the device timeout {effectiveTimeout} ms; control may drop");
        if (_opt.MaxPendingAckWaitMs is null)
            Gvcp.SetMaxPendingAckWaitMs(AutoPendingAckWaitMs(effectiveTimeout, HeartbeatPeriodMs, _opt.GvcpTimeoutMs));

        Volatile.Write(ref _state, StateOpen);
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(HeartbeatPeriodMs, _heartbeatCts.Token));
        GevLog.Debug(LogSrc, $"control acquired (CCP 0x{ccp:X}), heartbeat every {HeartbeatPeriodMs} ms, device timeout {DeviceHeartbeatTimeoutMs} ms");
    }

    private async Task<ulong> ReadTickFrequencyAsync(CancellationToken ct)
    {
        try
        {
            var high = await ReadRegCoreAsync(GvbsAddr.TimestampTickFreqHigh, ct).ConfigureAwait(false);
            var low = await ReadRegCoreAsync(GvbsAddr.TimestampTickFreqLow, ct).ConfigureAwait(false);
            return ((ulong)high << 32) | low;
        }
        catch (GevStatusException ex)
        {
            GevLog.Debug(LogSrc, $"timestamp tick frequency not readable ({GvcpConst.StatusName(ex.Status)}); reported as 0");
            return 0;
        }
    }

    // ------------------------------------------------------------------ heartbeat

    /// <summary>
    /// 주기마다 CCP 를 읽는다. 연속 N 회 실패, 또는 제어 비트가 사라지면 제어권 상실.
    /// 어떤 예외로 루프가 끝나더라도 취소가 아닌 한 반드시 <see cref="OnControlLost"/> 를 거친다 — 하트비트만 조용히 죽고
    /// <see cref="IsOpen"/> 은 true 로 남는 상태(장치는 자기 타임아웃으로 CCP 를 놓는데 호출자는 모르는 상태)를 만들지 않는다.
    /// </summary>
    private async Task HeartbeatLoopAsync(int periodMs, CancellationToken ct)
    {
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(periodMs, ct).ConfigureAwait(false);

                uint ccp;
                try
                {
                    var ack = await Gvcp.RequestAsync(GvcpCmd.ReadReg(GvbsAddr.Ccp), ct).ConfigureAwait(false);
                    ccp = ack.GetRegValue(0);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 예외 종류를 가리지 않는다 — 무엇이든 하트비트가 가지 않은 것이고, 연속되면 제어권 상실이다.
                    if (ct.IsCancellationRequested) return;
                    failures++;
                    GevLog.Warn(LogSrc, $"heartbeat failed ({failures}/{HeartbeatMaxFailures}): {ex.Message}");
                    if (failures >= HeartbeatMaxFailures)
                    {
                        OnControlLost(ex);
                        return;
                    }
                    continue;
                }

                failures = 0;
                if ((ccp & (GvbsAddr.CcpControl | GvbsAddr.CcpExclusive)) == 0)
                {
                    OnControlLost(new GevControlLostException($"control channel privilege was released (CCP reads 0x{ccp:X})"));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 닫는 중 — 정상 종료.
        }
        catch (Exception ex)
        {
            GevLog.Error(LogSrc, "heartbeat loop stopped unexpectedly", ex);
            OnControlLost(ex);
        }
    }

    /// <summary>
    /// 상태를 ControlLost 로 바꾸고 이벤트를 스레드 풀에서 올린다. 하트비트 태스크 안에서 직접 부르면
    /// 핸들러가 <see cref="DisposeAsync"/> 를 기다릴 때 그 태스크 자신을 기다리게 되어 멈춘다 — 그래서 분리한다.
    /// </summary>
    private void OnControlLost(Exception? cause)
    {
        if (Interlocked.CompareExchange(ref _state, StateControlLost, StateOpen) != StateOpen) return;
        // 제어권을 잃은 것이 확인됐다 — 닫을 때 CCP = 0 을 쓰지 않는다. 이미 우리 것이 아니고,
        // 다른 애플리케이션이 가져갔다면 남의 제어권을 지우려는 쓰기가 된다.
        _isControlling = false;
        _ccpWriteSent = false;
        GevLog.Error(LogSrc, $"control of {Address} lost", cause);
        var handler = ControlLost;
        if (handler is null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                handler(this, cause);
            }
            catch (Exception ex)
            {
                GevLog.Error(LogSrc, "ControlLost handler threw", ex);
            }
        });
    }

    // ------------------------------------------------------------------ state / dispose

    private void ThrowIfClosed()
    {
        switch (Volatile.Read(ref _state))
        {
            case StateDisposed:
                throw new ObjectDisposedException(nameof(GevDevice));
            case StateControlLost:
                throw new GevControlLostException($"control of {Address} was lost; reopen the device");
        }
    }

    /// <summary>하트비트를 멈추고, 제어 중이면 CCP = 0 을 써서 놓고, 채널을 닫는다. 몇 번 불러도 안전하다.</summary>
    public async ValueTask DisposeAsync()
    {
        var previous = Interlocked.Exchange(ref _state, StateDisposed);
        if (previous == StateDisposed) return;

        _heartbeatCts.Cancel();
        if (_heartbeatTask is not null)
        {
            try
            {
                await _heartbeatTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GevLog.Debug(LogSrc, $"heartbeat task ended with {ex.GetType().Name}: {ex.Message}");
            }
        }

        // _isControlling 은 ACK 를 본 뒤에야 선다. ACK 를 못 본 채 끝난 CCP 쓰기(취소·타임아웃)도 장치에는 적용됐을 수 있으므로
        // _ccpWriteSent 만으로도 해제를 시도한다 — 이미 풀려 있으면 장치가 그냥 받아 준다.
        if ((_isControlling || _ccpWriteSent) && !Gvcp.IsDisposed)
        {
            // 닫기는 오래 붙들지 않는다 — 채널의 재시도 예산 전부가 아니라 짧은 고정 예산만 준다.
            // 놓지 못해도 장치는 자기 하트비트 타임아웃으로 알아서 푼다.
            var releaseBudgetMs = (int)Math.Min((long)_opt.GvcpTimeoutMs * 2, CcpReleaseMaxMs);
            using var releaseCts = new CancellationTokenSource(releaseBudgetMs);
            try
            {
                await Gvcp.RequestAsync(GvcpCmd.WriteReg(GvbsAddr.Ccp, 0), releaseCts.Token).ConfigureAwait(false);
                GevLog.Debug(LogSrc, $"control of {Address} released");
            }
            catch (OperationCanceledException)
            {
                GevLog.Warn(LogSrc, $"releasing control of {Address} timed out after {releaseBudgetMs} ms; the device will drop it on its own heartbeat timeout");
            }
            catch (Exception ex) when (ex is GevException or ObjectDisposedException)
            {
                GevLog.Warn(LogSrc, $"failed to release control of {Address}: {ex.Message}");
            }
            _isControlling = false;
            _ccpWriteSent = false;
        }

        Gvcp.Dispose();
        _heartbeatCts.Dispose();
        GevLog.Info(LogSrc, $"closed {Address}");
    }

    // GetXmlAsync (Xml 모듈), GetNodeMapAsync (GenApi 모듈), OpenStreamAsync (Gvsp 모듈) 는 각 모듈의 partial 파티션에 들어간다.
}
