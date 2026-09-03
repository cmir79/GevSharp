using System.Net;

namespace GevSharp.Sim;

/// <summary>수신한 PACKETRESEND 요청 기록. <see cref="IsAccepted"/> 가 false 면 제어권이 없는 송신자이거나 다른 채널이라 무시된 요청이다.</summary>
public readonly record struct SimResendRequest(ulong BlockId, uint FirstPacketId, uint LastPacketId, IPEndPoint Sender, bool IsAccepted);
