using System.Net;
using GevSharp.Gvcp;

namespace GevSharp.Cli.Commands;

/// <summary>
/// 명령의 &lt;ip&gt; 인자 — "192.168.1.10" 또는 "127.0.0.1:4000". 포트를 생략하면 표준 GVCP 포트(3956).
/// 표준 포트는 공개 OpenAsync(IPAddress) 로 열고, 다른 포트(시뮬레이터 등)는 공개 OpenAsync(IPEndPoint) 로 연다.
/// 프로브만은 포트를 받는 오버로드가 내부 멤버라 InternalsVisibleTo 로 쓴다.
/// </summary>
public sealed class DeviceTarget
{
    private DeviceTarget(IPAddress address, int port)
    {
        Address = address;
        Port = port;
        EndPoint = new IPEndPoint(address, port);
    }

    public IPAddress Address { get; }
    public int Port { get; }
    public IPEndPoint EndPoint { get; }
    public bool IsStandardPort => Port == GvcpConst.Port;

    /// <summary>"ip" 또는 "ip:port" 를 해석한다. IPv4 만 받는다 — 콜론이 IPv6 와 겹치기 때문이다.</summary>
    public static DeviceTarget Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new CliUsageException("device address is empty");
        var s = text.Trim();
        var port = GvcpConst.Port;
        var colon = s.IndexOf(':');
        if (colon >= 0)
        {
            var portText = s.Substring(colon + 1);
            if (!int.TryParse(portText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out port)
                || port < 1 || port > 65535)
                throw new CliUsageException($"invalid port '{portText}' in '{text}' (expected ip or ip:port, port 1..65535)");
            s = s.Substring(0, colon);
        }
        if (!IPAddress.TryParse(s, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new CliUsageException($"invalid IPv4 address '{s}' (expected ip or ip:port)");
        return new DeviceTarget(address, port);
    }

    public override string ToString() => IsStandardPort ? Address.ToString() : $"{Address}:{Port}";

    /// <summary>장치를 연다.</summary>
    public Task<GevDevice> OpenAsync(GevDeviceOpt opt, CancellationToken ct)
        => IsStandardPort
            ? GevDevice.OpenAsync(Address, opt, ct)
            : GevDevice.OpenAsync(EndPoint, opt, ct);

    /// <summary>
    /// 유니캐스트 DISCOVERY_CMD 한 번. 쓸 수 있는 응답이 없으면 null — 시간 안에 응답이 없을 때만이 아니라 장치가 오류 status 로 답했을 때,
    /// 응답이 탐색 블록보다 짧을 때도 null 이다(뒤의 둘은 라이브러리가 Warn 로그로 남긴다). 예외는 <see cref="GevDiscovery.ProbeAsync(IPAddress, int, CancellationToken)"/> 와 같다.
    /// </summary>
    public Task<GevDeviceInfo?> ProbeAsync(int timeoutMs, CancellationToken ct)
        => IsStandardPort
            ? GevDiscovery.ProbeAsync(Address, timeoutMs, ct)
            : GevDiscovery.ProbeAsync(EndPoint, timeoutMs, ct);
}
