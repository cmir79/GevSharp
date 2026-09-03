using System.Globalization;
using System.Text.Json;
using GevSharp.Pfnc;

namespace GevSharp.Cli.Commands;

/// <summary>
/// 프레임을 &lt;frameId&gt;.bin(원시 페이로드 바이트, 스트라이드 그대로)과 &lt;frameId&gt;.json(폭·높이·스트라이드·픽셀 포맷 등)으로 저장한다.
/// 블록 ID 는 16비트 모드에서 되돌아오므로 긴 실행에서는 같은 이름을 덮어쓴다.
/// </summary>
public sealed class FrameSaver
{
    private readonly string _dir;

    public FrameSaver(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new CliUsageException("option --save expects a directory path");
        _dir = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(_dir);   // Directory 프로퍼티가 System.IO.Directory 를 가린다
    }

    public string Directory => _dir;
    public int SavedFrames { get; private set; }
    public long SavedBytes { get; private set; }

    public void Save(GevFrame frame)
    {
        var baseName = frame.FrameId.ToString(CultureInfo.InvariantCulture);
        var binPath = Path.Combine(_dir, baseName + ".bin");
        var jsonPath = Path.Combine(_dir, baseName + ".json");

        using (var fs = new FileStream(binPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            fs.Write(frame.Data.Span);
        }

        using (var fs = new FileStream(jsonPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("frameId", frame.FrameId);
            w.WriteNumber("timestamp", frame.Timestamp);
            w.WriteNumber("width", frame.Width);
            w.WriteNumber("height", frame.Height);
            w.WriteNumber("offsetX", frame.OffsetX);
            w.WriteNumber("offsetY", frame.OffsetY);
            w.WriteNumber("paddingX", frame.PaddingX);
            w.WriteNumber("paddingY", frame.PaddingY);
            w.WriteNumber("stride", frame.Stride);
            w.WriteString("pixelFormat", PixelFormatInfo.Name(frame.PixelFormatCode));
            w.WriteString("pixelFormatCode", "0x" + frame.PixelFormatCode.ToString("X8", CultureInfo.InvariantCulture));
            w.WriteNumber("bitsPerPixel", PixelFormatInfo.BitsPerPixel(frame.PixelFormatCode));
            w.WriteNumber("payloadSize", frame.PayloadSize);
            w.WriteBoolean("isComplete", frame.IsComplete);
            w.WriteNumber("missingPackets", frame.MissingPackets);
            w.WriteBoolean("hasChunkData", frame.HasChunkData);
            w.WriteString("dataFile", baseName + ".bin");
            w.WriteEndObject();
        }

        SavedFrames++;
        SavedBytes += frame.PayloadSize;
    }
}
