namespace GevSharp;

/// <summary>스트림 패킷 크기(SCPS) 결정 방식.</summary>
public enum PacketSizeMode
{
    /// <summary>인터페이스 MTU 에서 시작해 파이어테스트로 장치·경로가 통과시키는 최대 크기를 찾는다.</summary>
    Auto,

    /// <summary><see cref="GevStreamOpt.PacketSize"/> 값을 그대로 쓴다.</summary>
    Fixed,
}
