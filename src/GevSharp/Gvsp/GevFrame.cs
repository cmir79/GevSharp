using GevSharp.Gvsp;

namespace GevSharp;

/// <summary>리더에서 읽어 프레임에 옮겨 싣는 메타데이터. 수신 스레드가 채운다.</summary>
internal struct FrameMeta
{
    public ulong FrameId;
    public ulong Timestamp;
    public uint PixelFormatCode;
    public ushort PayloadType;
    public int Width;
    public int Height;
    public int OffsetX;
    public int OffsetY;
    public int PaddingX;
    public int PaddingY;
    public int Stride;
    public int PayloadSize;
    public bool IsComplete;
    public bool HasChunkData;
    public int MissingPackets;
    public int ExpectedPackets;
}

/// <summary>
/// 풀 버퍼의 대여권. <see cref="GevStream.ReceiveAsync"/> 가 돌려준 순간부터 <see cref="Dispose"/> 까지 소비자가 버퍼를 소유하며,
/// 그동안 수신 스레드는 이 버퍼에 쓰지 않는다. <see cref="Data"/> 는 버퍼를 복사 없이 감싼 것이라 Dispose 뒤에는 쓸 수 없다 —
/// 픽셀을 오래 들고 있으려면 <see cref="ToArray"/>.
/// 대여마다 새 객체를 만든다(프레임당 한 번, 패킷당은 아니다). 같은 객체를 재사용하면 뒤늦은 Dispose 가 다음 대여를 풀어 버릴 수 있다.
/// </summary>
public sealed class GevFrame : IDisposable
{
    private readonly GevFramePool _pool;
    private readonly FrameBuf _buf;
    private readonly int _version;
    private int _isDisposed;

    internal GevFrame(GevFramePool pool, FrameBuf buf, int version, in FrameMeta meta)
    {
        _pool = pool;
        _buf = buf;
        _version = version;
        FrameId = meta.FrameId;
        Timestamp = meta.Timestamp;
        PixelFormatCode = meta.PixelFormatCode;
        PayloadType = meta.PayloadType;
        Width = meta.Width;
        Height = meta.Height;
        OffsetX = meta.OffsetX;
        OffsetY = meta.OffsetY;
        PaddingX = meta.PaddingX;
        PaddingY = meta.PaddingY;
        Stride = meta.Stride;
        PayloadSize = meta.PayloadSize;
        IsComplete = meta.IsComplete;
        HasChunkData = meta.HasChunkData;
        MissingPackets = meta.MissingPackets;
        ExpectedPackets = meta.ExpectedPackets;
        // 청크가 없으면 유효 바이트가 곧 이미지다. 붙어 있으면 기하로 계산하되, 실제로 받은 것보다 크게 잡지 않는다
        // (모르는 코드는 0 이 나오므로 그때도 받은 만큼으로 둔다 — 자를 근거가 없으면 자르지 않는다).
        var imageBytes = meta.HasChunkData
            ? Pfnc.PixelFormatInfo.ImageBytesLong(meta.PixelFormatCode, meta.Width, meta.Height, meta.PaddingX, meta.PaddingY)
            : meta.PayloadSize;
        ImageSize = imageBytes <= 0 || imageBytes > meta.PayloadSize ? meta.PayloadSize : (int)imageBytes;
    }

    /// <summary>GVSP 블록 ID(16비트 또는 64비트).</summary>
    public ulong FrameId { get; }

    /// <summary>리더의 타임스탬프(장치 틱).</summary>
    public ulong Timestamp { get; }

    /// <summary>PFNC 픽셀 포맷 코드.</summary>
    public uint PixelFormatCode { get; }

    /// <summary>리더의 payload_type(청크 비트 포함 원시 값).</summary>
    public ushort PayloadType { get; }

    public int Width { get; }
    public int Height { get; }
    public int OffsetX { get; }
    public int OffsetY { get; }
    public int PaddingX { get; }
    public int PaddingY { get; }

    /// <summary>
    /// 한 줄의 바이트 수 = <see cref="Pfnc.PixelFormatInfo.LineBytes(uint, int)"/>(<see cref="PixelFormatCode"/>, <see cref="Width"/>) + <see cref="PaddingX"/>.
    /// 대부분은 ceil(Width × bpp / 8) 이지만 묶음 단위로 실리는 포맷은 마지막 묶음을 통째로 세므로 그 공식과 갈린다 —
    /// GVSP Packed 는 2픽셀 3바이트, 4:1:1 은 4픽셀 6바이트다(홀수 폭에서 1 바이트 차이가 난다).
    /// 소비자는 Width 로 다시 계산하지 말고 반드시 이 값으로 줄을 건너뛴다.
    /// <para>
    /// <b>0 은 "줄 간격이 없다" 는 뜻</b> — 줄이 바이트 경계에서 끝나지 않는 폭(홀수 폭 packed 등)에 줄 패딩까지 없으면
    /// 다음 줄이 바이트 가운데에서 시작하므로 어떤 바이트 수로도 줄을 건너뛸 수 없다. 그때 <see cref="Data"/> 는
    /// Width × Height 픽셀이 이어 붙은 한 덩어리이고, <see cref="Pfnc.PixelUnpack"/> 이 그대로 풀어 준다.
    /// </para>
    /// </summary>
    public int Stride { get; }

    /// <summary><see cref="Data"/> 안의 유효 바이트 수.</summary>
    public int PayloadSize { get; }

    /// <summary>모든 페이로드 패킷이 모였는지. false 는 <see cref="GevStreamOpt.DeliverIncompleteFrames"/> 가 켜졌을 때만 온다.</summary>
    public bool IsComplete { get; }

    /// <summary>이미지 뒤에 청크 데이터가 붙어 있는지(리더의 bit14 또는 extended chunk 타입). 청크는 해석하지 않고 바이트만 실어 준다.</summary>
    public bool HasChunkData { get; }

    /// <summary>못 받은 페이로드 패킷 수. 빠진 영역은 0 으로 채워져 있다.</summary>
    public int MissingPackets { get; }

    /// <summary>예상 페이로드 패킷 수.</summary>
    public int ExpectedPackets { get; }

    /// <summary>
    /// <see cref="Data"/> 앞쪽에서 이미지가 차지하는 바이트 수. 청크가 없으면 <see cref="PayloadSize"/> 와 같고,
    /// 붙어 있으면 그보다 작다 — 청크 바이트는 그 뒤에 이어진다.
    /// <para>
    /// 청크가 붙은 프레임은 리더가 크기를 알려 주지 못해 <see cref="PayloadSize"/> 가 "실제로 받은 끝" 이다.
    /// 그 값을 이미지 크기로 알고 화소를 읽으면 청크 바이트까지 화소로 셈하게 되므로, 이미지만 볼 때는 이 값으로 자른다:
    /// <c>frame.Data.Slice(0, frame.ImageSize)</c>. 기하가 알려 주는 크기보다 실제로 받은 것이 적으면 받은 만큼으로 줄어든다.
    /// </para>
    /// </summary>
    public int ImageSize { get; }

    public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    /// <summary>풀 버퍼 위의 무복사 뷰. Dispose 뒤에는 <see cref="ObjectDisposedException"/>.</summary>
    public ReadOnlyMemory<byte> Data
    {
        get
        {
            ThrowIfDisposed();
            return new ReadOnlyMemory<byte>(_buf.Data, 0, PayloadSize);
        }
    }

    /// <summary>유효 바이트를 복사해 돌려준다 — Dispose 뒤에도 살아남는 사본.</summary>
    public byte[] ToArray()
    {
        ThrowIfDisposed();
        var copy = new byte[PayloadSize];
        Buffer.BlockCopy(_buf.Data, 0, copy, 0, PayloadSize);
        return copy;
    }

    /// <summary>버퍼를 풀에 돌려준다. 몇 번을 불러도, 어느 스레드에서 불러도 한 번만 반납된다.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
        _pool.Return(_buf, _version);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _isDisposed) != 0 || Volatile.Read(ref _buf.Version) != _version)
        {
            throw new ObjectDisposedException(nameof(GevFrame), "Frame has been returned to the pool.");
        }
    }
}
