using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace GevSharp.Gvcp;

/// <summary>
/// 호스트 네트워크 조회 — IPv4 인터페이스 열거, 장치까지 나가는 로컬 주소 결정, 서브넷·브로드캐스트 계산, UDP 소켓 공통 설정.
/// </summary>
internal static class GevNet
{
    private const string LogSrc = "GevNet";

    /// <summary>Windows 전용 SIO_UDP_CONNRESET — ICMP port-unreachable 이 다음 수신을 10054 로 깨뜨리지 않게 끈다.</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    internal sealed class IfInfo
    {
        public required string Name { get; init; }
        public required IPAddress Address { get; init; }
        /// <summary>null 이면 마스크를 알 수 없는 인터페이스 — 지향 브로드캐스트·서브넷 판정을 건너뛴다.</summary>
        public IPAddress? Mask { get; init; }
        public bool IsLoopback { get; init; }

        public IPAddress? DirectedBroadcast => Mask is null ? null : GevNet.DirectedBroadcast(Address, Mask);

        public override string ToString() => Mask is null ? $"{Name} {Address}" : $"{Name} {Address}/{Mask}";
    }

    /// <summary>동작 중(Up)인 인터페이스의 IPv4 유니캐스트 주소를 전부 모은다. 조회 실패는 빈 목록 + 경고.</summary>
    internal static List<IfInfo> GetIpv4Interfaces(bool includeLoopback)
    {
        var list = new List<IfInfo>();
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex)
        {
            GevLog.Warn(LogSrc, "failed to enumerate network interfaces", ex);
            return list;
        }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            var isLoopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            if (isLoopback && !includeLoopback) continue;

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (Exception ex)
            {
                GevLog.Debug(LogSrc, $"skipping interface {nic.Name}: {ex.Message}");
                continue;
            }

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                IPAddress? mask = null;
                try
                {
                    mask = ua.IPv4Mask;
                }
                catch (Exception ex)
                {
                    // 일부 플랫폼은 마스크 조회를 지원하지 않는다 — 브로드캐스트만 못 쓰고 나머지는 동작한다.
                    GevLog.Debug(LogSrc, $"no IPv4 mask for {nic.Name} {ua.Address}: {ex.Message}");
                }
                if (mask is not null && Ipv4ToUInt32(mask) == 0) mask = null;
                list.Add(new IfInfo { Name = nic.Name, Address = ua.Address, Mask = mask, IsLoopback = isLoopback });
            }
        }
        return list;
    }

    /// <summary>target 과 같은 서브넷에 있는 인터페이스 주소. 없으면 null.</summary>
    internal static IPAddress? FindInterfaceForAddress(IPAddress target)
    {
        foreach (var i in GetIpv4Interfaces(includeLoopback: true))
        {
            if (i.Mask is not null && IsSameSubnet(target, i.Address, i.Mask))
                return i.Address;
        }
        return null;
    }

    /// <summary>
    /// device 로 나가는 로컬 주소. 같은 서브넷의 인터페이스가 있으면 그것, 없으면 임시 UDP 소켓을 연결해 OS 라우팅이 고른 주소를 읽는다.
    /// </summary>
    internal static IPAddress ResolveLocalAddress(IPAddress device)
    {
        if (device is null) throw new ArgumentNullException(nameof(device));
        if (device.AddressFamily != AddressFamily.InterNetwork)
            throw new GevException($"{device} is not an IPv4 address; GVCP runs over IPv4 only");

        var viaSubnet = FindInterfaceForAddress(device);
        if (viaSubnet is not null) return viaSubnet;

        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(device, GvcpConst.Port));
            var local = ((IPEndPoint)probe.LocalEndPoint!).Address;
            GevLog.Debug(LogSrc, $"route lookup: {device} is reached from {local}");
            return local;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            throw new GevException($"cannot determine a local address that reaches {device}", ex);
        }
    }

    internal static IPAddress DirectedBroadcast(IPAddress address, IPAddress mask)
        => Ipv4FromUInt32(Ipv4ToUInt32(address) | ~Ipv4ToUInt32(mask));

    internal static bool IsSameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var m = Ipv4ToUInt32(mask);
        if (m == 0) return false;
        return (Ipv4ToUInt32(a) & m) == (Ipv4ToUInt32(b) & m);
    }

    internal static uint Ipv4ToUInt32(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    internal static IPAddress Ipv4FromUInt32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return new IPAddress(b);
    }

    /// <summary>Windows 에서 ICMP unreachable 이 수신 호출을 오류로 깨우는 동작을 끈다. 다른 OS 에서는 아무것도 하지 않는다.</summary>
    internal static void DisableIcmpReset(Socket socket)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch (Exception ex)
        {
            // 실패해도 수신 루프가 ConnectionReset 을 걸러내므로 동작에는 지장이 없다.
            GevLog.Debug(LogSrc, $"SIO_UDP_CONNRESET not applied: {ex.Message}");
        }
    }
}
