using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using GevSharp.Gvcp;

namespace GevSharp.Cli.Commands;

/// <summary>주소·비트 필드를 사람이 읽는 문자열로. 부트스트랩 레지스터의 의미는 <see cref="GvbsAddr"/> 상수를 따른다.</summary>
public static class NetText
{
    public static string Mac(PhysicalAddress mac)
    {
        var bytes = mac.GetAddressBytes();
        var sb = new StringBuilder(bytes.Length * 3);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public static IPAddress Ipv4(uint value)
        => new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    /// <summary>IP 설정 비트(GVBS 0x0010/0x0014).</summary>
    public static string IpCfg(uint value)
    {
        var names = new List<string>(3);
        if ((value & GvbsAddr.IpCfgPersistent) != 0) names.Add("persistent");
        if ((value & GvbsAddr.IpCfgDhcp) != 0) names.Add("DHCP");
        if ((value & GvbsAddr.IpCfgLla) != 0) names.Add("LLA");
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static readonly (uint Mask, string Name)[] GvcpCapNames =
    {
        (GvbsAddr.GvcpCapConcatenation, "concatenation"),
        (GvbsAddr.GvcpCapWriteMem, "write-mem"),
        (GvbsAddr.GvcpCapPacketResend, "packet-resend"),
        (GvbsAddr.GvcpCapEvent, "event"),
        (GvbsAddr.GvcpCapEventData, "event-data"),
        (GvbsAddr.GvcpCapPendingAck, "pending-ack"),
        (GvbsAddr.GvcpCapAction, "action"),
        (GvbsAddr.GvcpCapPrimaryAppSwitchover, "primary-app-switchover"),
        (GvbsAddr.GvcpCapExtendedStatusCodes, "extended-status-codes"),
        (GvbsAddr.GvcpCapDiscoveryAckDelayWritable, "discovery-ack-delay-writable"),
        (GvbsAddr.GvcpCapDiscoveryAckDelay, "discovery-ack-delay"),
        (GvbsAddr.GvcpCapTestData, "test-data"),
        (GvbsAddr.GvcpCapManifestTable, "manifest-table"),
        (GvbsAddr.GvcpCapCcpAppSocket, "ccp-app-socket"),
        (GvbsAddr.GvcpCapLinkSpeed, "link-speed"),
        (GvbsAddr.GvcpCapHeartbeatDisable, "heartbeat-disable"),
        (GvbsAddr.GvcpCapSerialNumber, "serial-number"),
        (GvbsAddr.GvcpCapNameRegister, "name-register"),
    };

    /// <summary>GVCP 능력 비트(GVBS 0x0934) — 아는 비트는 이름으로, 모르는 비트는 번호로.</summary>
    public static string GvcpCap(uint value)
    {
        var names = new List<string>();
        var known = 0u;
        foreach (var (mask, name) in GvcpCapNames)
        {
            known |= mask;
            if ((value & mask) != 0) names.Add(name);
        }
        for (var bit = 0; bit < 32; bit++)
        {
            var mask = 1u << bit;
            if ((value & mask) != 0 && (known & mask) == 0) names.Add($"bit{bit}");
        }
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    /// <summary>CCP 레지스터(GVBS 0x0A00) 값.</summary>
    public static string Ccp(uint value)
    {
        if (value == 0) return "open";
        var names = new List<string>(3);
        if ((value & GvbsAddr.CcpExclusive) != 0) names.Add("exclusive");
        if ((value & GvbsAddr.CcpControl) != 0) names.Add("control");
        if ((value & GvbsAddr.CcpSwitchoverEnable) != 0) names.Add("switchover-enable");
        if (names.Count == 0) names.Add("unknown bits");
        return string.Join(", ", names);
    }

    /// <summary>SCPS 레지스터 — 크기와 플래그.</summary>
    public static string Scps(uint value)
    {
        var flags = new List<string>(3);
        if ((value & GvbsAddr.ScpsFireTest) != 0) flags.Add("fire-test");
        if ((value & GvbsAddr.ScpsDoNotFragment) != 0) flags.Add("do-not-fragment");
        if ((value & GvbsAddr.ScpsBigEndian) != 0) flags.Add("big-endian");
        return $"{value & GvbsAddr.ScpsSizeMask} bytes (flags: {(flags.Count == 0 ? "none" : string.Join(", ", flags))})";
    }

    public static string CharacterSet(int code) => code switch
    {
        GevDeviceInfo.CharacterSetUtf8 => "UTF-8",
        GevDeviceInfo.CharacterSetAscii => "ASCII",
        0 => "unspecified",
        _ => $"code {code}",
    };

    /// <summary>빈 문자열을 눈에 띄게.</summary>
    public static string Text(string? s) => string.IsNullOrEmpty(s) ? "(empty)" : s!;
}
