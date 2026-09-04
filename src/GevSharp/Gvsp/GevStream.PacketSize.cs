using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GevSharp.Gvcp;
using GevSharp.Gvsp;

namespace GevSharp;

/// <summary>
/// 패킷 크기(SCPS) 협상. 후보 크기를 파이어테스트 비트와 함께 SCPS 에 쓰면 장치가 그 크기의 테스트 패킷을 SCDA:SCP 로 한 개 보낸다.
/// 100 ms 안에 도착하면 그 크기는 경로 전체(장치·스위치·NIC)를 통과한 것이다.
/// 테스트 패킷은 반드시 단편화 금지로 보내게 한다 — 단편화가 허용되면 경로 어딘가의 작은 MTU 를 장치 쪽 IP 계층이 쪼개고 호스트가 다시 붙여
/// "통과" 로 보이므로, 검사가 잡아야 할 바로 그 경우(호스트 점보 프레임·중간 스위치 1500)를 놓친다.
/// 순서: MTU 그대로 → 1500 → 이분 탐색(통과 크기와 실패 크기 사이). MTU 를 모르면 9000 에서 시작하고, 아무것도 확인되지 않으면 1500.
/// </summary>
public sealed partial class GevStream
{
    private const int FireTestTimeoutMs = 100;
    private const int DefaultPacketSize = 1500;
    /// <summary>인터페이스 MTU 를 알 수 없을 때의 첫 후보 — 점보 프레임 경로를 1500 으로 묶어 버리지 않기 위해.</summary>
    private const int UnknownMtuSeed = 9000;
    private const int NegotiationGranularity = 16;
    private const int DrainLimit = 1024;

    private async Task<int> NegotiatePacketSizeAsync(Socket socket, CancellationToken ct)
    {
        var mtu = ResolveMtu();
        var probe = new byte[MaxPacketSize + ScratchSlackBytes];
        GevLog.Info(_logSrc, $"Negotiating packet size from interface MTU {mtu}.");

        if (await ProbeAsync(socket, probe, mtu, ct).ConfigureAwait(false))
        {
            return FinishNegotiation(mtu, mtu);
        }

        int lo;
        int hi;
        if (mtu > DefaultPacketSize && await ProbeAsync(socket, probe, DefaultPacketSize, ct).ConfigureAwait(false))
        {
            lo = DefaultPacketSize;
            hi = mtu;
        }
        else if (mtu > MinPacketSize && await ProbeAsync(socket, probe, MinPacketSize, ct).ConfigureAwait(false))
        {
            lo = MinPacketSize;
            hi = Math.Min(mtu, DefaultPacketSize);
        }
        else
        {
            GevLog.Warn(_logSrc, $"No test packet arrived for any probed packet size; assuming {DefaultPacketSize}. Check that the device can reach {_localAddress}:{LocalPort}.");
            return FinishNegotiation(DefaultPacketSize, mtu);
        }

        // lo 는 통과, hi 는 실패 — 간격이 충분히 좁아질 때까지 가운데를 찔러 본다.
        while (hi - lo > NegotiationGranularity)
        {
            var mid = AlignDown((lo + hi) / 2);
            if (mid <= lo || mid >= hi) break;
            if (await ProbeAsync(socket, probe, mid, ct).ConfigureAwait(false)) lo = mid;
            else hi = mid;
        }

        return FinishNegotiation(lo, mtu);
    }

    private int FinishNegotiation(int size, int mtu)
    {
        GevLog.Info(_logSrc, $"Negotiated packet size {size} (interface MTU {mtu}).");
        return size;
    }

    private async Task<bool> ProbeAsync(Socket socket, byte[] probe, int size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DrainPending(socket, probe, _logSrc);
        // 단편화 금지가 없으면 경로보다 큰 후보도 쪼개져 도착해 통과로 오인된다. 장치 플래그(빅엔디언 등)는 그대로 둔다.
        var scps = GvbsAddr.ScpsFireTest | GvbsAddr.ScpsDoNotFragment | _scpsFlags | (uint)size;
        await WriteRegAsync(GvbsAddr.ScpsOffset, scps, ct).ConfigureAwait(false);

        var expectedBytes = size - GvspConst.IpUdpOverhead;
        var ok = await Task.Run(() => WaitForTestPacket(socket, probe, expectedBytes, FireTestTimeoutMs, _logSrc), ct).ConfigureAwait(false);
        if (GevLog.IsEnabled(GevLogLevel.Debug))
        {
            GevLog.Debug(_logSrc, $"Packet size probe {size}: {(ok ? "test packet received" : "no test packet")}.");
        }
        return ok;
    }

    /// <summary>
    /// 마감까지 테스트 패킷을 기다린다. 후보 크기(IP 헤더 제외)보다 작은 데이터그램은 이전 후보의 늦은 패킷이므로 흘리고 계속 기다린다.
    /// </summary>
    private static bool WaitForTestPacket(Socket socket, byte[] probe, int expectedBytes, int timeoutMs, string logSrc)
    {
        var frequency = Stopwatch.Frequency;
        var deadline = Stopwatch.GetTimestamp() + timeoutMs * frequency / 1000;
        while (true)
        {
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return false;
            var waitMicros = (int)Math.Min(int.MaxValue / 2, remainingTicks * 1_000_000 / frequency) + 1;

            bool ready;
            try
            {
                ready = socket.Poll(waitMicros, SelectMode.SelectRead);
            }
            catch (ObjectDisposedException)
            {
                GevLog.Debug(logSrc, $"Fire test expecting {expectedBytes} bytes: socket closed while waiting for the test packet.");
                return false;
            }
            if (!ready) return false;

            int received;
            try
            {
                received = socket.Receive(probe, 0, probe.Length, SocketFlags.None);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
            {
                // 우리 버퍼보다 큰 데이터그램이 왔다 — 크기 검사에는 "적어도 이만큼" 으로 충분하다.
                GevLog.Debug(logSrc, $"Fire test expecting {expectedBytes} bytes: a datagram larger than the {probe.Length}-byte probe buffer arrived; counted as big enough.");
                received = probe.Length + 1;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                GevLog.Debug(logSrc, $"Fire test expecting {expectedBytes} bytes: ICMP port-unreachable echoed back; ignoring and waiting again.");
                continue;
            }
            catch (ObjectDisposedException)
            {
                GevLog.Debug(logSrc, $"Fire test expecting {expectedBytes} bytes: socket closed while receiving the test packet.");
                return false;
            }

            if (received >= expectedBytes) return true;
        }
    }

    /// <summary>소켓에 쌓인 데이터그램을 비운다 — 이전 후보의 테스트 패킷이 다음 후보의 답으로 오인되지 않게.</summary>
    private static void DrainPending(Socket socket, byte[] probe, string logSrc)
    {
        for (var i = 0; i < DrainLimit; i++)
        {
            try
            {
                if (!socket.Poll(0, SelectMode.SelectRead)) return;
                socket.Receive(probe, 0, probe.Length, SocketFlags.None);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize || ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // 크기 초과·ICMP 되돌림은 버리고 계속
                GevLog.Debug(logSrc, $"Draining the probe socket: {ex.SocketErrorCode} ignored.");
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private int ResolveMtu()
    {
        int mtu;
        try
        {
            mtu = (MtuResolver ?? InterfaceMtu)(_localAddress);
        }
        catch (Exception ex)
        {
            GevLog.Warn(_logSrc, $"Could not read the MTU of the interface owning {_localAddress}; probing from {UnknownMtuSeed}.", ex);
            mtu = UnknownMtuSeed;
        }

        if (mtu <= 0)
        {
            GevLog.Debug(_logSrc, $"Interface MTU for {_localAddress} unknown; probing from {UnknownMtuSeed}.");
            mtu = UnknownMtuSeed;
        }
        if (mtu > MaxPacketSize) mtu = MaxPacketSize;
        if (mtu < MinPacketSize) mtu = MinPacketSize;
        return AlignDown(mtu);
    }

    /// <summary>localAddress 를 가진 인터페이스의 IPv4 MTU. 못 찾으면 0.</summary>
    internal static int InterfaceMtu(IPAddress localAddress)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch (NetworkInformationException ex)
            {
                GevLog.Debug(LogSrc, $"Interface '{ni.Name}': IP properties unavailable ({ex.Message}); skipped while looking for the MTU.");
                continue;
            }

            var owns = false;
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.Equals(localAddress))
                {
                    owns = true;
                    break;
                }
            }
            if (!owns) continue;

            try { return props.GetIPv4Properties().Mtu; }
            catch (NetworkInformationException ex)
            {
                GevLog.Debug(LogSrc, $"Interface '{ni.Name}' owns {localAddress} but reports no IPv4 MTU ({ex.Message}); negotiating without it.");
                return 0;
            }
        }
        return 0;
    }

    /// <summary>SCPS 후보를 4 의 배수로 내림한다 — 워드 정렬을 요구하는 장치가 있다.</summary>
    private static int AlignDown(int size) => size & ~3;
}
