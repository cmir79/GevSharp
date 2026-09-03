namespace GevSharp.Gvcp;

/// <summary>
/// GVSP 수신기가 리센드를 요청하는 통로. 장치는 제어 소켓에서 온 PACKETRESEND 만 받아 주므로 스트림이 자기 소켓으로 보내면 안 된다 —
/// 제어 채널(<see cref="GvcpChannel"/>)이 구현한다. 수신 스레드에서 불리므로 무할당·스레드 안전이어야 한다.
/// </summary>
internal interface IGvcpResendPort
{
    void SendPacketResend(ulong blockId, uint firstPacketId, uint lastPacketId, bool extendedIds, int streamChannel);
}
