using GevSharp.GenApi.Model;

namespace GevSharp.GenApi.Runtime;

/// <summary>
/// 레지스터 노드(IntReg/MaskedIntReg/FloatReg/StringReg/Register)가 공유하는 레지스터 접근 부품 —
/// 주소 계산(Address + Σ pAddress + Σ pIndex×offset + 인라인 IntSwissKnife), 길이(Length/pLength), 포트, 원시 바이트 캐시.
/// <para>
/// 캐시는 노드마다 하나이며 해석된 주소를 키로 삼는다(pIndex 가 바뀌면 다른 주소라 자동으로 빗나간다).
/// Cachable: NoCache 는 저장하지 않고, WriteThrough(기본)는 쓴 값을 캐시에 남기며, WriteAround 는 쓰기 뒤 캐시를 버린다.
/// PollingTime 이 있으면 장치가 값을 스스로 바꾸는 레지스터이므로 NoCache 로 본다.
/// 같은 주소를 여러 노드가 나눠 쓰는 경우(StructReg 항목, 별칭 레지스터)는 노드맵이 쓰기 뒤 겹치는 캐시를 찾아 버린다.
/// 쓰기 전용 레지스터의 읽기-수정-쓰기 바탕값은 노드맵의 쓰기 그림자(<see cref="WriteShadow"/>)에서 온다 — 형제 노드가 쓴 비트가 보존된다.
/// </para>
/// 동시 읽기는 안전하다(캐시 접근은 잠금 아래, 빗나가면 두 번 읽을 뿐). 쓰기는 서로 겹치지 않게 호출자가 순서를 맞춘다.
/// </summary>
internal sealed class RegisterCore
{
    private readonly NodeBase _owner;
    private readonly RegisterSet _set;
    private readonly object _lock = new();
    private NodeBase[] _pAddresses = Array.Empty<NodeBase>();
    private IndexTerm[] _pIndexes = Array.Empty<IndexTerm>();
    private IntSwissKnifeNode[] _knives = Array.Empty<IntSwissKnifeNode>();
    private NodeBase? _pLength;
    private PortNode _port = null!;
    private byte[]? _cache;
    private ulong _cacheAddr;

    private readonly struct IndexTerm
    {
        public IndexTerm(NodeBase index, long? offset, NodeBase? pOffset)
        {
            Index = index;
            Offset = offset;
            POffset = pOffset;
        }

        public NodeBase Index { get; }
        public long? Offset { get; }
        public NodeBase? POffset { get; }
    }

    public RegisterCore(NodeBase owner, RegisterSet set)
    {
        _owner = owner;
        _set = set;
    }

    public NodeBase Owner => _owner;
    public RegisterSet Set => _set;
    public AccessMode AccessMode => _set.AccessMode;
    public IGevPort Port => _port.Port;

    /// <summary>
    /// 이 레지스터가 청크 포트에 붙어 있으면 그 사유, 아니면 null. 노드의 접근 모드를 NotAvailable 로 만들고
    /// 읽기·쓰기 오류 메시지에 실린다 — 값이 프레임에 실려 오는데 그 배선이 없으므로, 장치 주소에서 읽어
    /// 그럴듯한 숫자를 내놓는 대신 없다고 말한다.
    /// </summary>
    /// <summary>
    /// 청크 포트의 값은 장치 주소에 없다 — 거기서 읽으면 무관한 바이트가 값인 척 나온다(실측: ChunkExposureTime 이 512).
    /// 접근 모드로 막는 것만으로는 부족하다: 수식(SwissKnife)의 변수 읽기는 접근 모드를 거치지 않고 값을 바로 읽으므로
    /// 여기, 포트에 닿기 직전에서 막아야 그 경로로 새지 않는다.
    /// </summary>
    private void ThrowIfChunkPort()
    {
        if (ChunkPortReason is not { } why) return;
        var ex = new GenApiException($"Node '{_owner.Name}' cannot be read or written: {why}.", _owner.Name);
        ex.Data[GenApiException.ChunkDataKey] = true;
        throw ex;
    }

    public string? ChunkPortReason => _port.IsChunkPort
        ? $"the value comes from chunk data on port '{_port.Name}'; this library does not assemble chunk payloads"
        : null;

    /// <summary>
    /// 레지스터 하나가 가질 수 있는 최대 길이(16 MiB). 길이는 XML 의 Length 든 pLength 가 읽어 온 장치 값이든
    /// 결국 장치가 정하는 수라, 상한이 없으면 잘못됐거나 적대적인 값 하나로 호스트 메모리를 통째로 잡게 된다.
    /// 실제 레지스터는 킬로바이트 단위이므로 이 상한에 걸리는 정상 문서는 없다.
    /// </summary>
    public const int MaxLength = 16 * 1024 * 1024;

    /// <summary>읽은 값을 남겨도 되는지 — NoCache 도 PollingTime 도 없을 때.</summary>
    public bool IsCacheable => _set.Cachable != Cachable.NoCache && _owner.Def.PollingTimeMs is null;

    /// <summary>리터럴 Length. pLength 면 null.</summary>
    public int? StaticLength => _set.Length is { } l ? (int)l : null;

    public void Bind(NodeBinder binder)
    {
        if (_set.PPort is null)
            throw new GenApiException($"Register node '{_owner.Name}' has no pPort.", _owner.Name);
        _port = binder.Resolve<PortNode>(_set.PPort, RefKind.Port, "pPort");

        if (_set.Length is { } len)
        {
            if (len <= 0 || len > MaxLength)
                throw new GenApiException($"Register node '{_owner.Name}' has an invalid Length {len} (allowed 1..{MaxLength}).", _owner.Name);
        }
        else if (_set.PLength is null)
        {
            throw new GenApiException($"Register node '{_owner.Name}' has neither Length nor pLength.", _owner.Name);
        }
        _pLength = binder.ResolveOptional(_set.PLength, RefKind.Value, "pLength", NodeBinder.Numeric);

        var addrs = new NodeBase[_set.PAddresses.Count];
        for (var i = 0; i < addrs.Length; i++)
            addrs[i] = binder.Resolve(_set.PAddresses[i], RefKind.Value, "pAddress", NodeBinder.Numeric);
        _pAddresses = addrs;

        var idx = new IndexTerm[_set.PIndexes.Count];
        for (var i = 0; i < idx.Length; i++)
        {
            var p = _set.PIndexes[i];
            var index = binder.Resolve(p.PNode, RefKind.Value, "pIndex", NodeBinder.Numeric);
            var pOffset = binder.ResolveOptional(p.POffset, RefKind.Value, "pIndex pOffset", NodeBinder.Numeric);
            idx[i] = new IndexTerm(index, p.Offset, pOffset);
        }
        _pIndexes = idx;

        var knives = new IntSwissKnifeNode[_set.AddressSwissKnives.Count];
        for (var i = 0; i < knives.Length; i++)
            knives[i] = binder.AddressKnife(_set.AddressSwissKnives[i], _owner);
        _knives = knives;

        binder.AddCore(this);
    }

    /// <summary>주소 = Σ Address + Σ pAddress + Σ pIndex × (Offset | pOffset | Length) + Σ 인라인 IntSwissKnife. 음수는 오류.</summary>
    public async ValueTask<ulong> ResolveAddressAsync(CancellationToken ct)
    {
        long sum = 0;
        foreach (var a in _set.Addresses) sum += a;
        foreach (var n in _pAddresses) sum += await NodeBase.ReadInt64FromAsync(n, ct).ConfigureAwait(false);
        foreach (var t in _pIndexes)
        {
            var index = await NodeBase.ReadInt64FromAsync(t.Index, ct).ConfigureAwait(false);
            long offset;
            if (t.Offset is { } o) offset = o;
            else if (t.POffset is not null) offset = await NodeBase.ReadInt64FromAsync(t.POffset, ct).ConfigureAwait(false);
            else offset = await GetLengthAsync(ct).ConfigureAwait(false);
            sum += index * offset;
        }
        foreach (var k in _knives) sum += await k.ReadInt64Async(ct).ConfigureAwait(false);
        if (sum < 0)
            throw new GenApiException($"Register '{_owner.Name}' resolved to a negative address ({sum}).", _owner.Name);
        return (ulong)sum;
    }

    public async ValueTask<int> GetLengthAsync(CancellationToken ct)
    {
        if (_set.Length is { } l) return (int)l;
        var v = await NodeBase.ReadInt64FromAsync(_pLength!, ct).ConfigureAwait(false);
        if (v <= 0 || v > MaxLength)
            throw new GenApiException($"Register '{_owner.Name}' has an invalid length {v} from pLength '{_pLength!.Name}' (allowed 1..{MaxLength}).", _owner.Name);
        return (int)v;
    }

    /// <summary>레지스터 내용을 읽는다. 캐시가 유효하면(같은 주소·길이) 포트를 부르지 않는다. 돌려주는 배열은 호출자 소유다.</summary>
    public async ValueTask<byte[]> ReadAsync(CancellationToken ct, bool isFresh = false)
    {
        ThrowIfChunkPort();
        var addr = await ResolveAddressAsync(ct).ConfigureAwait(false);
        var len = await GetLengthAsync(ct).ConfigureAwait(false);
        var buf = new byte[len];
        if (!isFresh && TryCopyFromCache(addr, len, buf)) return buf;

        // 512 바이트를 넘는 길이도 포트 호출 한 번이다 — 포트가 GVCP 페이로드 단위로 나눈다.
        await Port.ReadAsync(addr, buf, ct).ConfigureAwait(false);
        if (!IsCacheable) return buf;
        StoreCache(addr, (byte[])buf.Clone());
        return buf;
    }

    /// <summary>
    /// 레지스터 내용을 호출자 버퍼에 채운다. 버퍼가 짧으면 포트를 부르기 전에 던진다 — 장치가 정한 길이만큼 읽어 놓고
    /// 버릴 일이 없어야 한다(길이를 pLength 로 부풀린 문서가 호스트에 헛읽기를 시키지 못한다).
    /// <para>
    /// 포트가 버퍼에 직접 채우므로, 읽기가 도중에 실패하면 버퍼 내용은 정해지지 않는다 — 새 바이트와 옛 바이트가 섞여 있을 수 있다.
    /// 던진 뒤의 버퍼를 읽지 않는 것은 호출자 몫이다.
    /// </para>
    /// </summary>
    public async ValueTask ReadIntoAsync(Memory<byte> buffer, CancellationToken ct)
    {
        ThrowIfChunkPort();
        var addr = await ResolveAddressAsync(ct).ConfigureAwait(false);
        var len = await GetLengthAsync(ct).ConfigureAwait(false);
        if (buffer.Length < len)
            throw new GenApiException($"Buffer of {buffer.Length} bytes is too small for register '{_owner.Name}' ({len} bytes).", _owner.Name);

        var dst = buffer.Slice(0, len);
        if (TryCopyFromCache(addr, len, dst)) return;
        await Port.ReadAsync(addr, dst, ct).ConfigureAwait(false);
        if (IsCacheable) StoreCache(addr, dst.ToArray());
    }

    /// <summary>캐시가 같은 주소·길이면 dst 로 옮기고 참. 캐시는 저장한 뒤 내용이 바뀌지 않으므로 잠금 안에서 복사하면 그만이다.</summary>
    private bool TryCopyFromCache(ulong addr, int len, Memory<byte> dst)
    {
        if (!IsCacheable) return false;
        lock (_lock)
        {
            if (_cache is null || _cacheAddr != addr || _cache.Length != len) return false;
            _cache.AsMemory().CopyTo(dst);
            return true;
        }
    }

    private void StoreCache(ulong addr, byte[] data)
    {
        lock (_lock)
        {
            _cache = data;
            _cacheAddr = addr;
        }
    }

    /// <summary>
    /// 읽기-수정-쓰기의 바탕값. 캐시가 유효하면 캐시, 아니면 읽는다. 쓰기 전용 레지스터는 읽을 수 없으므로 노드맵의 쓰기 그림자 —
    /// 이 주소에 마지막으로 쓴 바이트(같은 레지스터를 나눠 쓰는 다른 노드가 쓴 것 포함) — 를 바탕으로 삼고, 한 번도 쓰지 않은 바이트는 0 이다.
    /// </summary>
    public async ValueTask<byte[]> ReadForModifyAsync(CancellationToken ct)
    {
        if (_set.AccessMode != AccessMode.WriteOnly) return await ReadAsync(ct).ConfigureAwait(false);

        var addr = await ResolveAddressAsync(ct).ConfigureAwait(false);
        var len = await GetLengthAsync(ct).ConfigureAwait(false);
        var buf = new byte[len];
        _owner.Map.Shadow.Fill(addr, buf);
        return buf;
    }

    /// <summary>레지스터에 쓴다. 길이는 레지스터 길이와 같아야 한다. 캐시 정책을 적용하고, 쓴 바이트를 노드맵에 알려 그림자에 남기고 겹치는 다른 노드의 캐시를 버리게 한다.</summary>
    public async ValueTask WriteAsync(byte[] data, CancellationToken ct)
    {
        ThrowIfChunkPort();
        var addr = await ResolveAddressAsync(ct).ConfigureAwait(false);
        var len = await GetLengthAsync(ct).ConfigureAwait(false);
        if (data.Length != len)
            throw new GenApiException($"Register '{_owner.Name}' expects {len} bytes, got {data.Length}.", _owner.Name);

        await Port.WriteAsync(addr, data, ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (IsCacheable && _set.Cachable == Cachable.WriteThrough)
            {
                _cache = (byte[])data.Clone();
                _cacheAddr = addr;
            }
            else
            {
                _cache = null;
            }
        }
        _owner.Map.OnRegisterWritten(this, addr, data);
    }

    public void DropCache()
    {
        lock (_lock) _cache = null;
    }

    /// <summary>캐시가 [address, address+length) 와 겹치면 버리고 true.</summary>
    public bool DropIfOverlaps(ulong address, int length)
    {
        lock (_lock)
        {
            if (_cache is null) return false;
            var end = address + (ulong)length;
            var cacheEnd = _cacheAddr + (ulong)_cache.Length;
            if (address < cacheEnd && _cacheAddr < end)
            {
                _cache = null;
                return true;
            }
            return false;
        }
    }

    /// <summary>테스트·진단용: 캐시가 채워져 있는지.</summary>
    public bool HasCache
    {
        get { lock (_lock) return _cache is not null; }
    }
}
