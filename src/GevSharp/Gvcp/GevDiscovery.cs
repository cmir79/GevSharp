using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GevSharp.Gvcp;

namespace GevSharp;

/// <summary>탐색 옵션. 시간 단위는 ms.</summary>
public sealed class GevDiscoveryOpt
{
    /// <summary>응답을 모으는 시간.</summary>
    public int TimeoutMs { get; set; } = 1000;
    /// <summary>창 안에서 DISCOVERY_CMD 를 보내는 총 횟수(첫 전송 포함). 늦게 켜진 장치·유실된 첫 패킷을 잡는다.</summary>
    public int Repeat { get; set; } = 2;
    /// <summary>null = 동작 중인 모든 IPv4 인터페이스(루프백 제외).</summary>
    public IReadOnlyList<IPAddress>? Interfaces { get; set; }
    /// <summary>255.255.255.255 로 보낸다.</summary>
    public bool LimitedBroadcast { get; set; } = true;
    /// <summary>인터페이스 서브넷의 지향 브로드캐스트로도 보낸다.</summary>
    public bool DirectedBroadcast { get; set; } = true;

    /// <summary>브로드캐스트 대상 UDP 포트 — 표준 포트가 아닌 곳에서 듣는 시뮬레이터용.</summary>
    internal int Port { get; set; } = GvcpConst.Port;
    /// <summary>브로드캐스트에 더해 유니캐스트로도 보낼 대상 — 루프백 응답기로 브로드캐스트 경로(소켓·반복·수신·병합)를 시험하기 위한 것.</summary>
    internal IReadOnlyList<IPEndPoint>? UnicastTargets { get; set; }
}

/// <summary>
/// 다중 인터페이스 장치 탐색·유니캐스트 프로브·FORCEIP. 인터페이스마다 소켓을 (ifaceIp, 0) 에 묶고 제한/지향 브로드캐스트 둘 다로 보낸다 —
/// 카메라 전용 NIC 처럼 기본 경로가 아닌 인터페이스도 빠뜨리지 않기 위해서다.
/// </summary>
public static class GevDiscovery
{
    private const string LogSrc = "GevDiscovery";
    private const int RepeatIntervalMs = 200;
    private const int ReceiveBufferBytes = 256 * 1024;
    /// <summary>한 인터페이스의 수신이 이만큼 연달아 실패하면 그 인터페이스는 이번 창에서 포기한다(로그로 알린다).</summary>
    private const int RxMaxConsecutiveFailures = 8;
    private static int s_reqIdCounter;

    /// <summary>모든(또는 지정한) 인터페이스에 DISCOVERY_CMD 를 브로드캐스트하고 창 동안 응답을 모아 MAC 으로 중복을 제거한다.</summary>
    public static async Task<IReadOnlyList<GevDeviceInfo>> DiscoverAsync(GevDiscoveryOpt? opt = null, CancellationToken ct = default)
    {
        opt ??= new GevDiscoveryOpt();
        if (opt.TimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(opt), "TimeoutMs must be positive");
        var repeat = Math.Max(1, opt.Repeat);

        var ifaces = SelectInterfaces(opt.Interfaces);
        if (ifaces.Count == 0)
        {
            GevLog.Warn(LogSrc, "no usable IPv4 interface for discovery");
            return Array.Empty<GevDeviceInfo>();
        }

        var reqId = GvcpChannel.NextReqId(ref s_reqIdCounter);
        var packet = GvcpCmd.Discovery(allowBroadcastAck: true).ToArray(reqId);

        var tasks = new Task<List<GevDeviceInfo>>[ifaces.Count];
        for (var i = 0; i < ifaces.Count; i++)
            tasks[i] = DiscoverOnInterfaceAsync(ifaces[i], packet, opt, repeat, ct);
        var perInterface = await Task.WhenAll(tasks).ConfigureAwait(false);

        var all = new List<GevDeviceInfo>();
        foreach (var list in perInterface) all.AddRange(list);
        var result = Dedupe(all);
        GevLog.Info(LogSrc, $"discovery finished: {result.Count} device(s) from {all.Count} reply(ies) on {ifaces.Count} interface(s)");
        return result;
    }

    /// <summary>
    /// 이 인터페이스에서 DISCOVERY_CMD 를 보낼 곳들 — 순서대로 제한 브로드캐스트, 지향 브로드캐스트, 호출자가 준 유니캐스트.
    /// <para>
    /// 둘 다 보내는 이유가 갈린다. 제한 브로드캐스트(255.255.255.255)는 라우팅 표를 타지 않고 소켓이 묶인 인터페이스로 그냥 나가므로
    /// 카메라 전용 NIC 처럼 기본 경로가 아닌 곳도 닿지만, 그것을 걸러 내는 스택·장치가 있다. 지향 브로드캐스트는 서브넷 마스크를
    /// 알아야 만들 수 있고(모르면 건너뛴다), 마스크가 /0 이면 제한 브로드캐스트와 같은 주소가 되므로 같은 곳으로 두 번 보내지 않는다.
    /// </para>
    /// </summary>
    internal static List<IPEndPoint> BuildTargets(GevNet.IfInfo iface, GevDiscoveryOpt opt)
    {
        var targets = new List<IPEndPoint>(2);
        if (opt.LimitedBroadcast)
            targets.Add(new IPEndPoint(IPAddress.Broadcast, opt.Port));
        if (opt.DirectedBroadcast)
        {
            var directed = iface.DirectedBroadcast;
            if (directed is null)
                GevLog.Debug(LogSrc, $"{iface}: subnet mask unknown, directed broadcast skipped");
            else if (!directed.Equals(IPAddress.Broadcast))
                targets.Add(new IPEndPoint(directed, opt.Port));
        }
        if (opt.UnicastTargets is not null)
            targets.AddRange(opt.UnicastTargets);
        return targets;
    }

    private static async Task<List<GevDeviceInfo>> DiscoverOnInterfaceAsync(GevNet.IfInfo iface, byte[] packet, GevDiscoveryOpt opt, int repeat, CancellationToken ct)
    {
        var found = new List<GevDeviceInfo>();
        var targets = BuildTargets(iface, opt);
        if (targets.Count == 0)
        {
            GevLog.Warn(LogSrc, $"{iface}: no discovery target (both broadcast modes disabled or mask unknown)");
            return found;
        }

        UdpClient client;
        try
        {
            client = new UdpClient(AddressFamily.InterNetwork);
            client.EnableBroadcast = true;
            client.Client.Bind(new IPEndPoint(iface.Address, 0));
            client.Client.ReceiveBufferSize = ReceiveBufferBytes;
            GevNet.DisableIcmpReset(client.Client);
        }
        catch (SocketException ex)
        {
            GevLog.Warn(LogSrc, $"{iface}: cannot bind a discovery socket ({ex.SocketErrorCode})", ex);
            return found;
        }

        using (client)
        {
            var receiveTask = ReceiveDiscoveryRepliesAsync(client, iface.Address, found);
            var startMs = GevClock.NowMs();
            var intervalMs = Math.Min(RepeatIntervalMs, Math.Max(1, opt.TimeoutMs / repeat));
            try
            {
                for (var r = 0; r < repeat; r++)
                {
                    foreach (var target in targets)
                    {
                        try
                        {
                            await client.SendAsync(packet, packet.Length, target).ConfigureAwait(false);
                        }
                        catch (SocketException ex)
                        {
                            GevLog.Warn(LogSrc, $"{iface}: DISCOVERY_CMD to {target} failed ({ex.SocketErrorCode})");
                        }
                    }
                    if (r < repeat - 1)
                        await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                }
                // 시계 값이 어긋나도 창 길이를 넘겨 기다리지 않는다.
                var remainingMs = Math.Min(opt.TimeoutMs - (GevClock.NowMs() - startMs), opt.TimeoutMs);
                if (remainingMs > 0)
                    await Task.Delay((int)remainingMs, ct).ConfigureAwait(false);
            }
            finally
            {
                // 소켓을 닫아 수신 루프를 깨운다 — 취소여도 같은 경로로 정리한다.
                client.Close();
                await receiveTask.ConfigureAwait(false);
            }
        }
        return found;
    }

    /// <summary>
    /// 소켓이 닫힐 때까지 DISCOVERY_ACK 를 받아 목록에 넣는다. 짧은 응답은 경고만 남기고 건너뛴다.
    /// 창이 끝나 소켓이 닫히면 조용히 끝나고, 그 밖의 소켓 오류는 로그를 남기고 계속 받는다(연속 실패 상한까지) —
    /// 한 인터페이스의 일시 오류가 그 인터페이스의 장치를 소리 없이 빠뜨리지 않게.
    /// </summary>
    private static async Task ReceiveDiscoveryRepliesAsync(UdpClient client, IPAddress ifaceAddress, List<GevDeviceInfo> found)
    {
        var consecutiveFailures = 0;
        while (true)
        {
            UdpReceiveResult r;
            try
            {
                r = await client.ReceiveAsync().ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.Interrupted or SocketError.OperationAborted or SocketError.Shutdown or SocketError.NotSocket)
            {
                // 창이 끝나 소켓을 닫았다 — 정상 종료.
                return;
            }
            catch (SocketException ex)
            {
                consecutiveFailures++;
                GevLog.Warn(LogSrc, $"{ifaceAddress}: discovery receive failed ({ex.SocketErrorCode}, {consecutiveFailures} in a row)", ex);
                if (consecutiveFailures >= RxMaxConsecutiveFailures)
                {
                    GevLog.Error(LogSrc, $"{ifaceAddress}: receive keeps failing; devices on this interface may be missing from this scan");
                    return;
                }
                continue;
            }
            catch (Exception ex)
            {
                GevLog.Warn(LogSrc, $"{ifaceAddress}: discovery receive failed", ex);
                return;
            }

            var info = ParseDiscoveryReply(r.Buffer, r.RemoteEndPoint, ifaceAddress);
            if (info is not null) found.Add(info);
        }
    }

    /// <summary>응답 한 개를 해석한다. ack 종류·상태·길이가 맞지 않으면 null (로그).</summary>
    internal static GevDeviceInfo? ParseDiscoveryReply(byte[] buffer, IPEndPoint from, IPAddress ifaceAddress)
    {
        if (!GvcpAckHeader.TryParse(buffer, out var header))
        {
            GevLog.Warn(LogSrc, $"{ifaceAddress}: malformed reply from {from} ({buffer.Length} bytes) skipped");
            return null;
        }
        if (header.Command != GvcpConst.DiscoveryAck)
        {
            GevLog.Debug(LogSrc, $"{ifaceAddress}: ignored {GvcpPacket.CommandName(header.Command)} (0x{header.Command:X4}) from {from} on the discovery socket");
            return null;
        }
        if (header.IsError)
        {
            GevLog.Warn(LogSrc, $"{ifaceAddress}: DISCOVERY_ACK from {from} carries error status 0x{header.Status:X4} ({GvcpConst.StatusName(header.Status)}); skipped");
            return null;
        }
        if (header.Length < GvbsAddr.DiscoveryDataLen)
        {
            GevLog.Warn(LogSrc, $"{ifaceAddress}: truncated DISCOVERY_ACK from {from} ({header.Length} of {GvbsAddr.DiscoveryDataLen} bytes); skipped");
            return null;
        }
        try
        {
            var info = GevDeviceInfo.ParseDiscoveryAck(buffer.AsSpan(GvcpConst.HeaderSize, header.Length), ifaceAddress);
            if (GevLog.IsEnabled(GevLogLevel.Debug))
                GevLog.Debug(LogSrc, $"{ifaceAddress}: reply from {from}: {info}");
            return info;
        }
        catch (GevException ex)
        {
            GevLog.Warn(LogSrc, $"{ifaceAddress}: DISCOVERY_ACK from {from} rejected: {ex.Message}");
            return null;
        }
    }

    /// <summary>MAC 으로 중복을 없앤다. 같은 장치가 여러 인터페이스에서 보이면 장치 서브넷을 공유하는 인터페이스의 응답을 남긴다. 첫 등장 순서를 유지한다.</summary>
    internal static IReadOnlyList<GevDeviceInfo> Dedupe(IEnumerable<GevDeviceInfo> replies)
    {
        var order = new List<PhysicalAddress>();
        var byMac = new Dictionary<PhysicalAddress, GevDeviceInfo>();
        foreach (var d in replies)
        {
            if (!byMac.TryGetValue(d.Mac, out var existing))
            {
                byMac[d.Mac] = d;
                order.Add(d.Mac);
                continue;
            }
            if (!existing.IsReachableDirectly && d.IsReachableDirectly)
                byMac[d.Mac] = d;
        }
        var result = new List<GevDeviceInfo>(order.Count);
        foreach (var mac in order) result.Add(byMac[mac]);
        return result;
    }

    // ------------------------------------------------------------------ probe

    /// <summary>주소 하나에 유니캐스트 DISCOVERY_CMD 를 보낸다. 서브넷을 넘어서도, 루프백 시뮬레이터에도 통한다. 응답이 없으면 null.</summary>
    public static Task<GevDeviceInfo?> ProbeAsync(IPAddress address, int timeoutMs = 1000, CancellationToken ct = default)
    {
        if (address is null) throw new ArgumentNullException(nameof(address));
        return ProbeAsync(new IPEndPoint(address, GvcpConst.Port), timeoutMs, ct);
    }

    /// <summary>포트를 지정한 프로브 — 표준 포트가 아닌 시뮬레이터용.</summary>
    internal static async Task<GevDeviceInfo?> ProbeAsync(IPEndPoint endpoint, int timeoutMs, CancellationToken ct)
    {
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        using var channel = new GvcpChannel(endpoint, null, new GvcpChannelOpt { TimeoutMs = timeoutMs, Retries = 0 });
        GvcpAck ack;
        try
        {
            ack = await channel.RequestAsync(GvcpCmd.Discovery(allowBroadcastAck: false), ct).ConfigureAwait(false);
        }
        catch (GevTimeoutException)
        {
            GevLog.Debug(LogSrc, $"probe {endpoint}: no reply within {timeoutMs} ms");
            return null;
        }
        catch (GevStatusException ex)
        {
            GevLog.Warn(LogSrc, $"probe {endpoint}: {ex.Message}");
            return null;
        }

        if (ack.PayloadLength < GvbsAddr.DiscoveryDataLen)
        {
            GevLog.Warn(LogSrc, $"probe {endpoint}: truncated DISCOVERY_ACK ({ack.PayloadLength} of {GvbsAddr.DiscoveryDataLen} bytes); ignored");
            return null;
        }
        var local = GevNet.ResolveLocalAddress(endpoint.Address);
        return GevDeviceInfo.ParseDiscoveryAck(ack.Payload.Span, local);
    }

    // ------------------------------------------------------------------ FORCEIP

    /// <summary>모든(또는 지정한) 인터페이스로 FORCEIP_CMD 를 브로드캐스트한다. 장치는 주소를 바꾸면서 응답하지 않는 경우가 많으므로 보내고 바로 돌아온다.</summary>
    public static Task ForceIpAsync(PhysicalAddress mac, IPAddress ip, IPAddress subnet, IPAddress gateway, GevDiscoveryOpt? opt = null, CancellationToken ct = default)
    {
        if (mac is null) throw new ArgumentNullException(nameof(mac));
        opt ??= new GevDiscoveryOpt();
        ct.ThrowIfCancellationRequested();

        var cmd = GvcpCmd.ForceIp(mac, ip, subnet, gateway, allowBroadcastAck: true);
        var packet = cmd.ToArray(GvcpChannel.NextReqId(ref s_reqIdCounter));
        var ifaces = SelectInterfaces(opt.Interfaces);
        if (ifaces.Count == 0)
            throw new GevException("no usable IPv4 interface to send FORCEIP");

        var sent = 0;
        foreach (var iface in ifaces)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.EnableBroadcast = true;
                socket.Bind(new IPEndPoint(iface.Address, 0));
                if (opt.LimitedBroadcast)
                    sent += SendForceIp(socket, packet, new IPEndPoint(IPAddress.Broadcast, opt.Port), iface);
                var directed = opt.DirectedBroadcast ? iface.DirectedBroadcast : null;
                if (directed is not null && !directed.Equals(IPAddress.Broadcast))
                    sent += SendForceIp(socket, packet, new IPEndPoint(directed, opt.Port), iface);
                if (opt.UnicastTargets is not null)
                {
                    foreach (var target in opt.UnicastTargets)
                        sent += SendForceIp(socket, packet, target, iface);
                }
            }
            catch (SocketException ex)
            {
                GevLog.Warn(LogSrc, $"{iface}: FORCEIP socket failed ({ex.SocketErrorCode})", ex);
            }
        }
        if (sent == 0)
            throw new GevException("FORCEIP could not be sent on any interface");
        GevLog.Info(LogSrc, $"FORCEIP {mac} -> {ip}/{subnet} gw {gateway} sent {sent} time(s) on {ifaces.Count} interface(s)");
        return Task.CompletedTask;
    }

    private static int SendForceIp(Socket socket, byte[] packet, IPEndPoint target, GevNet.IfInfo iface)
    {
        try
        {
            socket.SendTo(packet, 0, packet.Length, SocketFlags.None, target);
            return 1;
        }
        catch (SocketException ex)
        {
            GevLog.Warn(LogSrc, $"{iface}: FORCEIP to {target} failed ({ex.SocketErrorCode})");
            return 0;
        }
    }

    // ------------------------------------------------------------------ interfaces

    /// <summary>null 이면 동작 중인 비루프백 IPv4 인터페이스 전부. 지정된 주소는 마스크를 찾아 붙이고, 모르는 주소는 마스크 없이 쓴다.</summary>
    private static List<GevNet.IfInfo> SelectInterfaces(IReadOnlyList<IPAddress>? explicitAddresses)
    {
        if (explicitAddresses is null)
            return GevNet.GetIpv4Interfaces(includeLoopback: false);

        var known = GevNet.GetIpv4Interfaces(includeLoopback: true);
        var list = new List<GevNet.IfInfo>(explicitAddresses.Count);
        foreach (var addr in explicitAddresses)
        {
            if (addr.AddressFamily != AddressFamily.InterNetwork)
                throw new GevException($"{addr} is not an IPv4 address");
            GevNet.IfInfo? match = null;
            foreach (var k in known)
            {
                if (k.Address.Equals(addr))
                {
                    match = k;
                    break;
                }
            }
            if (match is null)
            {
                GevLog.Debug(LogSrc, $"{addr} is not a known interface address; using it without a subnet mask");
                match = new GevNet.IfInfo { Name = addr.ToString(), Address = addr, Mask = null };
            }
            list.Add(match);
        }
        return list;
    }
}
