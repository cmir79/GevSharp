using System.Buffers.Binary;

namespace GevSharp.Sim;

/// <summary>바이트 배열에 대한 빅엔디언 읽기·쓰기. 시뮬레이터는 라이브러리의 패킷 코드와 독립적으로 와이어 형식을 다룬다.</summary>
internal static class SimWire
{
    public static ushort ReadU16(byte[] buf, int offset) => BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(offset, 2));
    public static uint ReadU32(byte[] buf, int offset) => BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(offset, 4));
    public static ulong ReadU64(byte[] buf, int offset) => BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(offset, 8));

    public static void WriteU16(byte[] buf, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset, 2), value);
    public static void WriteU32(byte[] buf, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset, 4), value);
    public static void WriteU64(byte[] buf, int offset, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(offset, 8), value);
}
