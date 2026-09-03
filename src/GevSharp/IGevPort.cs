namespace GevSharp;

/// <summary>
/// 레지스터 공간 접근 경계 — GenApi 계층이 전송을 아는 유일한 지점. 구현체는 GVCP 장치이거나 테스트용 메모리 모델이다.
/// 주소는 GenApi 규약대로 64비트지만 GVCP 는 32비트 주소만 나른다 — 범위를 벗어나면 구현이 예외를 낸다.
/// 바이트 순서 해석은 호출자(GenApi 노드) 몫이며, 포트는 원시 바이트만 옮긴다.
/// </summary>
public interface IGevPort
{
    /// <summary>address 부터 buffer.Length 바이트를 읽는다. 구현은 GVCP 최대 페이로드(512) 단위로 나눠 읽는다.</summary>
    ValueTask ReadAsync(ulong address, Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>address 부터 data 를 쓴다. 구현은 GVCP 최대 페이로드 단위로 나눠 쓴다.</summary>
    ValueTask WriteAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct = default);
}
