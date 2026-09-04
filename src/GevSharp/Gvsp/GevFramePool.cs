namespace GevSharp.Gvsp;

/// <summary>
/// 풀 버퍼 하나. <see cref="Version"/> 은 대여·반납마다 오르며, <see cref="GevFrame"/> 은 대여 시점 값을 들고 있다가 비교해
/// 반납 뒤의 접근과 뒤늦은 이중 반납을 걸러 낸다.
/// </summary>
internal sealed class FrameBuf
{
    public FrameBuf(int index, int bytes)
    {
        Index = index;
        Data = bytes > 0 ? new byte[bytes] : Array.Empty<byte>();
    }

    public readonly int Index;
    public byte[] Data;
    public int Version;
    public bool IsFree = true;
}

/// <summary>
/// 고정 개수의 프레임 버퍼 풀. 수신 스레드가 빌리고 소비자(임의 스레드)가 <see cref="GevFrame.Dispose"/> 로 돌려준다.
/// 버퍼 크기는 요구된 최대 프레임 크기를 따라 자란다 — 자람은 빈 버퍼를 빌려 줄 때 그 버퍼만 늦게 재할당한다.
/// 빈 버퍼가 없으면 즉시 false — 수신 스레드는 절대 기다리지 않고, 소비자가 들고 있는 버퍼에는 절대 쓰지 않는다.
/// </summary>
internal sealed class GevFramePool
{
    private const string LogSrc = "GevFramePool";

    /// <summary>
    /// 이 풀이 로그에 쓰는 이름. 풀은 스트림마다 하나이므로 어느 장치의 버퍼가 커졌는지 밝혀야 한다 —
    /// 버퍼가 예상보다 커졌다는 것은 메모리 그림에 바로 걸리는 사실인데, 대수가 늘면 같은 문장이
    /// 여러 줄 뜨면서 어느 것인지 알 수 없게 된다.
    /// </summary>
    private readonly string _logSrc;

    private readonly object _lock = new();
    private readonly FrameBuf[] _bufs;
    private readonly FrameBuf[] _free;
    private int _freeCount;
    private int _bufferBytes;
    private bool _hasLoggedGrowth;

    public GevFramePool(int bufferCount, int initialBytes, string? device = null)
    {
        if (bufferCount < 1) throw new ArgumentOutOfRangeException(nameof(bufferCount), "Buffer count must be at least 1.");
        if (initialBytes < 0) throw new ArgumentOutOfRangeException(nameof(initialBytes));

        _logSrc = device is null ? LogSrc : $"{LogSrc} {device}";

        _bufs = new FrameBuf[bufferCount];
        _free = new FrameBuf[bufferCount];
        for (var i = 0; i < bufferCount; i++)
        {
            _bufs[i] = new FrameBuf(i, initialBytes);
            _free[i] = _bufs[i];
        }
        _freeCount = bufferCount;
        _bufferBytes = initialBytes;
    }

    public int BufferCount => _bufs.Length;

    public int FreeCount
    {
        get { lock (_lock) return _freeCount; }
    }

    /// <summary>지금까지 요구된 최대 프레임 크기 — 새로 빌리는 버퍼는 최소 이 크기로 맞춰진다.</summary>
    public int BufferBytes => Volatile.Read(ref _bufferBytes);

    /// <summary>
    /// 빈 버퍼를 빌린다. 버퍼가 minBytes 보다 작으면 그 버퍼만 재할당한다(처음 한 번은 Info, 이후는 Debug 로그).
    /// 빈 버퍼가 없으면 false.
    /// </summary>
    public bool TryRent(int minBytes, out FrameBuf buf, out int version)
    {
        if (minBytes < 0) throw new ArgumentOutOfRangeException(nameof(minBytes));

        int target;
        lock (_lock)
        {
            if (_freeCount == 0)
            {
                buf = null!;
                version = 0;
                return false;
            }

            buf = _free[--_freeCount];
            buf.IsFree = false;
            version = ++buf.Version;
            if (minBytes > _bufferBytes) _bufferBytes = minBytes;
            target = _bufferBytes;
        }

        // 여기부터 buf 는 호출자만 만진다 — 재할당은 락 밖에서 해도 안전하다.
        if (buf.Data.Length < target)
        {
            var old = buf.Data.Length;
            buf.Data = new byte[target];
            if (!_hasLoggedGrowth)
            {
                _hasLoggedGrowth = true;
                GevLog.Info(_logSrc, $"Frame buffer grown from {old} to {target} bytes (pool of {_bufs.Length}); remaining buffers grow when next rented.");
            }
            else if (GevLog.IsEnabled(GevLogLevel.Debug))
            {
                GevLog.Debug(_logSrc, $"Frame buffer #{buf.Index} grown from {old} to {target} bytes.");
            }
        }

        return true;
    }

    /// <summary>버퍼를 돌려놓는다. 이미 반납됐거나 다른 대여의 버전이면 무시한다(뒤늦은 이중 반납 방지).</summary>
    public void Return(FrameBuf buf, int version)
    {
        if (buf is null) return;
        lock (_lock)
        {
            if (buf.IsFree || buf.Version != version) return;
            buf.Version++;
            buf.IsFree = true;
            _free[_freeCount++] = buf;
        }
    }

    /// <summary>빌려 주지 않은 버퍼의 메모리를 놓는다. 소비자가 들고 있는 버퍼는 그대로 유효하다.</summary>
    public void ReleaseFree()
    {
        lock (_lock)
        {
            for (var i = 0; i < _freeCount; i++)
            {
                _free[i].Data = Array.Empty<byte>();
            }
        }
    }
}
