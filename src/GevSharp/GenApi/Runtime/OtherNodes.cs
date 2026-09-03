using System.Text;
using GevSharp.GenApi.Model;

namespace GevSharp.GenApi.Runtime;

/// <summary>&lt;StringReg&gt; — 고정 길이 NUL 패딩 문자열 레지스터(UTF-8). 첫 NUL 에서 자르고, 쓸 때는 NUL 로 채운다. 길이를 넘는 문자열은 오류.</summary>
internal sealed class StringRegNode : NodeBase, IString
{
    private readonly RegisterCore _core;

    public StringRegNode(StringRegDef def, GenApiNodeMap map) : base(def, map)
    {
        _core = new RegisterCore(this, def.RegisterSet);
    }

    public override NodeKind Kind => NodeKind.String;
    internal RegisterCore Core => _core;
    internal override AccessMode IntrinsicAccessMode => _core.AccessMode;

    protected override void BindCore(NodeBinder binder) => _core.Bind(binder);

    /// <summary>청크 포트에 붙은 레지스터는 값 출처가 없다 — 장치 주소에서 읽은 숫자를 값인 양 내놓지 않는다.</summary>
    internal override ValueTask<string?> GetMissingValueReasonAsync(CancellationToken ct) => new(_core.ChunkPortReason);

    internal override void DropOwnCache() => _core.DropCache();

    public async ValueTask<string> GetAsync(CancellationToken ct = default)
    {
        await EnsureReadableAsync(ct).ConfigureAwait(false);
        return await ReadStringAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask SetAsync(string value, CancellationToken ct = default)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        await EnsureWritableAsync(ct).ConfigureAwait(false);
        await WriteStringAsync(value, ct).ConfigureAwait(false);
    }

    public async ValueTask<long> GetMaxLengthAsync(CancellationToken ct = default)
        => await _core.GetLengthAsync(ct).ConfigureAwait(false);

    internal override async ValueTask<string> ReadStringAsync(CancellationToken ct)
    {
        var bytes = await _core.ReadAsync(ct).ConfigureAwait(false);
        var n = Array.IndexOf(bytes, (byte)0);
        if (n < 0) n = bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, n);
    }

    internal override async ValueTask WriteStringAsync(string value, CancellationToken ct)
    {
        var len = await _core.GetLengthAsync(ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > len)
            throw new GenApiException($"String of {bytes.Length} bytes does not fit node '{Name}' (max {len} bytes).", Name);
        var buf = new byte[len];
        Buffer.BlockCopy(bytes, 0, buf, 0, bytes.Length);
        await _core.WriteAsync(buf, ct).ConfigureAwait(false);
        Map.OnWritten(this);
    }
}

/// <summary>&lt;String&gt; — 리터럴 값(쓰면 노드에 남는다) 또는 pValue 위임(String/StringReg).</summary>
internal sealed class StringNode : NodeBase, IString
{
    private readonly StringDef _def;
    private NodeBase? _pValue;
    private string _local;

    public StringNode(StringDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
        _local = def.Value ?? "";
    }

    public override NodeKind Kind => NodeKind.String;
    internal override NodeBase? ValueTarget => _pValue;

    protected override void BindCore(NodeBinder binder)
    {
        _pValue = binder.ResolveOptional(_def.PValue, RefKind.Value, "pValue", NodeBinder.Stringy);
    }

    public async ValueTask<string> GetAsync(CancellationToken ct = default)
    {
        await EnsureReadableAsync(ct).ConfigureAwait(false);
        return await ReadStringAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask SetAsync(string value, CancellationToken ct = default)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        await EnsureWritableAsync(ct).ConfigureAwait(false);
        await WriteStringAsync(value, ct).ConfigureAwait(false);
    }

    public ValueTask<long> GetMaxLengthAsync(CancellationToken ct = default)
        => _pValue is IString s ? s.GetMaxLengthAsync(ct) : new ValueTask<long>(int.MaxValue);

    internal override ValueTask<string> ReadStringAsync(CancellationToken ct)
        => _pValue is not null ? _pValue.ReadStringAsync(ct) : new ValueTask<string>(Volatile.Read(ref _local));

    internal override async ValueTask WriteStringAsync(string value, CancellationToken ct)
    {
        if (_pValue is not null) await _pValue.WriteStringAsync(value, ct).ConfigureAwait(false);
        else Volatile.Write(ref _local, value);
        Map.OnWritten(this);
    }
}

/// <summary>&lt;Boolean&gt; — 정수 노드(pValue) 위의 참/거짓. 참은 OnValue(기본 1), 거짓은 OffValue(기본 0)로 쓴다. 리터럴 Value 는 호스트 측 변수.</summary>
internal sealed class BooleanNode : NodeBase, IBoolean
{
    private readonly BooleanDef _def;
    private NodeBase? _pValue;
    private int _local;

    public BooleanNode(BooleanDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
        _local = def.Value == true ? 1 : 0;
    }

    public override NodeKind Kind => NodeKind.Boolean;
    internal override NodeBase? ValueTarget => _pValue;

    protected override void BindCore(NodeBinder binder)
    {
        _pValue = binder.ResolveOptional(_def.PValue, RefKind.Value, "pValue", NodeBinder.Numeric);
    }

    public async ValueTask<bool> GetAsync(CancellationToken ct = default)
    {
        await EnsureReadableAsync(ct).ConfigureAwait(false);
        return await ReadBoolAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask SetAsync(bool value, CancellationToken ct = default)
    {
        await EnsureWritableAsync(ct).ConfigureAwait(false);
        await WriteBoolAsync(value, ct).ConfigureAwait(false);
    }

    /// <summary>OnValue 면 참, OffValue 면 거짓, 둘 다 아니면 0 이 아닌지로 본다.</summary>
    internal async ValueTask<bool> ReadBoolAsync(CancellationToken ct)
    {
        if (_pValue is null) return Volatile.Read(ref _local) != 0;
        var v = await ReadInt64FromAsync(_pValue, ct).ConfigureAwait(false);
        if (v == _def.OnValue) return true;
        if (v == _def.OffValue) return false;
        return v != 0;
    }

    internal async ValueTask WriteBoolAsync(bool value, CancellationToken ct)
    {
        if (_pValue is not null)
            await _pValue.WriteValueAsync(new GenApiValue(value ? _def.OnValue : _def.OffValue), ct).ConfigureAwait(false);
        else
            Volatile.Write(ref _local, value ? 1 : 0);
        Map.OnWritten(this);
    }

    internal override ValueTask<GenApiValue> ReadValueAsync(CancellationToken ct) => ReadAsValueAsync(ct);

    private async ValueTask<GenApiValue> ReadAsValueAsync(CancellationToken ct)
        => GenApiValue.FromBoolean(await ReadBoolAsync(ct).ConfigureAwait(false));

    internal override ValueTask WriteValueAsync(GenApiValue value, CancellationToken ct) => WriteBoolAsync(value.IsNonZero, ct);

    /// <summary>
    /// 내부 값 경로가 주고받는 것은 참/거짓(1/0)이므로 한계값도 0..1, Inc 1 이다 — OnValue/OffValue 는 장치 쪽 표현이라
    /// 여기 실리지 않는다. 이걸 내놓지 않으면 Boolean 을 pValue 로 삼은 Converter 가 한계값을 못 물어 쓰기 자체가 막힌다.
    /// </summary>
    internal override ValueTask<GenApiValue> ReadLimitAsync(LimitKind kind, CancellationToken ct)
        => new(kind == LimitKind.Max ? GenApiValue.One : kind == LimitKind.Min ? GenApiValue.Zero : GenApiValue.One);
}

/// <summary>&lt;EnumEntry&gt; — 열거 항목. 값은 상수이며 자기 술어(pIsImplemented/pIsAvailable)를 가진다. 수식 변수로 쓰면 항목 값이 된다.</summary>
internal sealed class EnumEntryNode : NodeBase, IEnumEntry
{
    private readonly EnumEntryDef _def;

    public EnumEntryNode(EnumEntryDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
    }

    public override NodeKind Kind => NodeKind.EnumEntry;
    public string Symbolic => _def.Symbolic;
    public string EntryName => _def.EntryName;
    public long Value => _def.Value;
    public double? NumericValue => _def.NumericValue;
    public bool IsSelfClearing => _def.IsSelfClearing;
    internal override AccessMode IntrinsicAccessMode => AccessMode.ReadOnly;

    protected override void BindCore(NodeBinder binder) { }

    internal override ValueTask<GenApiValue> ReadValueAsync(CancellationToken ct) => new(new GenApiValue(_def.Value));
}

/// <summary>
/// &lt;Enumeration&gt; — 정수 값(pValue 또는 리터럴)에 항목 이름을 붙인 것. 읽은 값이 어느 항목에도 안 맞으면 <see cref="GenApiException"/>.
/// 항목을 고를 때는 그 항목의 구현·가용 술어도 검사한다. pSelected 가 있으면 셀렉터로 동작한다(무효화는 노드맵이 맡는다).
/// </summary>
internal sealed class EnumerationNode : NodeBase, IEnumeration
{
    private readonly EnumerationDef _def;
    private EnumEntryNode[] _entries = Array.Empty<EnumEntryNode>();
    private IReadOnlyList<IEnumEntry> _entryList = Array.Empty<IEnumEntry>();
    private NodeBase? _pValue;
    private long _local;

    public EnumerationNode(EnumerationDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
        _local = def.Value ?? (def.Entries.Count > 0 ? def.Entries[0].Value : 0);
    }

    public override NodeKind Kind => NodeKind.Enumeration;
    public IReadOnlyList<IEnumEntry> Entries => _entryList;
    internal override NodeBase? ValueTarget => _pValue;

    protected override void BindCore(NodeBinder binder)
    {
        var entries = new EnumEntryNode[_def.Entries.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = binder.NodeOf(_def.Entries[i]) as EnumEntryNode
                ?? throw new GenApiException($"Entry '{_def.Entries[i].Name}' of enumeration '{Name}' is not an EnumEntry node.", Name);
        }
        _entries = entries;
        _entryList = Array.AsReadOnly<IEnumEntry>(entries);
        _pValue = binder.ResolveOptional(_def.PValue, RefKind.Value, "pValue", NodeBinder.Numeric);
    }

    /// <summary>Symbolic 으로, 없으면 항목 이름으로 찾는다.</summary>
    internal EnumEntryNode? FindEntry(string symbolic)
    {
        foreach (var e in _entries)
        {
            if (string.Equals(e.Symbolic, symbolic, StringComparison.Ordinal)) return e;
        }
        foreach (var e in _entries)
        {
            if (string.Equals(e.EntryName, symbolic, StringComparison.Ordinal)) return e;
        }
        return null;
    }

    private EnumEntryNode? FindByValue(long value)
    {
        foreach (var e in _entries)
        {
            if (e.Value == value) return e;
        }
        return null;
    }

    /// <summary>
    /// 값이 같은 항목이 둘 이상일 때(벤더 XML 에 실제로 있다 — 옛 이름과 새 이름이 같은 값을 쓰고 각자 다른 존재 여부 술어를 단다)
    /// 지금 구현·가용한 쪽을 고른다. 그런 항목이 없으면 첫 번째로 되돌아간다 — 값 자체는 유효하므로 이름을 못 붙이는 것보다 낫다.
    /// 값이 하나뿐인 흔한 경우에는 술어를 한 번도 읽지 않는다(레지스터 왕복이 늘지 않는다).
    /// </summary>
    private async ValueTask<EnumEntryNode?> FindByValueAsync(long value, CancellationToken ct)
    {
        EnumEntryNode? first = null;
        var duplicates = 0;
        foreach (var e in _entries)
        {
            if (e.Value != value) continue;
            first ??= e;
            duplicates++;
        }
        if (first is null || duplicates == 1) return first;

        foreach (var e in _entries)
        {
            if (e.Value != value) continue;
            if (await e.IsImplementedAsync(ct).ConfigureAwait(false) && await e.IsAvailableAsync(ct).ConfigureAwait(false))
                return e;
        }
        return first;
    }

    public IEnumEntry? GetEntry(string symbolic) => symbolic is null ? null : FindEntry(symbolic);

    public async ValueTask<string> GetAsync(CancellationToken ct = default)
    {
        await EnsureReadableAsync(ct).ConfigureAwait(false);
        var v = await ReadInt64Async(ct).ConfigureAwait(false);
        return (await FindByValueAsync(v, ct).ConfigureAwait(false) ?? throw NoEntryFor(v)).Symbolic;
    }

    public async ValueTask SetAsync(string symbolic, CancellationToken ct = default)
    {
        if (symbolic is null) throw new ArgumentNullException(nameof(symbolic));
        var entry = FindEntry(symbolic)
            ?? throw new GenApiException($"Enumeration '{Name}' has no entry '{symbolic}' (entries: {EntryNames()}).", Name);
        await SetEntryAsync(entry, ct).ConfigureAwait(false);
    }

    public async ValueTask<long> GetIntValueAsync(CancellationToken ct = default)
    {
        await EnsureReadableAsync(ct).ConfigureAwait(false);
        return await ReadInt64Async(ct).ConfigureAwait(false);
    }

    public async ValueTask SetIntValueAsync(long value, CancellationToken ct = default)
    {
        var entry = await FindByValueAsync(value, ct).ConfigureAwait(false) ?? throw NoEntryFor(value);
        await SetEntryAsync(entry, ct).ConfigureAwait(false);
    }

    private async ValueTask SetEntryAsync(EnumEntryNode entry, CancellationToken ct)
    {
        await EnsureWritableAsync(ct).ConfigureAwait(false);
        if (!await entry.IsImplementedAsync(ct).ConfigureAwait(false))
            throw new GenApiException($"Entry '{entry.Symbolic}' of enumeration '{Name}' is not implemented.", Name);
        if (!await entry.IsAvailableAsync(ct).ConfigureAwait(false))
            throw new GenApiException($"Entry '{entry.Symbolic}' of enumeration '{Name}' is not available.", Name);
        await WriteInt64Async(entry.Value, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<IEnumEntry>> GetAvailableEntriesAsync(CancellationToken ct = default)
    {
        var list = new List<IEnumEntry>(_entries.Length);
        foreach (var e in _entries)
        {
            if (await e.IsImplementedAsync(ct).ConfigureAwait(false) && await e.IsAvailableAsync(ct).ConfigureAwait(false))
                list.Add(e);
        }
        return list;
    }

    internal async ValueTask<long> ReadInt64Async(CancellationToken ct)
        => _pValue is not null ? await ReadInt64FromAsync(_pValue, ct).ConfigureAwait(false) : Interlocked.Read(ref _local);

    /// <summary>항목이 있는 값만 받는다(내부 사슬을 통한 쓰기도 마찬가지).</summary>
    internal async ValueTask WriteInt64Async(long value, CancellationToken ct)
    {
        if (await FindByValueAsync(value, ct).ConfigureAwait(false) is null) throw NoEntryFor(value);
        if (_pValue is not null) await _pValue.WriteValueAsync(new GenApiValue(value), ct).ConfigureAwait(false);
        else Interlocked.Exchange(ref _local, value);
        Map.OnWritten(this);
    }

    internal override ValueTask<GenApiValue> ReadValueAsync(CancellationToken ct) => ReadAsValueAsync(ct);

    private async ValueTask<GenApiValue> ReadAsValueAsync(CancellationToken ct)
        => new GenApiValue(await ReadInt64Async(ct).ConfigureAwait(false));

    internal override ValueTask WriteValueAsync(GenApiValue value, CancellationToken ct)
        => WriteInt64Async(NumericCodec.ToInt64(value, Name), ct);

    /// <summary>
    /// 항목 값이 곧 이 노드의 값이므로 한계값은 가장 작은·가장 큰 항목 값이고 Inc 는 1 이다(값이 띄엄띄엄해도 쓰기는
    /// 항목 존재로 다시 걸린다). 이걸 내놓지 않으면 Enumeration 을 pValue 로 삼은 Converter 가 한계값을 못 물어 쓰기가 막힌다.
    /// </summary>
    internal override ValueTask<GenApiValue> ReadLimitAsync(LimitKind kind, CancellationToken ct)
    {
        if (kind == LimitKind.Inc) return new ValueTask<GenApiValue>(GenApiValue.One);
        if (_entries.Length == 0)
            throw new GenApiException($"Enumeration '{Name}' has no entries, so it has no {kind}.", Name);
        var min = long.MaxValue;
        var max = long.MinValue;
        foreach (var e in _entries)
        {
            if (e.Value < min) min = e.Value;
            if (e.Value > max) max = e.Value;
        }
        return new ValueTask<GenApiValue>(new GenApiValue(kind == LimitKind.Min ? min : max));
    }

    private GenApiException NoEntryFor(long value)
        => new($"Enumeration '{Name}' holds value {value}, which matches no entry (entries: {EntryNames()}).", Name);

    private string EntryNames()
    {
        var parts = new string[_entries.Length];
        for (var i = 0; i < parts.Length; i++) parts[i] = $"{_entries[i].Symbolic}={_entries[i].Value}";
        return string.Join(", ", parts);
    }
}

/// <summary>
/// &lt;Command&gt; — 실행은 CommandValue(또는 pCommandValue 값, 둘 다 없으면 1)를 pValue 에 쓰는 것. pValue 가 없으면 리터럴 Value 자리의
/// 호스트 측 변수에 남는다(Integer/Boolean 의 리터럴 Value 와 같은 규칙).
/// PollingTime 이 있으면 <see cref="IsDoneAsync"/> 가 pValue 를 새로 읽어 명령 값에서 돌아왔는지 본다(자기 소거 비트); 없으면 항상 완료.
/// </summary>
internal sealed class CommandNode : NodeBase, ICommand
{
    private readonly CommandDef _def;
    private NodeBase? _pValue;
    private NodeBase? _pCommandValue;
    private long _local;

    public CommandNode(CommandDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
        _local = def.Value ?? 0;
    }

    public override NodeKind Kind => NodeKind.Command;
    internal override NodeBase? ValueTarget => _pValue;

    protected override void BindCore(NodeBinder binder)
    {
        _pValue = binder.ResolveOptional(_def.PValue, RefKind.Value, "pValue", NodeBinder.Numeric);
        _pCommandValue = binder.ResolveOptional(_def.PCommandValue, RefKind.Value, "pCommandValue", NodeBinder.Numeric);
    }

    private async ValueTask<long> CommandValueAsync(CancellationToken ct)
    {
        if (_def.CommandValue is { } v) return v;
        if (_pCommandValue is not null) return await ReadInt64FromAsync(_pCommandValue, ct).ConfigureAwait(false);
        return 1;
    }

    public async ValueTask ExecuteAsync(CancellationToken ct = default)
    {
        await EnsureWritableAsync(ct).ConfigureAwait(false);
        var value = await CommandValueAsync(ct).ConfigureAwait(false);
        if (_pValue is not null) await _pValue.WriteValueAsync(new GenApiValue(value), ct).ConfigureAwait(false);
        else Interlocked.Exchange(ref _local, value);
        Map.OnWritten(this);
    }

    public async ValueTask<bool> IsDoneAsync(CancellationToken ct = default)
    {
        if (_def.PollingTimeMs is null || _pValue is null) return true;
        DropCacheChain(_pValue);
        var current = await ReadInt64FromAsync(_pValue, ct).ConfigureAwait(false);
        var command = await CommandValueAsync(ct).ConfigureAwait(false);
        return current != command;
    }

    internal override ValueTask<GenApiValue> ReadValueAsync(CancellationToken ct)
        => _pValue is not null ? _pValue.ReadValueAsync(ct) : new ValueTask<GenApiValue>(new GenApiValue(Interlocked.Read(ref _local)));

    internal override ValueTask WriteValueAsync(GenApiValue value, CancellationToken ct)
    {
        if (_pValue is not null) return _pValue.WriteValueAsync(value, ct);
        Interlocked.Exchange(ref _local, NumericCodec.ToInt64(value, Name));
        return default;
    }
}

/// <summary>&lt;Register&gt; — 원시 바이트 레지스터. 길이는 레지스터 길이 그대로이며 512 바이트를 넘어도 포트 호출 한 번이다.</summary>
internal sealed class RegisterNode : NodeBase, IRegister
{
    private readonly RegisterCore _core;

    public RegisterNode(RegisterDef def, GenApiNodeMap map) : base(def, map)
    {
        _core = new RegisterCore(this, def.RegisterSet);
    }

    public override NodeKind Kind => NodeKind.Register;
    internal RegisterCore Core => _core;
    internal override AccessMode IntrinsicAccessMode => _core.AccessMode;

    protected override void BindCore(NodeBinder binder) => _core.Bind(binder);

    /// <summary>청크 포트에 붙은 레지스터는 값 출처가 없다 — 장치 주소에서 읽은 바이트를 값인 양 내놓지 않는다.</summary>
    internal override ValueTask<string?> GetMissingValueReasonAsync(CancellationToken ct) => new(_core.ChunkPortReason);

    internal override void DropOwnCache() => _core.DropCache();

    public ValueTask<ulong> GetAddressAsync(CancellationToken ct = default) => _core.ResolveAddressAsync(ct);

    public async ValueTask<long> GetLengthAsync(CancellationToken ct = default)
        => await _core.GetLengthAsync(ct).ConfigureAwait(false);

    /// <summary>버퍼 길이는 장치를 읽기 전에 확인한다 — 짧은 버퍼로 부른 호출이 왕복과 할당을 먼저 치르고 던지지 않게.</summary>
    public async ValueTask GetAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        await EnsureReadableAsync(ct).ConfigureAwait(false);
        await _core.ReadIntoAsync(buffer, ct).ConfigureAwait(false);
    }

    public async ValueTask SetAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await EnsureWritableAsync(ct).ConfigureAwait(false);
        await _core.WriteAsync(data.ToArray(), ct).ConfigureAwait(false);
        Map.OnWritten(this);
    }
}

/// <summary>&lt;Category&gt; — pFeature 순서 그대로의 피처 목록. 빠진 피처 이름은 바인딩 오류.</summary>
internal sealed class CategoryNode : NodeBase, ICategory
{
    private readonly CategoryDef _def;
    private IReadOnlyList<INode> _features = Array.Empty<INode>();

    public CategoryNode(CategoryDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
    }

    public override NodeKind Kind => NodeKind.Category;
    public IReadOnlyList<INode> Features => _features;
    internal override AccessMode IntrinsicAccessMode => AccessMode.ReadOnly;

    protected override void BindCore(NodeBinder binder)
    {
        var features = new INode[_def.PFeatures.Count];
        for (var i = 0; i < features.Length; i++)
            features[i] = binder.Resolve(_def.PFeatures[i], RefKind.Feature, "pFeature", NodeBinder.Any);
        _features = features;
    }
}

/// <summary>
/// &lt;Port&gt; — 레지스터 노드가 붙는 전송 경계. 대부분은 장치 레지스터 공간이지만, ChunkID/pChunkID 가 달린 포트는
/// "값이 장치가 아니라 프레임에 실려 온다" 는 선언이다. 그 배선이 없으므로 그런 포트에 붙은 노드는 값이 없는 것으로 다룬다
/// (<see cref="IsChunkPort"/>) — 장치 주소에서 읽어 그럴듯한 숫자를 내놓으면 쓰는 쪽이 그것을 믿는다.
/// </summary>
internal sealed class PortNode : NodeBase, IPortNode
{
    private readonly PortDef _def;

    public PortNode(PortDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
    }

    public override NodeKind Kind => NodeKind.Port;
    public IGevPort Port => Map.Port;

    /// <summary>이 포트의 값은 프레임의 청크 데이터에 있다 — 장치 레지스터 공간이 아니다.</summary>
    public bool IsChunkPort => _def.ChunkId is not null || _def.PChunkId is not null;

    protected override void BindCore(NodeBinder binder)
    {
        if (IsChunkPort)
        {
            GevLog.Debug("GenApi.Runtime", $"Port '{Name}': values come from chunk data (ChunkID/pChunkID), which this library does not deliver; nodes on this port report not-available.");
        }
        else if (_def.IsEndianessSwapped || _def.IsChunkDataCached)
        {
            GevLog.Debug("GenApi.Runtime", $"Port '{Name}': SwapEndianess/CacheChunkData are ignored; the port maps to the device register space.");
        }
    }
}

/// <summary>제네릭 &lt;Node&gt; 와 스키마에 없는 요소의 자리표시자. 값이 없고 술어만 동작한다.</summary>
internal sealed class GenericNode : NodeBase
{
    public GenericNode(NodeDef def, GenApiNodeMap map) : base(def, map) { }

    public override NodeKind Kind => NodeKind.Unknown;
    internal override AccessMode IntrinsicAccessMode => AccessMode.ReadOnly;

    protected override void BindCore(NodeBinder binder) { }
}
