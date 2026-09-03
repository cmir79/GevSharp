namespace GevSharp.GenApi.Runtime;

/// <summary>
/// 쓰기 그림자 — 노드맵이 포트에 마지막으로 쓴 바이트를 주소별로 기억한다. 쓰기 전용 레지스터는 장치에서 읽어 올 수 없으므로
/// 읽기-수정-쓰기(MaskedIntReg·StructReg 항목)의 바탕값을 여기서 가져온다: 같은 레지스터를 나눠 쓰는 다른 노드가 쓴 비트도
/// 그대로 남아, 한 필드를 쓸 때 형제 필드가 0 으로 지워지지 않는다. 한 번도 쓰지 않은 바이트는 0 이다.
/// <para>
/// 읽은 값은 넣지 않는다 — 같은 주소가 읽을 때는 상태, 쓸 때는 제어인 레지스터가 있어 읽은 바이트를 쓰기 바탕으로 삼으면 틀린다.
/// 무효화로도 지우지 않는다: 쓰기 전용 레지스터의 내용을 호스트가 알 길은 마지막 쓰기뿐이며, 지워 봐야 0 으로 돌아갈 뿐이다.
/// 장치가 스스로 바꾼 쓰기 전용 레지스터(예: 사용자 세트 불러오기 뒤)는 여기 반영되지 않는다.
/// </para>
/// </summary>
internal sealed class WriteShadow
{
    private readonly Dictionary<ulong, byte> _bytes = new();
    private readonly object _lock = new();

    /// <summary>[address, address+data.Length) 에 쓴 바이트를 기억한다.</summary>
    public void Store(ulong address, byte[] data)
    {
        lock (_lock)
        {
            for (var i = 0; i < data.Length; i++) _bytes[address + (ulong)i] = data[i];
        }
    }

    /// <summary>[address, address+dst.Length) 의 마지막 쓴 바이트를 dst 에 채운다(모르는 바이트는 0). 하나라도 알면 true.</summary>
    public bool Fill(ulong address, byte[] dst)
    {
        var any = false;
        lock (_lock)
        {
            for (var i = 0; i < dst.Length; i++)
            {
                if (_bytes.TryGetValue(address + (ulong)i, out var b))
                {
                    dst[i] = b;
                    any = true;
                }
                else
                {
                    dst[i] = 0;
                }
            }
        }
        return any;
    }
}
