namespace GevSharp.Xml;

/// <summary>
/// 읽어 나간 바이트를 세어 상한을 넘는 순간 <see cref="GevException"/> 으로 끊는 읽기 전용 스트림.
/// ZIP 헤더가 선언한 압축 해제 크기는 장치가 준 값이라 믿을 수 없다 — 실제로 풀려 나오는 양을 센다.
/// 상한과 같은 양까지는 통과시키고, 그보다 한 바이트라도 더 나오면 끊는다.
/// </summary>
internal sealed class CappedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private readonly string _what;
    private long _readBytes;

    /// <param name="inner">원본 스트림. 이 스트림을 닫으면 함께 닫힌다.</param>
    /// <param name="maxBytes">허용 상한(바이트).</param>
    /// <param name="what">예외 메시지에 실을 대상 이름(예: ZIP 항목 이름).</param>
    public CappedReadStream(Stream inner, long maxBytes, string what)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxBytes = maxBytes;
        _what = what ?? throw new ArgumentNullException(nameof(what));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _readBytes;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Count(n);
        return n;
    }

    // 기능 기준 조건 — NETSTANDARD2_1_OR_GREATER 는 net8.0 자산에서 정의되지 않아, 한 심볼만 쓰면 이 경로가 통째로 빠진다.
#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
    /// <summary>span 을 그대로 내부 스트림에 넘기고 읽은 만큼 센다 — 이 경로가 없으면 기반 클래스가 풀 배열을 빌려 한 번 더 복사한다.</summary>
    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        Count(n);
        return n;
    }
#endif

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    private void Count(int n)
    {
        _readBytes += n;
        if (_readBytes > _maxBytes)
            throw new GevException($"{_what} inflates past the {_maxBytes} byte limit.");
    }
}
