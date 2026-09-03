using System.Buffers.Binary;
using System.Text;

namespace GevSharp.Sim;

/// <summary>
/// 장치 레지스터 공간의 바이트 이미지. 두 영역으로 이뤄진다:
///  - 메인 영역 0x0000_0000..0x0001_0FFF — 부트스트랩 64 KiB + 피처 페이지 4 KiB(<see cref="SimFeatureAddr"/>). 표에 없는 주소는 평범한 RAM 이다.
///  - XML 영역 0x0010_0000.. — GenApi XML 바이트(4의 배수로 0 패딩), 읽기 전용.
/// 모든 접근은 하나의 잠금 아래에서 이뤄지며 다중 바이트 값은 빅엔디언이다.
/// 매핑되지 않은 주소는 <see cref="ArgumentOutOfRangeException"/> — 프로토콜 계층은 <see cref="Contains"/> 로 먼저 검사해 INVALID_ADDRESS 로 바꾼다.
/// </summary>
public sealed class SimRegisterMap
{
    public const uint MainRegionSize = 0x0001_1000;
    public const uint XmlRegionBase = 0x0010_0000;

    private readonly object _gate = new();
    private readonly byte[] _main = new byte[MainRegionSize];
    private readonly byte[] _xml;
    private readonly bool[] _readOnlyWords = new bool[MainRegionSize / 4];

    /// <param name="xmlBytes">UTF-8 XML 본문. 길이가 4의 배수가 아니면 0 으로 채워 영역을 만든다.</param>
    public SimRegisterMap(byte[] xmlBytes)
    {
        if (xmlBytes is null) throw new ArgumentNullException(nameof(xmlBytes));
        XmlLength = xmlBytes.Length;
        int padded = (xmlBytes.Length + 3) & ~3;
        _xml = new byte[padded];
        Buffer.BlockCopy(xmlBytes, 0, _xml, 0, xmlBytes.Length);
    }

    /// <summary>XML 본문의 실제 바이트 수(First URL 에 적히는 길이).</summary>
    public int XmlLength { get; }

    /// <summary>XML 영역의 매핑 크기(4의 배수).</summary>
    public uint XmlRegionSize => (uint)_xml.Length;

    /// <summary>[addr, addr+length) 전체가 한 영역 안에 있으면 true. 영역 경계를 걸치는 접근은 허용하지 않는다.</summary>
    public bool Contains(uint addr, int length)
    {
        if (length < 0) return false;
        ulong end = (ulong)addr + (ulong)length;
        if (addr < MainRegionSize) return end <= MainRegionSize;
        if (addr >= XmlRegionBase) return end <= XmlRegionBase + (ulong)_xml.Length;
        return false;
    }

    /// <summary>범위 안의 4바이트 워드 하나라도 읽기 전용이면 true. XML 영역은 전부 읽기 전용이다.</summary>
    public bool IsReadOnly(uint addr, int length)
    {
        if (length <= 0) return false;
        if (addr >= XmlRegionBase) return true;
        lock (_gate)
        {
            uint first = addr / 4;
            uint last = (uint)(((ulong)addr + (ulong)length - 1) / 4);
            for (uint w = first; w <= last && w < _readOnlyWords.Length; w++)
            {
                if (_readOnlyWords[w]) return true;
            }
            return false;
        }
    }

    /// <summary>메인 영역의 [addr, addr+length) 를 읽기 전용으로 표시한다(WRITEREG/WRITEMEM → WRITE_PROTECT). 내부 쓰기(<see cref="WriteU32"/> 등)는 막지 않는다.</summary>
    public void MarkReadOnly(uint addr, int length) => SetReadOnly(addr, length, true);

    public void MarkWritable(uint addr, int length) => SetReadOnly(addr, length, false);

    private void SetReadOnly(uint addr, int length, bool value)
    {
        if (addr >= MainRegionSize) throw new ArgumentOutOfRangeException(nameof(addr), "Only the main region carries a write-protection table.");
        lock (_gate)
        {
            uint first = addr / 4;
            uint last = (uint)(((ulong)addr + (ulong)Math.Max(length, 1) - 1) / 4);
            for (uint w = first; w <= last && w < _readOnlyWords.Length; w++) _readOnlyWords[w] = value;
        }
    }

    public uint ReadU32(uint addr)
    {
        lock (_gate)
        {
            var (arr, off) = Resolve(addr, 4);
            return BinaryPrimitives.ReadUInt32BigEndian(arr.AsSpan(off, 4));
        }
    }

    public void WriteU32(uint addr, uint value)
    {
        lock (_gate)
        {
            var (arr, off) = Resolve(addr, 4);
            BinaryPrimitives.WriteUInt32BigEndian(arr.AsSpan(off, 4), value);
        }
    }

    public ulong ReadU64(uint addr)
    {
        lock (_gate)
        {
            var (arr, off) = Resolve(addr, 8);
            return BinaryPrimitives.ReadUInt64BigEndian(arr.AsSpan(off, 8));
        }
    }

    public void WriteU64(uint addr, ulong value)
    {
        lock (_gate)
        {
            var (arr, off) = Resolve(addr, 8);
            BinaryPrimitives.WriteUInt64BigEndian(arr.AsSpan(off, 8), value);
        }
    }

    /// <summary>IEEE 754 binary32, 빅엔디언.</summary>
    public float ReadF32(uint addr) => BitConverter.Int32BitsToSingle((int)ReadU32(addr));

    public void WriteF32(uint addr, float value) => WriteU32(addr, (uint)BitConverter.SingleToInt32Bits(value));

    public void ReadBytes(uint addr, Span<byte> dst)
    {
        lock (_gate)
        {
            var (arr, off) = Resolve(addr, dst.Length);
            arr.AsSpan(off, dst.Length).CopyTo(dst);
        }
    }

    public byte[] ReadBytes(uint addr, int length)
    {
        var buf = new byte[length];
        ReadBytes(addr, buf);
        return buf;
    }

    public void WriteBytes(uint addr, ReadOnlySpan<byte> src)
    {
        lock (_gate)
        {
            var (arr, off) = Resolve(addr, src.Length);
            src.CopyTo(arr.AsSpan(off, src.Length));
        }
    }

    /// <summary>고정 길이 NUL 종료 UTF-8 문자열을 읽는다. NUL 이 없으면 length 전체를 문자열로 본다.</summary>
    public string ReadString(uint addr, int length)
    {
        var buf = ReadBytes(addr, length);
        int n = Array.IndexOf(buf, (byte)0);
        if (n < 0) n = buf.Length;
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    /// <summary>문자열을 UTF-8 로 넣고 나머지는 NUL 로 채운다. 마지막 바이트는 항상 NUL 이 되도록 length−1 바이트에서 자른다.</summary>
    public void WriteString(uint addr, int length, string value)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        var buf = new byte[length];
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        int n = Math.Min(bytes.Length, length - 1);
        Buffer.BlockCopy(bytes, 0, buf, 0, n);
        WriteBytes(addr, buf);
    }

    private (byte[] Array, int Offset) Resolve(uint addr, int length)
    {
        if (!Contains(addr, length))
            throw new ArgumentOutOfRangeException(nameof(addr), $"Address 0x{addr:X8} (+{length}) is not mapped.");
        return addr < MainRegionSize
            ? (_main, (int)addr)
            : (_xml, (int)(addr - XmlRegionBase));
    }
}
