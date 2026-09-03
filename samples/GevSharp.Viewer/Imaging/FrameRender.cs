using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GevSharp.Pfnc;
using PfncFormat = GevSharp.Pfnc.PixelFormat;

namespace GevSharp.Viewer.Imaging;

/// <summary>
/// 받은 프레임을 화면에 올릴 비트맵으로 옮긴다. 카메라가 알려 준 픽셀 포맷과 줄 간격만 보고 판단하므로
/// 벤더를 구분하지 않는다. 표시용 변환이라 계조는 8비트로 줄인다 — 원본 화소가 필요하면 프레임에서 직접 가져간다.
/// </summary>
public sealed class FrameRender : IDisposable
{
    // 비트맵 두 장을 번갈아 쓴다. 한 장을 계속 고쳐 쓰면 두 가지가 어긋난다 — 참조가 그대로라
    // 바인딩이 갱신을 알아채지 못하고, 화면이 합성 중인 그림에 그대로 덮어써서 찢어진다.
    private WriteableBitmap? _front;
    private WriteableBitmap? _back;
    private int _width;
    private int _height;
    private ushort[] _scratch = Array.Empty<ushort>();

    /// <summary>표시할 수 없는 포맷이면 그 이유. 표시에 성공하면 null.</summary>
    public string? Unsupported { get; private set; }

    /// <summary>프레임을 그리고 그 비트맵을 돌려준다. 크기가 그대로면 두 장을 번갈아 쓸 뿐 새로 만들지 않는다.</summary>
    public WriteableBitmap? Render(GevFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            Unsupported = "frame carries no geometry";
            return _front;
        }

        EnsureBitmaps(frame.Width, frame.Height);
        // 이번 장은 화면에 걸려 있지 않은 쪽에 그린다.
        (_front, _back) = (_back, _front);
        var target = _front!;
        var code = frame.PixelFormatCode;

        using var locked = target.Lock();
        var src = frame.Data.Span.Slice(0, frame.ImageSize);

        if (code == (uint)PfncFormat.Mono8)
        {
            Unsupported = null;
            WriteMono8(src, frame, locked);
        }
        else if (PixelFormatInfo.IsMono(code) && PixelUnpack.CanUnpack(code))
        {
            Unsupported = null;
            WriteMonoWide(src, frame, locked, code);
        }
        else if (code is (uint)PfncFormat.RGB8 or (uint)PfncFormat.BGR8)
        {
            Unsupported = null;
            WriteRgb8(src, frame, locked, isBgr: code == (uint)PfncFormat.BGR8);
        }
        else if (PixelFormatInfo.IsBayer(code) && PixelFormatInfo.BitsPerPixel(code) == 8)
        {
            Unsupported = null;
            WriteBayer8(src, frame, locked, PixelFormatInfo.BayerPattern(code));
        }
        else
        {
            var name = PixelFormatInfo.IsKnown(code) ? PixelFormatInfo.ToPixelFormat(code).ToString() : $"0x{code:X8}";
            Unsupported = $"{name} is not rendered by this viewer yet";
        }

        return target;
    }

    /// <summary>줄 간격. 0 은 "줄 정렬이 없다" 는 뜻이라 폭에서 계산한 길이를 그대로 쓴다.</summary>
    private static int RowStep(GevFrame frame, int packedRowBytes)
        => frame.Stride > 0 ? frame.Stride : packedRowBytes;

    private unsafe void WriteMono8(ReadOnlySpan<byte> src, GevFrame frame, ILockedFramebuffer fb)
    {
        var step = RowStep(frame, frame.Width);
        var dst = (byte*)fb.Address;
        for (var y = 0; y < frame.Height; y++)
        {
            var rowStart = y * step;
            if (rowStart + frame.Width > src.Length) break;
            var row = src.Slice(rowStart, frame.Width);
            var p = dst + y * fb.RowBytes;
            for (var x = 0; x < row.Length; x++)
            {
                var g = row[x];
                p[x * 4 + 0] = g;
                p[x * 4 + 1] = g;
                p[x * 4 + 2] = g;
                p[x * 4 + 3] = 255;
            }
        }
    }

    private unsafe void WriteMonoWide(ReadOnlySpan<byte> src, GevFrame frame, ILockedFramebuffer fb, uint code)
    {
        var count = frame.Width * frame.Height;
        if (_scratch.Length < count) _scratch = new ushort[count];
        // 줄 간격 0 은 줄 정렬이 없다는 뜻이므로 폭 기준 길이로 푼다.
        var srcStride = frame.Stride > 0 ? frame.Stride : 0;
        PixelUnpack.Unpack(code, src, srcStride, _scratch, frame.Width, frame.Width, frame.Height);

        var shift = Math.Max(0, PixelFormatInfo.Depth(code) - 8);
        var dst = (byte*)fb.Address;
        for (var y = 0; y < frame.Height; y++)
        {
            var p = dst + y * fb.RowBytes;
            var o = y * frame.Width;
            for (var x = 0; x < frame.Width; x++)
            {
                var g = (byte)(_scratch[o + x] >> shift);
                p[x * 4 + 0] = g;
                p[x * 4 + 1] = g;
                p[x * 4 + 2] = g;
                p[x * 4 + 3] = 255;
            }
        }
    }

    private unsafe void WriteRgb8(ReadOnlySpan<byte> src, GevFrame frame, ILockedFramebuffer fb, bool isBgr)
    {
        var step = RowStep(frame, frame.Width * 3);
        var dst = (byte*)fb.Address;
        for (var y = 0; y < frame.Height; y++)
        {
            var rowStart = y * step;
            if (rowStart + frame.Width * 3 > src.Length) break;
            var row = src.Slice(rowStart, frame.Width * 3);
            var p = dst + y * fb.RowBytes;
            for (var x = 0; x < frame.Width; x++)
            {
                var a = row[x * 3 + 0];
                var g = row[x * 3 + 1];
                var c = row[x * 3 + 2];
                p[x * 4 + 0] = isBgr ? a : c;   // B
                p[x * 4 + 1] = g;               // G
                p[x * 4 + 2] = isBgr ? c : a;   // R
                p[x * 4 + 3] = 255;
            }
        }
    }

    /// <summary>
    /// 2x2 묶음 하나를 화소 넷에 그대로 펴는 가장 단순한 디베이어. 보간하지 않으므로 색 경계가 거칠지만
    /// 화면으로 색과 초점을 확인하는 데에는 충분하고, 프레임마다 도는 경로라 값이 싸다.
    /// </summary>
    private unsafe void WriteBayer8(ReadOnlySpan<byte> src, GevFrame frame, ILockedFramebuffer fb, BayerPattern pattern)
    {
        var step = RowStep(frame, frame.Width);
        var dst = (byte*)fb.Address;

        for (var y = 0; y + 1 < frame.Height; y += 2)
        {
            var r0 = y * step;
            var r1 = (y + 1) * step;
            if (r1 + frame.Width > src.Length) break;

            for (var x = 0; x + 1 < frame.Width; x += 2)
            {
                var a = src[r0 + x];
                var b = src[r0 + x + 1];
                var c = src[r1 + x];
                var d = src[r1 + x + 1];

                // 배열 이름의 두 글자가 첫 줄 첫 두 픽셀의 색이다 — 그 정의를 그대로 편다.
                // 네 자리 중 R 과 B 는 대각선으로 하나씩이고 남은 대각선 둘이 G 다.
                byte red, green, blue;
                switch (pattern)
                {
                    case BayerPattern.RG:                       // R G / G B
                        red = a; green = (byte)((b + c) >> 1); blue = d; break;
                    case BayerPattern.GR:                       // G R / B G
                        red = b; green = (byte)((a + d) >> 1); blue = c; break;
                    case BayerPattern.GB:                       // G B / R G
                        red = c; green = (byte)((a + d) >> 1); blue = b; break;
                    default:                                    // BG: B G / G R
                        red = d; green = (byte)((b + c) >> 1); blue = a; break;
                }

                for (var dy = 0; dy < 2; dy++)
                {
                    var p = dst + (y + dy) * fb.RowBytes + x * 4;
                    for (var dx = 0; dx < 2; dx++)
                    {
                        p[dx * 4 + 0] = blue;
                        p[dx * 4 + 1] = green;
                        p[dx * 4 + 2] = red;
                        p[dx * 4 + 3] = 255;
                    }
                }
            }
        }
    }

    private void EnsureBitmaps(int width, int height)
    {
        if (_front is not null && _back is not null && _width == width && _height == height) return;
        _front?.Dispose();
        _back?.Dispose();
        _front = Create(width, height);
        _back = Create(width, height);
        _width = width;
        _height = height;
    }

    private static WriteableBitmap Create(int width, int height)
        => new(new PixelSize(width, height), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Opaque);

    public void Dispose()
    {
        _front?.Dispose();
        _back?.Dispose();
        _front = null;
        _back = null;
    }
}
