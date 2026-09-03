using System.Buffers.Binary;
using GevSharp.Gvcp;

namespace GevSharp;

/// <summary>레지스터·메모리 접근과 <see cref="IGevPort"/> 구현.</summary>
public sealed partial class GevDevice
{
    /// <summary>장치가 한 패킷에 여러 레지스터를 허용하는지(GVCP capability concatenation 비트). 아니면 한 개씩 보낸다.</summary>
    private bool CanConcatenate => (GvcpCapability & GvbsAddr.GvcpCapConcatenation) != 0;

    // ------------------------------------------------------------------ registers

    public Task<uint> ReadRegAsync(uint addr, CancellationToken ct = default)
    {
        ThrowIfClosed();
        return ReadRegCoreAsync(addr, ct);
    }

    /// <summary>여러 레지스터를 읽는다. concatenation 을 지원하면 135 개씩 묶고, 아니면 하나씩 보낸다. 결과는 입력 순서.</summary>
    public async Task<uint[]> ReadRegsAsync(IReadOnlyList<uint> addrs, CancellationToken ct = default)
    {
        if (addrs is null) throw new ArgumentNullException(nameof(addrs));
        ThrowIfClosed();
        if (addrs.Count == 0) return Array.Empty<uint>();

        var list = addrs as uint[] ?? addrs.ToArray();
        var result = new uint[list.Length];
        var batch = CanConcatenate ? GvcpConst.MaxRegsPerPacket : 1;
        for (var i = 0; i < list.Length; i += batch)
        {
            var n = Math.Min(batch, list.Length - i);
            var cmd = GvcpCmd.ReadRegs(list.AsSpan(i, n));
            var ack = await Gvcp.RequestAsync(cmd, ct).ConfigureAwait(false);
            if (ack.RegCount != n)
                throw new GevException($"READREG_ACK returned {ack.RegCount} value(s), expected {n}");
            for (var j = 0; j < n; j++)
                result[i + j] = ack.GetRegValue(j);
        }
        return result;
    }

    public Task WriteRegAsync(uint addr, uint value, CancellationToken ct = default)
    {
        ThrowIfClosed();
        return WriteRegCoreAsync(addr, value, ct);
    }

    /// <summary>
    /// 여러 레지스터를 쓴다. concatenation 을 지원하면 67 쌍씩 묶고, 아니면 하나씩 보낸다.
    /// 장치가 항목 하나를 거절하면 <see cref="GevStatusException.FailedIndex"/>(와 Data[<see cref="GvcpChannel.FailedIndexKey"/>]) 에
    /// writes 기준 번호를 넣는다 — 그 앞 항목은 적용됐고 그 항목부터는 아니다.
    /// </summary>
    public async Task WriteRegsAsync(IReadOnlyList<KeyValuePair<uint, uint>> writes, CancellationToken ct = default)
    {
        if (writes is null) throw new ArgumentNullException(nameof(writes));
        ThrowIfClosed();
        if (writes.Count == 0) return;

        var list = writes as KeyValuePair<uint, uint>[] ?? writes.ToArray();
        var batch = CanConcatenate ? GvcpPacket.MaxWriteRegsPerPacket : 1;
        for (var i = 0; i < list.Length; i += batch)
        {
            var n = Math.Min(batch, list.Length - i);
            var cmd = GvcpCmd.WriteRegs(list.AsSpan(i, n));
            GvcpAck ack;
            try
            {
                ack = await Gvcp.RequestAsync(cmd, ct).ConfigureAwait(false);
            }
            catch (GevStatusException ex) when (ex.Data[GvcpChannel.FailedIndexKey] is int inBatch)
            {
                // 묶음 안 번호를 writes 전체 번호로 바꿔 둔다.
                ex.Data[GvcpChannel.FailedIndexKey] = i + inBatch;
                ex.FailedIndex = i + inBatch;
                GevLog.Warn(LogSrc, $"WRITEREG batch {i}..{i + n - 1} of {list.Length} rejected at entry {i + inBatch}; entries before it were applied");
                throw;
            }
            LogWriteIndex(ack, n, "register(s)");
        }
    }

    private async Task<uint> ReadRegCoreAsync(uint addr, CancellationToken ct)
    {
        var ack = await Gvcp.RequestAsync(GvcpCmd.ReadReg(addr), ct).ConfigureAwait(false);
        if (ack.RegCount < 1)
            throw new GevException($"READREG_ACK for 0x{addr:X8} carries no value");
        return ack.GetRegValue(0);
    }

    private async Task WriteRegCoreAsync(uint addr, uint value, CancellationToken ct)
    {
        var ack = await Gvcp.RequestAsync(GvcpCmd.WriteReg(addr, value), ct).ConfigureAwait(false);
        LogWriteIndex(ack, 1, "register(s)");
    }

    /// <summary>ack 의 index 가 요청과 다르면 디버그 로그만 남긴다 — 장치마다 index 를 채우는 방식이 달라 오류로 올리지 않는다.</summary>
    private static void LogWriteIndex(GvcpAck ack, int expected, string unit)
    {
        if (ack.TryGetWriteIndex(out var index) && index != expected && GevLog.IsEnabled(GevLogLevel.Debug))
            GevLog.Debug(LogSrc, $"{ack.Name}_ACK index {index} differs from the {expected} {unit} requested");
    }

    // ------------------------------------------------------------------ memory

    /// <summary>
    /// addr 부터 dst.Length 바이트를 읽는다. READMEM 은 4바이트 정렬·4의 배수 길이만 받으므로 앞뒤를 넓혀 읽고 요청한 창만 복사한다.
    /// 512 바이트씩 나눈다.
    /// </summary>
    public async Task ReadMemAsync(uint addr, Memory<byte> dst, CancellationToken ct = default)
    {
        ThrowIfClosed();
        if (dst.Length == 0) return;
        ThrowIfRangeOverflows(addr, dst.Length);

        var alignedStart = addr & ~3u;
        var alignedEnd = ((ulong)addr + (ulong)dst.Length + 3) & ~3UL;
        var cursor = (ulong)alignedStart;
        while (cursor < alignedEnd)
        {
            var chunk = (int)Math.Min((ulong)GvcpConst.MaxMemPayload, alignedEnd - cursor);
            var cmd = GvcpCmd.ReadMem((uint)cursor, chunk);
            var ack = await Gvcp.RequestAsync(cmd, ct).ConfigureAwait(false);
            CopyReadWindow(ack, (uint)cursor, chunk, addr, dst);
            cursor += (ulong)chunk;
        }
    }

    /// <summary>
    /// READMEM_ACK 를 검증하고 [cursor, cursor+chunk) 와 [addr, addr+dst.Length) 가 겹치는 부분만 복사한다.
    /// 요청보다 짧은 응답은 오류다 — 없는 데이터를 채울 수 없다. 요청보다 긴 응답은 앞에서 요청한 만큼만 쓰고 꼬리를 버린다:
    /// 길이를 워드 단위로 올려 붙여 답하는 장치가 있고, 그 응답은 부트스트랩 문자열을 읽는 경로에서 이미 받아들이고 있다.
    /// 같은 응답이 한 경로에서만 치명적이면 안 된다.
    /// </summary>
    private static void CopyReadWindow(GvcpAck ack, uint cursor, int chunk, uint addr, Memory<byte> dst)
    {
        if (ack.MemAddress != cursor)
            throw new GevException($"READMEM_ACK address 0x{ack.MemAddress:X8} does not match the requested 0x{cursor:X8}");
        var data = ack.MemData;
        if (data.Length < chunk)
            throw new GevException($"READMEM_ACK returned {data.Length} byte(s) for 0x{cursor:X8}, expected {chunk}");
        if (data.Length > chunk)
        {
            if (GevLog.IsEnabled(GevLogLevel.Debug))
                GevLog.Debug(LogSrc, $"READMEM_ACK for 0x{cursor:X8} carries {data.Length} byte(s) for a {chunk}-byte request; using the first {chunk}");
            data = data.Slice(0, chunk);
        }

        var from = Math.Max((long)cursor, addr);
        var to = Math.Min((long)cursor + chunk, (long)addr + dst.Length);
        if (to <= from) return;
        data.Slice((int)(from - cursor), (int)(to - from)).CopyTo(dst.Span.Slice((int)(from - addr)));
    }

    /// <summary>
    /// addr 부터 src 를 쓴다. WRITEMEM 은 4바이트 정렬·4의 배수 길이만 받으므로 정렬되지 않은 머리·꼬리는 그 경계 워드 하나씩만 읽어
    /// 원래 바이트를 보존한 채 겹쳐 쓴다(읽기-수정-쓰기). 가운데는 src 가 전부 덮으므로 읽지 않는다 — 쓰기 전용 영역이어도 되고
    /// 큰 블록에 읽기 왕복이 붙지 않는다. 512 바이트씩 나눈다.
    /// </summary>
    public async Task WriteMemAsync(uint addr, ReadOnlyMemory<byte> src, CancellationToken ct = default)
    {
        ThrowIfClosed();
        if (src.Length == 0) return;
        ThrowIfRangeOverflows(addr, src.Length);

        var alignedStart = addr & ~3u;
        var alignedEnd = ((ulong)addr + (ulong)src.Length + 3) & ~3UL;
        var alignedLen = (int)(alignedEnd - alignedStart);
        var headLen = (int)(addr - alignedStart);
        var tailLen = (int)(alignedEnd - ((ulong)addr + (ulong)src.Length));
        ReadOnlyMemory<byte> block = src;
        if (headLen > 0 || tailLen > 0)
        {
            if (GevLog.IsEnabled(GevLogLevel.Debug))
                GevLog.Debug(LogSrc, $"WRITEMEM 0x{addr:X8}+{src.Length} is not 4-byte aligned; widening to 0x{alignedStart:X8}+{alignedLen}, preserving {headLen} leading / {tailLen} trailing byte(s)");
            var widened = new byte[alignedLen];
            var word = new byte[4];
            if (headLen > 0)
            {
                await ReadMemAsync(alignedStart, word, ct).ConfigureAwait(false);
                word.AsSpan(0, headLen).CopyTo(widened);
            }
            if (tailLen > 0)
            {
                var tailWord = (uint)(alignedEnd - 4);
                // 머리와 꼬리가 같은 워드면 이미 읽었다.
                if (!(headLen > 0 && tailWord == alignedStart))
                    await ReadMemAsync(tailWord, word, ct).ConfigureAwait(false);
                word.AsSpan(4 - tailLen).CopyTo(widened.AsSpan(alignedLen - tailLen));
            }
            src.Span.CopyTo(widened.AsSpan(headLen));
            block = widened;
        }

        var offset = 0;
        while (offset < block.Length)
        {
            var chunk = Math.Min(GvcpConst.MaxMemPayload, block.Length - offset);
            var cmd = GvcpCmd.WriteMem(alignedStart + (uint)offset, block.Span.Slice(offset, chunk));
            var ack = await Gvcp.RequestAsync(cmd, ct).ConfigureAwait(false);
            LogWriteIndex(ack, chunk, "byte(s)");
            offset += chunk;
        }
    }

    /// <summary>
    /// NUL 종료 문자열 레지스터를 읽는다. 장치 모드가 알린 문자 집합(UTF-8 / ASCII)으로 해석하고 앞뒤 공백은 그대로 둔다.
    /// length 는 레지스터 전체 길이.
    /// </summary>
    public async Task<string> ReadStringAsync(uint addr, int length, CancellationToken ct = default)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "must be positive");
        var buf = new byte[length];
        await ReadMemAsync(addr, buf, ct).ConfigureAwait(false);
        return GevDeviceInfo.DecodeNulString(buf, _info?.CharacterSet ?? GevDeviceInfo.CharacterSetUtf8);
    }

    private static void ThrowIfRangeOverflows(uint addr, int length)
    {
        if ((ulong)addr + (ulong)length > 0x1_0000_0000UL)
            throw new GevException($"memory range 0x{addr:X8}+{length} exceeds the 32-bit GVCP address space");
    }

    // ------------------------------------------------------------------ IGevPort

    /// <summary>GenApi 포트 읽기. 4바이트 정렬 4바이트는 READREG, 나머지는 READMEM. 32비트를 넘는 주소는 <see cref="GevException"/>.</summary>
    async ValueTask IGevPort.ReadAsync(ulong address, Memory<byte> buffer, CancellationToken ct)
    {
        var addr = ToGvcpAddress(address, buffer.Length);
        if (buffer.Length == 4 && (addr & 3) == 0)
        {
            var value = await ReadRegAsync(addr, ct).ConfigureAwait(false);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.Span, value);
            return;
        }
        await ReadMemAsync(addr, buffer, ct).ConfigureAwait(false);
    }

    /// <summary>GenApi 포트 쓰기. 4바이트 정렬 4바이트는 WRITEREG, 나머지는 WRITEMEM.</summary>
    async ValueTask IGevPort.WriteAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var addr = ToGvcpAddress(address, data.Length);
        if (data.Length == 4 && (addr & 3) == 0)
        {
            await WriteRegAsync(addr, BinaryPrimitives.ReadUInt32BigEndian(data.Span), ct).ConfigureAwait(false);
            return;
        }
        await WriteMemAsync(addr, data, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GenApi 주소(64비트)를 GVCP 가 나르는 32비트 주소로 좁힌다.
    /// 벤더 XML 은 32비트를 넘는 주소를 리터럴로 적어 두기도 한다 — 상위 비트는 GVCP 에 실을 수 없는 장식이고
    /// 실제 레지스터는 하위 32비트에 있다(실장치에서 확인: 파일 접근 기준주소 0xFFFFD0000000 → 0xD0000000 이 유효한 레지스터).
    /// 그래서 조용히 버리지 않고 주소마다 한 번 경고를 남긴 뒤 하위 32비트를 쓴다.
    /// 좁힌 주소에서 길이가 32비트 공간을 넘어서면(끝이 넘침) 그건 장식이 아니라 잘못된 접근이라 예외다.
    /// </summary>
    private uint ToGvcpAddress(ulong address, int length)
    {
        var addr = (uint)address;
        if (address > uint.MaxValue)
        {
            if (_wideAddressesWarned.TryAdd(address, 0))
            {
                GevLog.Warn(LogSrc, $"GenApi address 0x{address:X} does not fit the 32-bit GVCP address space; using its low 32 bits (0x{addr:X8})");
            }
        }
        if ((ulong)addr + (ulong)length > 0x1_0000_0000UL)
            throw new GevException($"range 0x{addr:X8}+{length} exceeds the 32-bit GVCP address space");
        return addr;
    }

    /// <summary>이미 경고한 32비트 초과 주소 — 노드마다 매번 읽어도 로그가 한 번씩만 남게 한다.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _wideAddressesWarned = new();
}
