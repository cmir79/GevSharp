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
    /// <summary>
    /// 창 안에서 DISCOVERY_CMD 를 보내는 총 횟수(첫 전송 포함, 대상마다). 늦게 켜진 장치·유실된 첫 패킷을 잡는다.
    /// 1 이상이어야 한다 — 1 미만이면 <see cref="GevDiscovery.DiscoverAsync"/> 가 <see cref="ArgumentOutOfRangeException"/> 을 던진다.
    /// <para>
    /// 전송은 창이 열린 시각부터 일정한 간격(창 길이 ÷ Repeat, 최대 200 ms, 최소 1 ms)으로 예약하고, 예약 시각이 창 밖으로 나가는
    /// 전송은 보내지 않는다 — 반복이 창을 늘리지 않는다. 그래서 Repeat 가 <see cref="TimeoutMs"/> 보다 크면 실제 전송은
    /// Repeat 번이 아니라 TimeoutMs 번(간격 1 ms)으로 줄어든다. 그 밖에는 Repeat 번을 모두 보낸다.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 모든(또는 지정한) 인터페이스에 DISCOVERY_CMD 를 브로드캐스트하고 창(<see cref="GevDiscoveryOpt.TimeoutMs"/>) 동안 응답을 모아
    /// MAC 으로 중복을 제거한다.
    /// </summary>
    /// <remarks>
    /// 빈 목록은 "창 동안 아무도 답하지 않았다" 만 뜻하지 않는다.
    /// <list type="bullet">
    /// <item>보낼 인터페이스가 없으면 창을 열지 않고 곧바로 빈 목록을 돌려준다 — <see cref="GevDiscoveryOpt.Interfaces"/> 가 빈 목록이거나,
    /// null 인데 루프백 말고 동작 중(Up)인 IPv4 인터페이스가 없거나(꺼진 어댑터, 케이블이 빠진 카메라 NIC 처럼 링크가 없는 어댑터는
    /// Up 이 아니다), 인터페이스 목록을 읽지 못한 경우다.</item>
    /// <item>인터페이스는 있었지만 어느 것에서도 DISCOVERY_CMD 가 나가지 못해도 빈 목록이다 — 소켓을 묶지 못했거나 보낼 대상이
    /// 없으면 곧바로, 전송이 전부 실패했으면 창이 끝난 뒤에 돌아온다.</item>
    /// </list>
    /// 어느 경우든 까닭을 <see cref="GevLog"/> 에 Warn 으로 남긴다. 빈 결과를 "장치 없음" 과 가려야 하는 호출자는 Warn 을 받는 싱크를 붙인다.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="GevDiscoveryOpt.TimeoutMs"/> 가 0 이하이거나 <see cref="GevDiscoveryOpt.Repeat"/> 가 1 미만.</exception>
    /// <exception cref="GevException"><see cref="GevDiscoveryOpt.Interfaces"/> 에 IPv4 가 아닌 주소가 있다.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> 가 취소됐다.</exception>
    public static async Task<IReadOnlyList<GevDeviceInfo>> DiscoverAsync(GevDiscoveryOpt? opt = null, CancellationToken ct = default)
    {
        opt ??= new GevDiscoveryOpt();
        if (opt.TimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(opt), "TimeoutMs must be positive");
        if (opt.Repeat < 1) throw new ArgumentOutOfRangeException(nameof(opt), "Repeat must be at least 1");
        var repeat = opt.Repeat;

        var ifaces = SelectInterfaces(opt.Interfaces, out var noIfaceReason);
        if (ifaces.Count == 0)
        {
            // 예외로 바꾸지 않고 빈 목록을 돌려준다 — 호출자(샘플·앱)는 빈 목록을 "찾은 장치 없음" 으로 다룬다. 대신 그 빈 목록이
            // 창을 기다린 결과가 아니라는 것과 까닭을 경고로 밝힌다.
            GevLog.Warn(LogSrc, $"discovery not sent: {noIfaceReason}; returning an empty list without waiting for the {opt.TimeoutMs} ms window");
            return Array.Empty<GevDeviceInfo>();
        }

        var reqId = GvcpChannel.NextReqId(ref s_reqIdCounter);
        var packet = GvcpCmd.Discovery(allowBroadcastAck: true).ToArray(reqId);

        var tasks = new Task<(List<GevDeviceInfo> Found, int Sent)>[ifaces.Count];
        for (var i = 0; i < ifaces.Count; i++)
            tasks[i] = DiscoverOnInterfaceAsync(ifaces[i], packet, opt, repeat, ct);
        var perInterface = await Task.WhenAll(tasks).ConfigureAwait(false);

        var all = new List<GevDeviceInfo>();
        var sentIfaces = 0;
        foreach (var (found, sent) in perInterface)
        {
            all.AddRange(found);
            if (sent > 0) sentIfaces++;
        }
        var result = Dedupe(all);
        if (sentIfaces == 0)
        {
            // 인터페이스마다 까닭(대상 없음·바인드 실패·전송 실패)은 이미 경고했다. 요약까지 "탐색을 마쳤다" 로 남기면
            // 빈 결과가 "아무도 답하지 않았다" 로 읽힌다.
            GevLog.Warn(LogSrc, $"no DISCOVERY_CMD was sent on any of {ifaces.Count} interface(s) (see the warnings above); the empty result does not mean that no device answered");
        }
        else
        {
            GevLog.Info(LogSrc, $"discovery finished: {result.Count} device(s) from {all.Count} reply(ies) on {sentIfaces} of {ifaces.Count} interface(s)");
        }
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

    /// <summary>한 인터페이스에서 탐색한다. Sent 는 실제로 나간 DISCOVERY_CMD 수 — 0 이면 이 인터페이스에서는 탐색이 일어나지 않았다(까닭은 경고로 남겼다).</summary>
    private static async Task<(List<GevDeviceInfo> Found, int Sent)> DiscoverOnInterfaceAsync(GevNet.IfInfo iface, byte[] packet, GevDiscoveryOpt opt, int repeat, CancellationToken ct)
    {
        var found = new List<GevDeviceInfo>();
        var sent = 0;
        var targets = BuildTargets(iface, opt);
        if (targets.Count == 0)
        {
            GevLog.Warn(LogSrc, $"{iface}: no discovery target (both broadcast modes disabled or mask unknown)");
            return (found, sent);
        }

        // 소켓은 만드는 순간 OS 핸들을 쥔다 — 바인드·옵션 설정이 실패해도 여기서 닫지 않으면 핸들이 GC 종료자가 돌 때까지 남아,
        // 탐색을 부를 때마다 실패한 인터페이스 수만큼 쌓인다.
        UdpClient? client = null;
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
            client?.Dispose();
            GevLog.Warn(LogSrc, $"{iface}: cannot bind a discovery socket ({ex.SocketErrorCode})", ex);
            return (found, sent);
        }
        catch
        {
            client?.Dispose();
            throw;
        }

        using (client)
        {
            var receiveTask = ReceiveDiscoveryRepliesAsync(client, iface.Address, found);
            var startMs = GevClock.NowMs();
            var windowEndMs = startMs + opt.TimeoutMs;
            // r 번째 전송은 창이 열린 시각 + r × 간격에 예약한다. 앞 대기가 타이머 눈금만큼 늦게 깨어도 뒤 예약이 밀려 쌓이지 않고,
            // 예약 시각이 창 밖인 전송은 보내지 않으므로 반복이 창을 늘리지 않는다. 간격은 1 ms 아래로 내리지 않아
            // Repeat 가 창 길이(ms)보다 크면 창 길이만큼만 보낸다. 그 밖에는 (Repeat-1) × 간격 < 창 이라 Repeat 번을 모두 보낸다.
            // 건너뛸지는 실제로 깬 시각이 아니라 예약 시각으로 가른다 — 굶주린 스케줄러가 늦게 깨웠다고 창 안에 예약된 전송을
            // 빠뜨리면 보내는 횟수가 부하에 따라 달라진다. 늦게 깬 몫은 곧바로 보내므로 창을 넘기는 것은 늦게 깬 한 번뿐이다.
            var intervalMs = Math.Min(RepeatIntervalMs, Math.Max(1, opt.TimeoutMs / repeat));
            var rounds = 0;
            try
            {
                for (var r = 0; r < repeat; r++)
                {
                    if (r > 0)
                    {
                        var dueMs = startMs + (long)r * intervalMs;
                        if (dueMs >= windowEndMs) break;
                        var waitMs = dueMs - GevClock.NowMs();
                        if (waitMs > 0)
                            await Task.Delay((int)waitMs, ct).ConfigureAwait(false);
                        else
                            ct.ThrowIfCancellationRequested();
                    }
                    foreach (var target in targets)
                    {
                        try
                        {
                            await client.SendAsync(packet, packet.Length, target).ConfigureAwait(false);
                            sent++;
                        }
                        catch (SocketException ex)
                        {
                            GevLog.Warn(LogSrc, $"{iface}: DISCOVERY_CMD to {target} failed ({ex.SocketErrorCode})");
                        }
                    }
                    rounds++;
                }
                if (rounds < repeat && GevLog.IsEnabled(GevLogLevel.Debug))
                    GevLog.Debug(LogSrc, $"{iface}: {rounds} of {repeat} DISCOVERY_CMD round(s) fit in the {opt.TimeoutMs} ms window at a {intervalMs} ms interval; the rest were not sent");
                // 시계 값이 어긋나도 창 길이를 넘겨 기다리지 않는다.
                var remainingMs = Math.Min(windowEndMs - GevClock.NowMs(), opt.TimeoutMs);
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
        return (found, sent);
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

    /// <summary>
    /// 주소 하나에 유니캐스트 DISCOVERY_CMD 를 한 번 보내고(재시도 없음) <paramref name="timeoutMs"/> 동안 응답을 기다린다.
    /// 서브넷을 넘어서도, 루프백 시뮬레이터에도 통한다.
    /// </summary>
    /// <returns>
    /// 온전한 DISCOVERY_ACK 가 오면 그 장치 정보. 아래 셋이면 null 이다 — null 은 "장치가 없다" 가 아니라 "쓸 수 있는 응답이 없었다" 다.
    /// <list type="number">
    /// <item>시간 안에 응답이 없다. 닫힌 포트, 기다리던 것이 아닌 명령의 ack(버린다), PENDING_ACK 로 답한 뒤 끝내 완료하지 않은 경우도
    /// 여기 든다. Debug 로그.</item>
    /// <item>장치가 오류 status 로 답했다 — 장치는 거기 있지만 탐색을 거절했다. Warn 로그에 status 를 남긴다.</item>
    /// <item>응답이 탐색 블록(248 바이트)보다 짧다. Warn 로그.</item>
    /// </list>
    /// 브로드캐스트 탐색(<see cref="DiscoverAsync"/>)도 같은 응답(오류 status·짧은 응답)을 목록에 넣지 않고 건너뛴다 — 프로브는
    /// 그 탐색을 주소 하나에 보내는 것이라 같은 응답을 같게 다룬다. 둘째·셋째를 "없음" 과 가려야 하는 호출자는 Warn 을 받는
    /// <see cref="GevLog.Sink"/> 를 붙인다.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> 가 null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutMs"/> 가 0 이하.</exception>
    /// <exception cref="GevException">IPv4 주소가 아니거나, 보내기·받기가 소켓 오류로 실패했거나, 장치로 나가는 로컬 주소를 정할 수 없다.</exception>
    /// <exception cref="SocketException">프로브용 소켓을 만들거나 묶지 못했다(드묾 — 이 경우만 감싸지 않고 그대로 나온다).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> 가 취소됐다.</exception>
    public static Task<GevDeviceInfo?> ProbeAsync(IPAddress address, int timeoutMs = 1000, CancellationToken ct = default)
    {
        if (address is null) throw new ArgumentNullException(nameof(address));
        return ProbeAsync(new IPEndPoint(address, GvcpConst.Port), timeoutMs, ct);
    }

    /// <summary>포트를 지정한 프로브 — 표준 포트가 아닌 시뮬레이터용. null 이 되는 경우와 예외는 공개 오버로드와 같다.</summary>
    internal static async Task<GevDeviceInfo?> ProbeAsync(IPEndPoint endpoint, int timeoutMs, CancellationToken ct)
    {
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        using var channel = new GvcpChannel(endpoint, null, new GvcpChannelOpt { TimeoutMs = timeoutMs, Retries = 0 });
        GvcpAck ack;
        try
        {
            ack = await channel.RequestAsync(GvcpCmd.Discovery(allowBroadcastAck: false), ct).ConfigureAwait(false);
        }
        catch (GevTimeoutException ex)
        {
            // 무응답·PENDING_ACK 뒤 미완료 — 채널의 메시지가 어느 쪽인지 말해 준다.
            GevLog.Debug(LogSrc, $"probe {endpoint}: {ex.Message}; returning null");
            return null;
        }
        catch (GevStatusException ex)
        {
            // 장치는 거기 있고 답도 했다 — null 이 "없음" 으로 읽히지 않게 경고로 남긴다.
            GevLog.Warn(LogSrc, $"probe {endpoint}: {ex.Message}; the device is there but refused discovery, returning null");
            return null;
        }

        if (ack.PayloadLength < GvbsAddr.DiscoveryDataLen)
        {
            GevLog.Warn(LogSrc, $"probe {endpoint}: truncated DISCOVERY_ACK ({ack.PayloadLength} of {GvbsAddr.DiscoveryDataLen} bytes); returning null");
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
        var ifaces = SelectInterfaces(opt.Interfaces, out var noIfaceReason);
        if (ifaces.Count == 0)
            throw new GevException($"no usable IPv4 interface to send FORCEIP: {noIfaceReason}");

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

    /// <summary>
    /// null 이면 동작 중인 비루프백 IPv4 인터페이스 전부. 지정된 주소는 마스크를 찾아 붙이고, 모르는 주소는 마스크 없이 쓴다.
    /// 지정한 주소는 하나마다 항목 하나가 되므로 목록이 비는 것은 지정 목록이 비었거나 자동 선택에서 고를 것이 없을 때뿐이다 —
    /// <paramref name="emptyReason"/> 에 그 까닭(로그용 영어 문장)을 담는다. 목록이 비지 않았으면 쓰지 않는다.
    /// </summary>
    private static List<GevNet.IfInfo> SelectInterfaces(IReadOnlyList<IPAddress>? explicitAddresses, out string emptyReason)
    {
        if (explicitAddresses is null)
        {
            var up = GevNet.GetIpv4Interfaces(includeLoopback: false, out var enumerated);
            emptyReason = enumerated
                ? "no network interface other than loopback is up with an IPv4 address (a disabled adapter, or one without link such as an unplugged camera NIC, is not up)"
                : "the host's network interfaces could not be enumerated";
            return up;
        }

        emptyReason = "GevDiscoveryOpt.Interfaces is an empty list";
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
