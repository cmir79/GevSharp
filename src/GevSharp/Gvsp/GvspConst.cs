namespace GevSharp.Gvsp;

/// <summary>
/// GVSP 와이어 상수. 전부 빅엔디언.
/// 표준 헤더 8바이트: [status u16][block_id u16][packet_infos u32] —
///   packet_infos: bit31 EI(확장 ID 모드), [30:24] content type, [23:0] packet id.
/// 확장 헤더 20바이트: [status u16][flags u16][packet_infos u32][block_id u64][packet_id u32] — EI 비트는 packet_infos 에 있다.
/// 리더(image) 데이터 36바이트: [flags u16][payload_type u16][timestamp u64][pixel_format u32][size_x u32][size_y u32][offset_x u32][offset_y u32][padding_x u16][padding_y u16].
/// 트레일러 데이터 8바이트: [reserved u16][payload_type u16][size_y u32].
/// 페이로드 패킷: 헤더 뒤가 곧 이미지 바이트. 패킷 id 는 리더 0, 페이로드 1..N, 트레일러 N+1.
/// 페이로드 패킷 n 의 프레임 내 오프셋 = (n-1) × (SCPS − 28 − 헤더 길이). 마지막 페이로드만 짧을 수 있다.
/// </summary>
public static class GvspConst
{
    public const int HeaderSize = 8;
    public const int ExtendedHeaderSize = 20;
    /// <summary>IP(20)+UDP(8). SCPS 는 이 오버헤드를 포함한 IP 패킷 전체 크기다.</summary>
    public const int IpUdpOverhead = 28;

    public const uint ExtendedIdMask = 0x8000_0000;
    public const uint ContentTypeMask = 0x7F00_0000;
    public const int ContentTypeShift = 24;
    public const uint PacketIdMask = 0x00FF_FFFF;
    public const uint NumPartsMask = 0x0000_00FF;

    // ---- content types ([30:24] of packet_infos) ----
    public const byte ContentLeader = 0x01;
    public const byte ContentTrailer = 0x02;
    public const byte ContentPayload = 0x03;
    public const byte ContentAllIn = 0x04;
    public const byte ContentH264 = 0x05;
    public const byte ContentMultiZone = 0x06;
    public const byte ContentMultiPart = 0x07;
    public const byte ContentGenDc = 0x08;

    // ---- payload types (leader) — [13:0]; bit14 = chunk data appended ----
    public const ushort PayloadImage = 0x0001;
    public const ushort PayloadRawData = 0x0002;
    public const ushort PayloadFile = 0x0003;
    public const ushort PayloadChunkData = 0x0004;
    public const ushort PayloadExtendedChunkData = 0x0005;
    public const ushort PayloadJpeg = 0x0006;
    public const ushort PayloadJpeg2000 = 0x0007;
    public const ushort PayloadH264 = 0x0008;
    public const ushort PayloadMultiZoneImage = 0x0009;
    public const ushort PayloadMultiPart = 0x000A;
    public const ushort PayloadGenDc = 0x000B;
    public const ushort PayloadTypeMask = 0x3FFF;
    public const ushort PayloadChunkFlag = 0x4000;

    public const int ImageLeaderDataSize = 36;
    public const int TrailerDataSize = 8;

    /// <summary>패킷 status 는 GVCP 상태 코드와 같은 공간을 쓴다(0x0100 = 리센드된 패킷, 0x8xxx = 오류).</summary>
    public const ushort StatusSuccess = 0x0000;
    public const ushort StatusPacketResend = 0x0100;
    public const ushort StatusPacketUnavailable = 0x800C;

    public static bool IsError(ushort status) => (status & 0x8000) != 0;

    /// <summary>SCPS(IP 패킷 크기)에서 페이로드 패킷 한 개가 나르는 이미지 바이트 수.</summary>
    public static int DataBytesPerPacket(int scps, bool extendedIds)
        => scps - IpUdpOverhead - (extendedIds ? ExtendedHeaderSize : HeaderSize);
}
