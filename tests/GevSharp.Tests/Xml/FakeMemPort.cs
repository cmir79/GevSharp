using System.Text;
using GevSharp.Gvcp;

namespace GevSharp.Tests.Xml;

/// <summary>
/// 부트스트랩 블록(0x0000~0x0FFF)과 임의 메모리 영역을 가진 인메모리 포트.
/// 어느 영역에도 완전히 들어가지 않는 접근은 실장치처럼 INVALID_ADDRESS 상태 예외로 실패한다.
/// 모든 읽기를 (주소, 길이) 로 기록해 청크 분할·캐시 적중을 검증할 수 있게 한다.
/// </summary>
internal sealed class FakeMemPort : IGevPort
{
    private readonly List<(ulong Start, byte[] Data)> _regions = new();
    private readonly byte[] _bootstrap = new byte[0x1000];

    public List<(ulong Addr, int Len)> Reads { get; } = new();

    /// <summary>읽기마다 (주소, 길이) 로 호출되는 훅 — 특정 읽기 시점에 토큰을 취소하는 식으로 전송 도중의 취소를 흉내 낸다.</summary>
    public Action<ulong, int>? OnRead { get; set; }

    public FakeMemPort(string manufacturer = "Acme", string model = "Cam", string deviceVersion = "1.0")
    {
        _regions.Add((0, _bootstrap));
        SetString(GvbsAddr.ManufacturerName, GvbsAddr.ManufacturerNameLen, manufacturer);
        SetString(GvbsAddr.ModelName, GvbsAddr.ModelNameLen, model);
        SetString(GvbsAddr.DeviceVersion, GvbsAddr.DeviceVersionLen, deviceVersion);
    }

    /// <summary>고정 길이 문자열 필드를 NUL 종료로 채운다. 필드보다 길면 테스트 오류.</summary>
    public void SetString(uint addr, int fieldLen, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length >= fieldLen) throw new ArgumentException($"'{text}' does not fit in {fieldLen} bytes.", nameof(text));
        Array.Clear(_bootstrap, (int)addr, fieldLen);
        Buffer.BlockCopy(bytes, 0, _bootstrap, (int)addr, bytes.Length);
    }

    public void SetFirstUrl(string url) => SetString(GvbsAddr.FirstUrl, GvbsAddr.UrlLen, url);

    public void SetSecondUrl(string url) => SetString(GvbsAddr.SecondUrl, GvbsAddr.UrlLen, url);

    public void AddRegion(ulong addr, byte[] data) => _regions.Add((addr, data));

    public int ReadCountAtOrAbove(ulong addr) => Reads.Count(r => r.Addr >= addr);

    public ValueTask ReadAsync(ulong address, Memory<byte> buffer, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return new ValueTask(Task.FromCanceled(ct));
        Reads.Add((address, buffer.Length));
        OnRead?.Invoke(address, buffer.Length);

        foreach (var (start, data) in _regions)
        {
            if (address >= start && address + (ulong)buffer.Length <= start + (ulong)data.Length)
            {
                data.AsMemory((int)(address - start), buffer.Length).CopyTo(buffer);
                return default;
            }
        }

        return new ValueTask(Task.FromException(new GevStatusException("READMEM", GvcpConst.StatusInvalidAddress)));
    }

    public ValueTask WriteAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return new ValueTask(Task.FromCanceled(ct));

        foreach (var (start, region) in _regions)
        {
            if (address >= start && address + (ulong)data.Length <= start + (ulong)region.Length)
            {
                data.CopyTo(region.AsMemory((int)(address - start), data.Length));
                return default;
            }
        }

        return new ValueTask(Task.FromException(new GevStatusException("WRITEMEM", GvcpConst.StatusInvalidAddress)));
    }
}
