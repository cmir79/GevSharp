namespace GevSharp.Gvcp;

/// <summary>
/// GVCP 와이어 상수. 패킷은 전부 빅엔디언.
/// 헤더 8바이트: CMD = [type u8=0x42][flags u8][command u16][length u16][req_id u16],
///               ACK = [status u16][command u16][length u16][req_id u16].
/// </summary>
public static class GvcpConst
{
    /// <summary>장치가 GVCP 를 듣는 표준 UDP 포트.</summary>
    public const int Port = 3956;

    public const int HeaderSize = 8;

    /// <summary>READMEM/WRITEMEM 한 패킷의 최대 데이터 바이트(4의 배수).</summary>
    public const int MaxMemPayload = 512;

    /// <summary>READREG/WRITEREG 한 패킷에 실을 수 있는 최대 레지스터 수(페이로드 540 바이트 한도 기준).</summary>
    public const int MaxRegsPerPacket = 135;

    public const byte PacketTypeCmd = 0x42;
    public const byte PacketTypeAck = 0x00;
    public const byte PacketTypeError = 0x80;

    // ---- CMD flags (header byte 1) ----
    public const byte FlagAckRequired = 0x01;
    /// <summary>DISCOVERY_CMD: 장치가 브로드캐스트로 ACK 해도 된다.</summary>
    public const byte FlagAllowBroadcastAck = 0x10;
    /// <summary>PACKETRESEND_CMD: 64비트 블록 ID·32비트 패킷 ID 사용.</summary>
    public const byte FlagExtendedIds = 0x10;

    // ---- commands ----
    public const ushort DiscoveryCmd = 0x0002;
    public const ushort DiscoveryAck = 0x0003;
    public const ushort ForceIpCmd = 0x0004;
    public const ushort ForceIpAck = 0x0005;
    public const ushort PacketResendCmd = 0x0040;
    public const ushort PacketResendAck = 0x0041;
    public const ushort ReadRegCmd = 0x0080;
    public const ushort ReadRegAck = 0x0081;
    public const ushort WriteRegCmd = 0x0082;
    public const ushort WriteRegAck = 0x0083;
    public const ushort ReadMemCmd = 0x0084;
    public const ushort ReadMemAck = 0x0085;
    public const ushort WriteMemCmd = 0x0086;
    public const ushort WriteMemAck = 0x0087;
    public const ushort PendingAck = 0x0089;
    public const ushort EventCmd = 0x00C0;
    public const ushort EventAck = 0x00C1;
    public const ushort EventDataCmd = 0x00C2;
    public const ushort EventDataAck = 0x00C3;
    public const ushort ActionCmd = 0x0100;
    public const ushort ActionAck = 0x0101;

    // ---- status codes (ACK header, also GVSP packet status) ----
    public const ushort StatusSuccess = 0x0000;
    public const ushort StatusPacketResend = 0x0100;
    public const ushort StatusNotImplemented = 0x8001;
    public const ushort StatusInvalidParameter = 0x8002;
    public const ushort StatusInvalidAddress = 0x8003;
    public const ushort StatusWriteProtect = 0x8004;
    public const ushort StatusBadAlignment = 0x8005;
    public const ushort StatusAccessDenied = 0x8006;
    public const ushort StatusBusy = 0x8007;
    public const ushort StatusLocalProblem = 0x8008;
    public const ushort StatusMsgMismatch = 0x8009;
    public const ushort StatusInvalidProtocol = 0x800A;
    public const ushort StatusNoMsg = 0x800B;
    public const ushort StatusPacketUnavailable = 0x800C;
    public const ushort StatusDataOverrun = 0x800D;
    public const ushort StatusInvalidHeader = 0x800E;
    public const ushort StatusWrongConfig = 0x800F;
    public const ushort StatusPacketNotYetAvailable = 0x8010;
    public const ushort StatusPacketAndPrevRemovedFromMemory = 0x8011;
    public const ushort StatusPacketRemovedFromMemory = 0x8012;
    public const ushort StatusNoRefTime = 0x8013;
    public const ushort StatusPacketTemporarilyUnavailable = 0x8014;
    public const ushort StatusOverflow = 0x8015;
    public const ushort StatusActionLate = 0x8016;
    public const ushort StatusLeaderTrailerOverflow = 0x8017;
    public const ushort StatusError = 0x8FFF;

    public static bool IsError(ushort status) => (status & 0x8000) != 0;

    public static string StatusName(ushort status) => status switch
    {
        StatusSuccess => "SUCCESS",
        StatusPacketResend => "PACKET_RESEND",
        StatusNotImplemented => "NOT_IMPLEMENTED",
        StatusInvalidParameter => "INVALID_PARAMETER",
        StatusInvalidAddress => "INVALID_ADDRESS",
        StatusWriteProtect => "WRITE_PROTECT",
        StatusBadAlignment => "BAD_ALIGNMENT",
        StatusAccessDenied => "ACCESS_DENIED",
        StatusBusy => "BUSY",
        StatusLocalProblem => "LOCAL_PROBLEM",
        StatusMsgMismatch => "MSG_MISMATCH",
        StatusInvalidProtocol => "INVALID_PROTOCOL",
        StatusNoMsg => "NO_MSG",
        StatusPacketUnavailable => "PACKET_UNAVAILABLE",
        StatusDataOverrun => "DATA_OVERRUN",
        StatusInvalidHeader => "INVALID_HEADER",
        StatusWrongConfig => "WRONG_CONFIG",
        StatusPacketNotYetAvailable => "PACKET_NOT_YET_AVAILABLE",
        StatusPacketAndPrevRemovedFromMemory => "PACKET_AND_PREV_REMOVED_FROM_MEMORY",
        StatusPacketRemovedFromMemory => "PACKET_REMOVED_FROM_MEMORY",
        StatusNoRefTime => "NO_REF_TIME",
        StatusPacketTemporarilyUnavailable => "PACKET_TEMPORARILY_UNAVAILABLE",
        StatusOverflow => "OVERFLOW",
        StatusActionLate => "ACTION_LATE",
        StatusLeaderTrailerOverflow => "LEADER_TRAILER_OVERFLOW",
        StatusError => "ERROR",
        _ => "UNKNOWN",
    };
}
