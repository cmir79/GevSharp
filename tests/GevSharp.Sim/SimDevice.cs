using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GevSharp.Gvcp;

namespace GevSharp.Sim;

/// <summary>
/// 인프로세스 GigE 장치 시뮬레이터 — GVCP 응답기 + GVSP 송신기. 루프백에서 라이브러리와 테스트를 대향시키기 위한 것이다.
/// 라이브러리에서 빌려 쓰는 것은 상수(<see cref="GvcpConst"/>, <see cref="GvbsAddr"/>, Gvsp.GvspConst)뿐이고
/// 패킷 조립·해석은 여기서 독립적으로 구현한다 — 양쪽이 같은 버그를 공유해 오류가 상쇄되는 일을 막는다.
/// 생명주기: 생성 → <see cref="Start"/>(소켓 바인드, 서버 스레드) → <see cref="Stop"/>/<see cref="Dispose"/>.
/// 카운터·이벤트는 테스트가 장치 쪽 관찰 결과를 확인하기 위한 것이다.
/// </summary>
public sealed partial class SimDevice : IDisposable
{
    private static readonly Lazy<string> _embeddedXml = new(LoadEmbeddedXml);

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double _nsPerClockTick = 1_000_000_000.0 / Stopwatch.Frequency;
    private readonly byte[] _mac = new byte[6];
    private readonly List<SimResendRequest> _resendRequests = new();

    private long _timestampBaseNs;
    private Socket? _gvcpSocket;
    private Socket? _gvspSocket;
    private Thread? _gvcpThread;
    private volatile bool _isStopping;
    private IPEndPoint? _gvcpEndPoint;

    private IPEndPoint? _owner;
    private readonly Stopwatch _ownerClock = new();

    private int _readRegCount, _writeRegCount, _readMemCount, _writeMemCount, _discoveryCount, _forceIpCount;
    private int _heartbeatObserved, _heartbeatTimeouts, _malformedCount, _errorCount;
    private int _framesSent, _packetsSent, _packetsDropped, _packetsResent, _resendErrorPackets, _testPacketsSent, _testPacketsIgnored, _sendErrors;
    private int _resendRequestsTrimmed;
    private int _maxCommandHandleMs;
    private string? _lastError;

    /// <summary><see cref="ResendRequests"/> 가 보관하는 최대 개수. 넘치면 오래된 것부터 버리고 <see cref="ResendRequestsTrimmed"/> 를 센다.</summary>
    public const int ResendRequestsCap = 1024;

    public SimDevice(SimDeviceOpt? opt = null)
    {
        Opt = opt ?? new SimDeviceOpt();
        // 소켓·부트스트랩 IP 레지스터 모두 IPv4 전제다. 다른 주소 계열을 0.0.0.0 으로 흘리지 않고 여기서 거절한다.
        if (Opt.BindAddress is null || Opt.BindAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("SimDevice supports IPv4 bind addresses only.", nameof(opt));
        GenApiXml = Opt.GenApiXml ?? DefaultGenApiXml;
        Registers = new SimRegisterMap(Encoding.UTF8.GetBytes(GenApiXml));
        InitBootstrap();
        ResetFeatures();
    }

    public SimDeviceOpt Opt { get; }

    /// <summary>레지스터 이미지. 테스트가 직접 값을 심거나 확인할 때 쓴다(프로토콜 접근 제어를 거치지 않는다).</summary>
    public SimRegisterMap Registers { get; }

    /// <summary>장치가 내보내는 GenApi XML 본문(First URL 이 가리키는 바이트의 원문).</summary>
    public string GenApiXml { get; }

    /// <summary>내장 SimCamera.xml 본문.</summary>
    public static string DefaultGenApiXml => _embeddedXml.Value;

    /// <summary>GVCP 소켓의 로컬 엔드포인트. <see cref="Start"/> 전에는 <see cref="InvalidOperationException"/>.</summary>
    public IPEndPoint GvcpEndPoint => _gvcpEndPoint ?? throw new InvalidOperationException("SimDevice is not started.");

    /// <summary>GVSP 송신 소켓의 포트(SCSP 레지스터 값). 시작 전에는 0.</summary>
    public int GvspSourcePort { get; private set; }

    public bool IsRunning => _gvcpThread is { IsAlive: true } && !_isStopping;

    /// <summary>1 GHz 단조 타임스탬프 카운터(리더 타임스탬프·래치에 쓰인다). TimestampControl 의 reset 으로 0 부터 다시 센다.</summary>
    public ulong TimestampTicks => (ulong)Math.Max(0, NowNs - Volatile.Read(ref _timestampBaseNs));

    /// <summary>장치 MAC(부트스트랩 0x0008/0x000C). 로컬 관리 주소 02:47:45:56:xx:xx — 뒤 두 바이트는 일련번호에서 결정적으로 뽑는다.</summary>
    public byte[] Mac => (byte[])_mac.Clone();

    // ---- 관찰용 카운터 ----
    public int ReadRegCount => Volatile.Read(ref _readRegCount);
    public int WriteRegCount => Volatile.Read(ref _writeRegCount);
    public int ReadMemCount => Volatile.Read(ref _readMemCount);
    public int WriteMemCount => Volatile.Read(ref _writeMemCount);
    public int DiscoveryCount => Volatile.Read(ref _discoveryCount);
    public int ForceIpCount => Volatile.Read(ref _forceIpCount);
    /// <summary>제어권 보유자가 CCP 레지스터를 READREG 한 횟수 — 호스트 하트비트가 도착한 흔적.</summary>
    public int HeartbeatObserved => Volatile.Read(ref _heartbeatObserved);
    /// <summary>하트비트 타임아웃으로 CCP 를 비운 횟수.</summary>
    public int HeartbeatTimeouts => Volatile.Read(ref _heartbeatTimeouts);
    public int MalformedCount => Volatile.Read(ref _malformedCount);
    public int ErrorCount => Volatile.Read(ref _errorCount);
    /// <summary>
    /// 응답기 스레드가 명령 하나를 붙들고 있던 최대 시간(ms) — 명령을 받아 처리를 마칠 때까지.
    /// 장치가 명령 안에서 스스로 기다린 시간(AcquisitionStop 이 송신 스레드를 거두는 등)이 그대로 여기에 잡힌다.
    /// 호스트가 재는 왕복 시간과 달리 양쪽 깨어남 지연이 섞이지 않으므로, 응답기가 무언가에 붙들리는 회귀를 좁게 잡는 값이다.
    /// </summary>
    public int MaxCommandHandleMs => Volatile.Read(ref _maxCommandHandleMs);
    /// <summary>
    /// 보낸 프레임 수. 트레일러가 회선에 오르기 **직전에** 올라간다 — 수신 측이 프레임을 완성으로 본 뒤에 세면
    /// "프레임은 받았는데 카운터는 옛 값" 인 창이 생겨 테스트가 순서를 못 믿게 되기 때문이다.
    /// 그래서 트레일러 송신이 실패하는 드문 경우(소켓 오류)에는 실제로 나가지 않은 프레임도 한 번 세어질 수 있다.
    /// </summary>
    public int FramesSent => Volatile.Read(ref _framesSent);
    /// <summary>첫 전송 GVSP 패킷 수(리더·페이로드·트레일러). 리센드·테스트 패킷은 별도.</summary>
    public int PacketsSent => Volatile.Read(ref _packetsSent);
    public int PacketsDropped => Volatile.Read(ref _packetsDropped);
    public int PacketsResent => Volatile.Read(ref _packetsResent);
    public int ResendErrorPackets => Volatile.Read(ref _resendErrorPackets);
    public int TestPacketsSent => Volatile.Read(ref _testPacketsSent);
    public int TestPacketsIgnored => Volatile.Read(ref _testPacketsIgnored);
    public int SendErrors => Volatile.Read(ref _sendErrors);

    /// <summary>수신한 PACKETRESEND 요청(무시된 것 포함) 최근 <see cref="ResendRequestsCap"/> 개 — 스냅숏.</summary>
    public IReadOnlyList<SimResendRequest> ResendRequests
    {
        get { lock (_resendRequests) return _resendRequests.ToArray(); }
    }

    /// <summary>상한에 밀려 <see cref="ResendRequests"/> 에서 버려진 요청 수.</summary>
    public int ResendRequestsTrimmed => Volatile.Read(ref _resendRequestsTrimmed);

    /// <summary><see cref="ResendRequests"/> 를 비운다(장시간 테스트가 주기적으로 부른다). 카운터는 건드리지 않는다.</summary>
    public void ClearResendRequests()
    {
        lock (_resendRequests) _resendRequests.Clear();
    }

    /// <summary>현재 CCP 를 쥔 엔드포인트. 없으면 null.</summary>
    public IPEndPoint? ControlOwner
    {
        get { lock (_gate) return _owner is null ? null : new IPEndPoint(_owner.Address, _owner.Port); }
    }

    /// <summary>마지막 오류 설명(영어). 잘못된 패킷, 거부된 요청, 소켓 오류 등. 정상 동작에서는 null.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    public bool IsAcquiring => Registers.ReadU32(SimFeatureAddr.AcquisitionStatus) != 0;

    /// <summary>제어권 보유자가 바뀔 때(획득·해제·타임아웃). 서버 스레드에서 호출된다.</summary>
    public event Action<IPEndPoint?>? ControlOwnerChanged;

    /// <summary>프레임 하나의 전송이 끝났을 때(블록 ID). 송신 스레드에서 호출된다.</summary>
    public event Action<ulong>? FrameSent;

    /// <summary>소켓을 열고 GVCP 서버 스레드를 시작한다. 이미 실행 중이면 <see cref="InvalidOperationException"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_gvcpThread is { IsAlive: true }) throw new InvalidOperationException("SimDevice is already started.");
        }
        _isStopping = false;

        var gvcp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var gvsp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            DisableConnReset(gvcp);
            DisableConnReset(gvsp);
            gvcp.ReceiveBufferSize = 1 << 20;
            gvcp.Bind(new IPEndPoint(Opt.BindAddress, Opt.GvcpPort));
            gvsp.SendBufferSize = 4 << 20;
            gvsp.Bind(new IPEndPoint(Opt.BindAddress, 0));
        }
        catch
        {
            gvcp.Dispose();
            gvsp.Dispose();
            throw;
        }

        _gvcpSocket = gvcp;
        _gvspSocket = gvsp;
        _gvcpEndPoint = (IPEndPoint)gvcp.LocalEndPoint!;
        GvspSourcePort = ((IPEndPoint)gvsp.LocalEndPoint!).Port;
        Registers.WriteU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScspOffset), (uint)GvspSourcePort);

        // 이 스레드가 PACKETRESEND 도 처리한다 — 선점되면 리센드가 늦어 멀쩡한 프레임이 보존 시간에 걸려 버려진다.
        var thread = new Thread(GvcpLoop) { IsBackground = true, Name = "GevSharp.Sim.Gvcp", Priority = ThreadPriority.AboveNormal };
        lock (_gate) _gvcpThread = thread;
        thread.Start();
    }

    /// <summary>획득을 멈추고 소켓을 닫고 스레드를 거둔다. 여러 번 불러도 된다. 레지스터 내용은 남는다.</summary>
    public void Stop()
    {
        _isStopping = true;
        StopAcquisition(join: true);

        Thread? thread;
        lock (_gate)
        {
            thread = _gvcpThread;
            _gvcpThread = null;
        }
        _gvcpSocket?.Dispose();
        _gvspSocket?.Dispose();
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(3000);
        _gvcpSocket = null;
        _gvspSocket = null;
        _gvcpEndPoint = null;
    }

    public void Dispose() => Stop();

    /// <summary>피처 페이지를 생성 시 옵션 값으로 되돌린다(UserSetLoad). FrameCounter 는 유지한다.</summary>
    public void ResetFeatures()
    {
        var r = Registers;
        r.WriteU32(SimFeatureAddr.Width, (uint)Opt.Width);
        r.WriteU32(SimFeatureAddr.Height, (uint)Opt.Height);
        r.WriteU32(SimFeatureAddr.OffsetX, 0);
        r.WriteU32(SimFeatureAddr.OffsetY, 0);
        r.WriteU32(SimFeatureAddr.PixelFormat, Opt.PixelFormat);
        r.WriteU32(SimFeatureAddr.ExposureTimeRaw, 10_000_000);   // 10 ms in 1 GHz ticks
        r.WriteU32(SimFeatureAddr.GainSelector, 0);
        for (int i = 0; i < SimFeatureAddr.GainCount; i++) r.WriteU32(SimFeatureAddr.GainRaw0 + (uint)(4 * i), 0);
        r.WriteU32(SimFeatureAddr.TriggerControl, 0);
        r.WriteU32(SimFeatureAddr.AcquisitionMode, SimFeatureAddr.AcquisitionModeContinuous);
        r.WriteU32(SimFeatureAddr.AcquisitionStart, 0);
        r.WriteU32(SimFeatureAddr.AcquisitionStop, 0);
        r.WriteF32(SimFeatureAddr.AcquisitionFrameRate, (float)Opt.FrameRateHz);
        r.WriteU32(SimFeatureAddr.TestPattern, SimFeatureAddr.TestPatternDiagonalRamp);
        r.WriteU32(SimFeatureAddr.UserSetSelector, 0);
        r.WriteU32(SimFeatureAddr.UserSetLoad, 0);
        r.WriteU32(SimFeatureAddr.AcquisitionFrameCount, 1);
        r.WriteU32(SimFeatureAddr.ReverseX, 0);
        r.WriteU32(SimFeatureAddr.WidthMax, 4096);
        r.WriteU32(SimFeatureAddr.HeightMax, 4096);
        r.WriteU32(SimFeatureAddr.TriggerSoftware, 0);
    }

    // ---- 부트스트랩 초기화 ----

    private void InitBootstrap()
    {
        var r = Registers;

        r.WriteU32(GvbsAddr.Version, 0x0002_0000);
        r.WriteU32(GvbsAddr.DeviceMode, GvbsAddr.DeviceModeBigEndian | 1);   // charset 1 = UTF-8

        // MAC: 로컬 관리 비트가 선 02:47:45:56 뒤에 일련번호 해시 2바이트
        ushort tail = SerialHash(Opt.SerialNumber);
        _mac[0] = 0x02; _mac[1] = 0x47; _mac[2] = 0x45; _mac[3] = 0x56;
        _mac[4] = (byte)(tail >> 8); _mac[5] = (byte)tail;
        r.WriteU32(GvbsAddr.MacHigh, (uint)(_mac[0] << 8 | _mac[1]));
        r.WriteU32(GvbsAddr.MacLow, (uint)(_mac[2] << 24 | _mac[3] << 16 | _mac[4] << 8 | _mac[5]));

        uint ip = ToU32(Opt.BindAddress);
        r.WriteU32(GvbsAddr.SupportedIpCfg, GvbsAddr.IpCfgPersistent | GvbsAddr.IpCfgDhcp | GvbsAddr.IpCfgLla);
        r.WriteU32(GvbsAddr.CurrentIpCfg, GvbsAddr.IpCfgPersistent);
        r.WriteU32(GvbsAddr.CurrentIp, ip);
        r.WriteU32(GvbsAddr.CurrentSubnet, 0xFF00_0000);
        r.WriteU32(GvbsAddr.CurrentGateway, 0);

        r.WriteString(GvbsAddr.ManufacturerName, GvbsAddr.ManufacturerNameLen, Opt.Manufacturer);
        r.WriteString(GvbsAddr.ModelName, GvbsAddr.ModelNameLen, Opt.Model);
        r.WriteString(GvbsAddr.DeviceVersion, GvbsAddr.DeviceVersionLen, Opt.DeviceVersion);
        r.WriteString(GvbsAddr.ManufacturerInfo, GvbsAddr.ManufacturerInfoLen, Opt.ManufacturerInfo);
        r.WriteString(GvbsAddr.SerialNumber, GvbsAddr.SerialNumberLen, Opt.SerialNumber);
        r.WriteString(GvbsAddr.UserDefinedName, GvbsAddr.UserDefinedNameLen, Opt.UserDefinedName);

        // First URL: Local:<file>;<hex addr>;<hex len> — 16진수에 0x 접두사 없음, 길이는 실제 XML 바이트 수
        r.WriteString(GvbsAddr.FirstUrl, GvbsAddr.UrlLen, $"Local:SimCamera.xml;{SimRegisterMap.XmlRegionBase:X};{Registers.XmlLength:X}");
        r.WriteString(GvbsAddr.SecondUrl, GvbsAddr.UrlLen, "");

        r.WriteU32(GvbsAddr.NumNetworkInterfaces, 1);
        r.WriteU32(GvbsAddr.PersistentIp0, ip);
        r.WriteU32(GvbsAddr.PersistentSubnet0, 0xFF00_0000);
        r.WriteU32(GvbsAddr.PersistentGateway0, 0);
        r.WriteU32(GvbsAddr.LinkSpeed0, 1000);

        r.WriteU32(GvbsAddr.NumMessageChannels, 0);
        r.WriteU32(GvbsAddr.NumStreamChannels, 1);
        r.WriteU32(GvbsAddr.NumActionSignals, 0);
        r.WriteU32(GvbsAddr.ActionDeviceKey, 0);
        r.WriteU32(GvbsAddr.NumActiveLinks, 1);
        r.WriteU32(GvbsAddr.GvspCapability, 0);
        r.WriteU32(GvbsAddr.MessageChannelCapability, 0);

        uint cap = GvbsAddr.GvcpCapConcatenation | GvbsAddr.GvcpCapWriteMem | GvbsAddr.GvcpCapPacketResend
                 | GvbsAddr.GvcpCapSerialNumber | GvbsAddr.GvcpCapNameRegister | GvbsAddr.GvcpCapCcpAppSocket;
        if (Opt.SupportPendingAck) cap |= GvbsAddr.GvcpCapPendingAck;
        r.WriteU32(GvbsAddr.GvcpCapability, cap);

        r.WriteU32(GvbsAddr.HeartbeatTimeout, (uint)Math.Max(0, Opt.HeartbeatTimeoutMs));
        r.WriteU32(GvbsAddr.TimestampTickFreqHigh, 0);
        r.WriteU32(GvbsAddr.TimestampTickFreqLow, 1_000_000_000);
        r.WriteU32(GvbsAddr.TimestampControl, 0);
        r.WriteU32(GvbsAddr.TimestampLatchedHigh, 0);
        r.WriteU32(GvbsAddr.TimestampLatchedLow, 0);
        r.WriteU32(GvbsAddr.DiscoveryAckDelay, 0);
        r.WriteU32(GvbsAddr.GvcpConfig, 0);
        r.WriteU32(GvbsAddr.PendingTimeout, (uint)Math.Max(0, Opt.PendingAckDelayMs));

        r.WriteU32(GvbsAddr.Ccp, 0);
        r.WriteU32(GvbsAddr.PrimaryAppPort, 0);
        r.WriteU32(GvbsAddr.PrimaryAppIp, 0);

        r.WriteU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset), 0);
        r.WriteU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset), (uint)Opt.DefaultPacketSize & GvbsAddr.ScpsSizeMask);
        r.WriteU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScpdOffset), 0);
        r.WriteU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScdaOffset), 0);
        r.WriteU32(GvbsAddr.StreamChannel(0, GvbsAddr.ScspOffset), 0);
        r.WriteU32(GvbsAddr.StreamChannel(0, GvbsAddr.SccOffset), SimStreamBits.SccPacketResend | SimStreamBits.SccExtendedIds);
        r.WriteU32(GvbsAddr.StreamChannel(0, GvbsAddr.SccfgOffset), Opt.ExtendedIds ? SimStreamBits.SccfgExtendedIds : 0);

        // 쓰기 보호 표 — 식별·능력·읽기 전용 상태 레지스터
        r.MarkReadOnly(GvbsAddr.Version, 4);
        r.MarkReadOnly(GvbsAddr.DeviceMode, 4);
        r.MarkReadOnly(GvbsAddr.MacHigh, 8);
        r.MarkReadOnly(GvbsAddr.SupportedIpCfg, 4);
        r.MarkReadOnly(GvbsAddr.CurrentIpCfg, 4);
        r.MarkReadOnly(GvbsAddr.CurrentIp, 4);
        r.MarkReadOnly(GvbsAddr.CurrentSubnet, 4);
        r.MarkReadOnly(GvbsAddr.CurrentGateway, 4);
        r.MarkReadOnly(GvbsAddr.ManufacturerName, GvbsAddr.ManufacturerNameLen);
        r.MarkReadOnly(GvbsAddr.ModelName, GvbsAddr.ModelNameLen);
        r.MarkReadOnly(GvbsAddr.DeviceVersion, GvbsAddr.DeviceVersionLen);
        r.MarkReadOnly(GvbsAddr.ManufacturerInfo, GvbsAddr.ManufacturerInfoLen);
        r.MarkReadOnly(GvbsAddr.SerialNumber, GvbsAddr.SerialNumberLen);
        r.MarkReadOnly(GvbsAddr.FirstUrl, GvbsAddr.UrlLen);
        r.MarkReadOnly(GvbsAddr.SecondUrl, GvbsAddr.UrlLen);
        r.MarkReadOnly(GvbsAddr.NumNetworkInterfaces, 4);
        r.MarkReadOnly(GvbsAddr.LinkSpeed0, 4);
        r.MarkReadOnly(GvbsAddr.NumMessageChannels, 4);
        r.MarkReadOnly(GvbsAddr.NumStreamChannels, 4);
        r.MarkReadOnly(GvbsAddr.NumActionSignals, 4);
        r.MarkReadOnly(GvbsAddr.NumActiveLinks, 4);
        r.MarkReadOnly(GvbsAddr.GvspCapability, 4);
        r.MarkReadOnly(GvbsAddr.MessageChannelCapability, 4);
        r.MarkReadOnly(GvbsAddr.GvcpCapability, 4);
        r.MarkReadOnly(GvbsAddr.TimestampTickFreqHigh, 8);
        r.MarkReadOnly(GvbsAddr.TimestampLatchedHigh, 8);
        r.MarkReadOnly(GvbsAddr.PendingTimeout, 4);
        r.MarkReadOnly(GvbsAddr.PrimaryAppPort, 4);
        r.MarkReadOnly(GvbsAddr.PrimaryAppIp, 4);
        r.MarkReadOnly(GvbsAddr.StreamChannel(0, GvbsAddr.ScspOffset), 4);
        r.MarkReadOnly(GvbsAddr.StreamChannel(0, GvbsAddr.SccOffset), 4);
        r.MarkReadOnly(SimFeatureAddr.AcquisitionStatus, 4);
        r.MarkReadOnly(SimFeatureAddr.WidthMax, 4);
        r.MarkReadOnly(SimFeatureAddr.HeightMax, 4);
        r.MarkReadOnly(SimFeatureAddr.FrameCounter, 4);
    }

    private static ushort SerialHash(string serial)
    {
        uint h = 2166136261;
        foreach (char c in serial ?? "")
        {
            h ^= c;
            h *= 16777619;
        }
        return (ushort)(h ^ (h >> 16));
    }

    internal static uint ToU32(IPAddress address)
    {
        // IPv4 가 아니면 0 을 돌려주지 않는다 — 0.0.0.0 이 레지스터에 조용히 실리는 길을 막는다
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"expected an IPv4 address, got {address.AddressFamily}", nameof(address));
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    internal static IPAddress FromU32(uint value)
        => new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    private long NowNs => (long)(_clock.ElapsedTicks * _nsPerClockTick);

    private static string LoadEmbeddedXml()
    {
        const string name = "GevSharp.Sim.Assets.SimCamera.xml";
        using var stream = typeof(SimDevice).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Windows 는 UDP 소켓이 보낸 데이터그램에 ICMP 포트 도달 불가가 돌아오면 다음 수신에서 ConnectionReset 을 던진다.
    /// 시뮬레이터는 상대가 사라져도 계속 돌아야 하므로 그 보고를 끈다(다른 플랫폼에서는 무시된다).
    /// </summary>
    private static void DisableConnReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            const int sioUdpConnReset = unchecked((int)0x9800000C);
            socket.IOControl(sioUdpConnReset, new byte[4], null);
        }
        catch (Exception)
        {
            // 지원하지 않는 환경이면 수신 루프의 예외 처리로 충분하다
        }
    }

    private void SetError(string message)
    {
        Volatile.Write(ref _lastError, message);
        Interlocked.Increment(ref _errorCount);
    }
}
