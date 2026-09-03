namespace GevSharp.Sim;

/// <summary>
/// 스트림 채널 0 의 능력(SCC, 0x0D20)·구성(SCCFG, 0x0D24) 레지스터에서 시뮬레이터가 정의하는 비트. 값은 정수 기준(LSB = bit 0) 마스크다.
/// GenApi XML 의 BigEndian 비트 번호로는 bit 0 이 MSB 이므로, 마스크 0x2 는 XML 에서 Bit 30 이다.
/// </summary>
public static class SimStreamBits
{
    /// <summary>SCC: PACKETRESEND 명령을 처리한다.</summary>
    public const uint SccPacketResend = 0x0000_0001;
    /// <summary>SCC: 확장 ID 모드(64비트 블록 ID)를 지원한다.</summary>
    public const uint SccExtendedIds = 0x0000_0002;
    /// <summary>SCCFG: 확장 ID 모드 사용 — 다음 프레임부터 20바이트 GVSP 헤더로 보낸다.</summary>
    public const uint SccfgExtendedIds = 0x0000_0002;
}
