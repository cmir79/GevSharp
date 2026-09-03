using GevSharp.Gvcp;
using GevSharp.Gvsp;

namespace GevSharp.Tests.Gvsp;

/// <summary>
/// PACKETRESEND 출구의 테스트 대역. 요청을 기록하고, 모드에 따라 송신기에 다시 보내게 하거나(상태 0x0100), 0x800C 오류 패킷으로 답하거나,
/// 아무것도 하지 않는다. 수신 스레드에서 불리므로 여기서 하는 일은 소켓 송신뿐이다.
/// </summary>
internal sealed class TestResendPort : IGvcpResendPort
{
    public enum Mode
    {
        /// <summary>요청 범위를 그대로 다시 보낸다.</summary>
        Resend,
        /// <summary>요청 범위의 각 id 에 <see cref="UnavailableStatus"/> 오류 패킷으로 답한다.</summary>
        Unavailable,
        /// <summary>답하지 않는다.</summary>
        Never,
    }

    public readonly record struct Request(ulong BlockId, uint First, uint Last, bool ExtendedIds, int StreamChannel);

    private readonly object _lock = new();
    private readonly List<Request> _requests = new();
    private readonly GvspTestSender _sender;

    public TestResendPort(GvspTestSender sender)
    {
        _sender = sender;
    }

    public Mode Behaviour { get; set; } = Mode.Resend;

    /// <summary>못 주겠다는 답의 상태 코드. 기본은 0x800C(패킷 없음)이고, "조금 있다 다시 물어라"(0x8010·0x8014) 같은 답을 흉내 낼 때 바꾼다.</summary>
    public ushort UnavailableStatus { get; set; } = GvspConst.StatusPacketUnavailable;

    /// <summary><see cref="Mode.Resend"/> 에서도 이 id 들에는 <see cref="UnavailableStatus"/> 로 답한다 — 한 프레임 안에 메울 수 있는 구멍과 없는 구멍을 섞기 위해.</summary>
    public HashSet<uint> UnavailableIds { get; } = new();

    /// <summary>
    /// 요청에 답한 직후 부르는 갈고리 — "장치가 이 요청에 답한 다음" 에 이어질 일을 테스트가 꾸밀 때 쓴다(예: 프레임의 나머지 보내기).
    /// 수신 스레드에서 불리므로 무거운 일은 하지 않는다. 대신 테스트 스레드가 타이밍의 일부가 되지 않아 러너가 밀려도 순서가 유지된다.
    /// </summary>
    public Action<Request>? AfterRequest { get; set; }

    public Request[] Requests
    {
        get { lock (_lock) return _requests.ToArray(); }
    }

    public int RequestCount
    {
        get { lock (_lock) return _requests.Count; }
    }

    public void SendPacketResend(ulong blockId, uint firstPacketId, uint lastPacketId, bool extendedIds, int streamChannel)
    {
        var request = new Request(blockId, firstPacketId, lastPacketId, extendedIds, streamChannel);
        lock (_lock) _requests.Add(request);

        switch (Behaviour)
        {
            case Mode.Resend:
                if (UnavailableIds.Count == 0)
                {
                    _sender.Resend(blockId, firstPacketId, lastPacketId);
                    break;
                }
                for (var id = firstPacketId; id <= lastPacketId; id++)
                {
                    if (UnavailableIds.Contains(id)) _sender.SendError(blockId, id, UnavailableStatus);
                    else _sender.Resend(blockId, id, id);
                }
                break;
            case Mode.Unavailable:
                for (var id = firstPacketId; id <= lastPacketId; id++)
                {
                    _sender.SendError(blockId, id, UnavailableStatus);
                }
                break;
            case Mode.Never:
                break;
        }

        AfterRequest?.Invoke(request);
    }
}
