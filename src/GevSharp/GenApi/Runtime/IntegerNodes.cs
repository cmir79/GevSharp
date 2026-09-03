using GevSharp.GenApi.Model;

namespace GevSharp.GenApi.Runtime;

/// <summary>
/// <see cref="IInteger"/> 로 노출되는 다섯 종류의 공통 면. 공개 GetAsync/SetAsync 는 접근 모드를 검사하고,
/// 내부 <see cref="ReadInt64Async"/>/<see cref="WriteInt64Async"/> 는 검사 없이 값만 옮긴다.
/// 쓰기는 Min..Max 범위와 Inc 격자, ValidValueSet 을 검사해 어긋나면 <see cref="GenApiException"/> — 조용히 자르지 않는다.
/// </summary>
internal abstract class IntegerNodeBase : NodeBase, IInteger
{
    private readonly IntegerBaseDef _baseDef;

    protected IntegerNodeBase(IntegerBaseDef def, GenApiNodeMap map) : base(def, map)
    {
        _baseDef = def;
    }

    public override NodeKind Kind => NodeKind.Integer;
    public Representation Representation => _baseDef.Representation ?? Representation.PureNumber;
    public string? Unit => _baseDef.Unit;

    public async ValueTask<long> GetAsync(CancellationToken ct = default)
    {
        await EnsureReadableAsync(ct).ConfigureAwait(false);
        return await ReadInt64Async(ct).ConfigureAwait(false);
    }

    public async ValueTask SetAsync(long value, CancellationToken ct = default)
    {
        await EnsureWritableAsync(ct).ConfigureAwait(false);
        await WriteInt64Async(value, ct).ConfigureAwait(false);
    }

    public abstract ValueTask<long> GetMinAsync(CancellationToken ct = default);
    public abstract ValueTask<long> GetMaxAsync(CancellationToken ct = default);
    public abstract ValueTask<long> GetIncAsync(CancellationToken ct = default);

    internal abstract ValueTask<long> ReadInt64Async(CancellationToken ct);

    /// <summary>검사를 통과한 값을 실제로 쓴다.</summary>
    protected abstract ValueTask WriteCoreAsync(long value, CancellationToken ct);

    internal async ValueTask WriteInt64Async(long value, CancellationToken ct)
    {
        await ValidateAsync(value, ct).ConfigureAwait(false);
        await WriteCoreAsync(value, ct).ConfigureAwait(false);
        Map.OnWritten(this);
    }

    protected virtual async ValueTask ValidateAsync(long value, CancellationToken ct)
    {
        var min = await GetMinAsync(ct).ConfigureAwait(false);
        var max = await GetMaxAsync(ct).ConfigureAwait(false);
        if (value < min || value > max)
            throw new GenApiException($"Value {value} for node '{Name}' is outside the range {min}..{max}.", Name);

        var inc = await GetIncAsync(ct).ConfigureAwait(false);
        if (inc > 1 && !IsOnGrid(value, min, inc))
        {
            // 격자를 아는 것은 이 노드뿐이다. 변환 노드를 통해 들어온 쪽은 간격을 볼 수 없으므로 값으로도 실어 보낸다.
            var grid = new GenApiException($"Value {value} for node '{Name}' is not on the increment grid (min {min}, inc {inc}).", Name);
            grid.Data[GenApiException.GridAnchorKey] = min == long.MinValue ? 0L : min;
            grid.Data[GenApiException.GridIncrementKey] = inc;
            throw grid;
        }

        if (_baseDef.ValidValueSet is { } set)
        {
            var found = false;
            foreach (var v in set)
            {
                if (v == value)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                throw new GenApiException($"Value {value} for node '{Name}' is not in the valid value set [{string.Join(", ", set)}].", Name);
        }
    }

    /// <summary>
    /// (value − min) 이 inc 의 배수인지. 뺄셈 대신 나머지끼리 비교해 넘침 없이 판정하고, Min 이 정해지지 않은 노드(long.MinValue)는
    /// 0 을 기준으로 잡는다 — Inc 가 있는데 Min 이 없다고 격자 검사를 건너뛰지 않는다.
    /// </summary>
    internal static bool IsOnGrid(long value, long min, long inc)
    {
        var anchor = min == long.MinValue ? 0 : min % inc;
        return (value % inc - anchor) % inc == 0;
    }

    internal override ValueTask<GenApiValue> ReadValueAsync(CancellationToken ct) => ReadAsValueAsync(ct);

    private async ValueTask<GenApiValue> ReadAsValueAsync(CancellationToken ct)
        => new GenApiValue(await ReadInt64Async(ct).ConfigureAwait(false));

    internal override ValueTask WriteValueAsync(GenApiValue value, CancellationToken ct)
        => WriteInt64Async(NumericCodec.ToInt64(value, Name), ct);

    internal override async ValueTask<GenApiValue> ReadLimitAsync(LimitKind kind, CancellationToken ct) => kind switch
    {
        LimitKind.Min => new GenApiValue(await GetMinAsync(ct).ConfigureAwait(false)),
        LimitKind.Max => new GenApiValue(await GetMaxAsync(ct).ConfigureAwait(false)),
        _ => new GenApiValue(await GetIncAsync(ct).ConfigureAwait(false)),
    };
}

/// <summary>
/// &lt;Integer&gt; — 리터럴 값(호스트 측 변수, 쓰면 노드에 남는다), pValue 위임, 또는 pIndex 로 고른 pValueIndexed/ValueIndexed.
/// Min/Max/Inc 는 리터럴 → pMin/pMax/pInc → 위임 대상 정수 노드의 값 → (long.MinValue, long.MaxValue, 1) 순으로 정한다.
/// pValueCopy 대상에는 쓰기마다 같은 값을 더 써 넣는다.
/// </summary>
internal sealed class IntegerNode : IntegerNodeBase
{
    private readonly IntegerDef _def;
    private NodeBase? _pValue;
    private NodeBase[] _copies = Array.Empty<NodeBase>();
    private NodeBase? _pIndex;
    private IndexedSlot[] _slots = Array.Empty<IndexedSlot>();
    private NodeBase? _pValueDefault;
    private NodeBase? _pMin;
    private NodeBase? _pMax;
    private NodeBase? _pInc;
    private long _local;

    private readonly struct IndexedSlot
    {
        public IndexedSlot(long index, NodeBase? node, long literal)
        {
            Index = index;
            Node = node;
            Literal = literal;
        }

        public long Index { get; }
        public NodeBase? Node { get; }
        public long Literal { get; }
    }

    public IntegerNode(IntegerDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
        _local = def.Value ?? def.ValueDefault ?? 0;
    }

    protected override void BindCore(NodeBinder binder)
    {
        _pValue = binder.ResolveOptional(_def.PValue, RefKind.Value, "pValue", NodeBinder.Numeric);
        _pIndex = binder.ResolveOptional(_def.PIndex, RefKind.Value, "pIndex", NodeBinder.Numeric);
        _pValueDefault = binder.ResolveOptional(_def.PValueDefault, RefKind.Value, "pValueDefault", NodeBinder.Numeric);
        _pMin = binder.ResolveOptional(_def.PMin, RefKind.Limit, "pMin", NodeBinder.Numeric);
        _pMax = binder.ResolveOptional(_def.PMax, RefKind.Limit, "pMax", NodeBinder.Numeric);
        _pInc = binder.ResolveOptional(_def.PInc, RefKind.Limit, "pInc", NodeBinder.Numeric);

        var copies = new NodeBase[_def.PValueCopies.Count];
        for (var i = 0; i < copies.Length; i++)
            copies[i] = binder.Resolve(_def.PValueCopies[i], RefKind.Value, "pValueCopy", NodeBinder.Numeric);
        _copies = copies;

        var slots = new List<IndexedSlot>();
        foreach (var p in _def.PValueIndexed)
            slots.Add(new IndexedSlot(p.Index, binder.Resolve(p.PNode, RefKind.Value, $"pValueIndexed Index=\"{p.Index}\"", NodeBinder.Numeric), 0));
        foreach (var v in _def.ValueIndexed)
            slots.Add(new IndexedSlot(v.Index, null, v.Value));
        _slots = slots.ToArray();
    }

    internal override NodeBase? ValueTarget => _pValue ?? _pValueDefault;

    internal override void CollectValueTargets(List<NodeBase> into)
    {
        if (_pValue is not null) into.Add(_pValue);
        if (_pValueDefault is not null) into.Add(_pValueDefault);
        foreach (var s in _slots)
        {
            if (s.Node is not null) into.Add(s.Node);
        }
    }

    internal override async ValueTask<NodeBase?> GetAccessTargetAsync(CancellationToken ct)
    {
        if (_pValue is not null) return _pValue;
        if (_pIndex is null) return _pValueDefault;
        return (await TrySelectSlotAsync(ct).ConfigureAwait(false)).Slot?.Node;
    }

    internal override async ValueTask<string?> GetMissingValueReasonAsync(CancellationToken ct)
    {
        if (_pValue is not null || _pIndex is null) return null;
        var (slot, index) = await TrySelectSlotAsync(ct).ConfigureAwait(false);
        return slot is null ? NoSlotReason(index) : null;
    }

    internal override async ValueTask<long> ReadInt64Async(CancellationToken ct)
    {
        if (_pValue is not null) return await ReadInt64FromAsync(_pValue, ct).ConfigureAwait(false);
        if (_pIndex is not null)
        {
            var slot = await SelectSlotAsync(ct).ConfigureAwait(false);
            return slot.Node is null ? slot.Literal : await ReadInt64FromAsync(slot.Node, ct).ConfigureAwait(false);
        }
        // pIndex 없이 pValueDefault 만 있는 정의 — 기본 노드가 곧 값 출처다
        if (_pValueDefault is not null) return await ReadInt64FromAsync(_pValueDefault, ct).ConfigureAwait(false);
        return Interlocked.Read(ref _local);
    }

    protected override async ValueTask WriteCoreAsync(long value, CancellationToken ct)
    {
        if (_pValue is not null)
        {
            await _pValue.WriteValueAsync(new GenApiValue(value), ct).ConfigureAwait(false);
        }
        else if (_pIndex is not null)
        {
            var slot = await SelectSlotAsync(ct).ConfigureAwait(false);
            if (slot.Node is null)
                throw new GenApiException($"Node '{Name}' maps index {slot.Index} to a literal value and cannot be written.", Name);
            await slot.Node.WriteValueAsync(new GenApiValue(value), ct).ConfigureAwait(false);
        }
        else if (_pValueDefault is not null)
        {
            await _pValueDefault.WriteValueAsync(new GenApiValue(value), ct).ConfigureAwait(false);
        }
        else
        {
            Interlocked.Exchange(ref _local, value);
        }
        foreach (var copy in _copies) await copy.WriteValueAsync(new GenApiValue(value), ct).ConfigureAwait(false);
    }

    /// <summary>pIndex 값에 맞는 슬롯(없으면 기본값 슬롯). 아무것도 없으면 Slot 이 null — 접근 모드 조회는 예외 대신 NotAvailable 로 답한다.</summary>
    private async ValueTask<(IndexedSlot? Slot, long Index)> TrySelectSlotAsync(CancellationToken ct)
    {
        var index = await ReadInt64FromAsync(_pIndex!, ct).ConfigureAwait(false);
        foreach (var s in _slots)
        {
            if (s.Index == index) return (s, index);
        }
        if (_pValueDefault is not null) return (new IndexedSlot(index, _pValueDefault, 0), index);
        if (_def.ValueDefault is { } d) return (new IndexedSlot(index, null, d), index);
        return (null, index);
    }

    private async ValueTask<IndexedSlot> SelectSlotAsync(CancellationToken ct)
    {
        var (slot, index) = await TrySelectSlotAsync(ct).ConfigureAwait(false);
        return slot ?? throw new GenApiException($"Node '{Name}' has {NoSlotReason(index)}.", Name);
    }

    private string NoSlotReason(long index) => $"no value for index {index} of '{_pIndex!.Name}' and no default";

    private async ValueTask<IntegerNodeBase?> TargetIntegerAsync(CancellationToken ct)
        => await GetAccessTargetAsync(ct).ConfigureAwait(false) as IntegerNodeBase;

    public override async ValueTask<long> GetMinAsync(CancellationToken ct = default)
    {
        if (_def.Min is { } m) return m;
        if (_pMin is not null) return await ReadInt64FromAsync(_pMin, ct).ConfigureAwait(false);
        var target = await TargetIntegerAsync(ct).ConfigureAwait(false);
        return target is null ? long.MinValue : await target.GetMinAsync(ct).ConfigureAwait(false);
    }

    public override async ValueTask<long> GetMaxAsync(CancellationToken ct = default)
    {
        if (_def.Max is { } m) return m;
        if (_pMax is not null) return await ReadInt64FromAsync(_pMax, ct).ConfigureAwait(false);
        var target = await TargetIntegerAsync(ct).ConfigureAwait(false);
        return target is null ? long.MaxValue : await target.GetMaxAsync(ct).ConfigureAwait(false);
    }

    public override async ValueTask<long> GetIncAsync(CancellationToken ct = default)
    {
        if (_def.Inc is { } i) return i;
        if (_pInc is not null) return await ReadInt64FromAsync(_pInc, ct).ConfigureAwait(false);
        var target = await TargetIntegerAsync(ct).ConfigureAwait(false);
        return target is null ? 1 : await target.GetIncAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>&lt;IntReg&gt; — 길이 1..8 바이트의 정수 레지스터. Endianess 로 바이트 순서를, Sign 으로 부호 확장을 정한다.</summary>
internal sealed class IntRegNode : IntegerNodeBase
{
    private readonly IntRegDef _def;
    private readonly RegisterCore _core;

    public IntRegNode(IntRegDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
        _core = new RegisterCore(this, def.RegisterSet);
    }

    internal RegisterCore Core => _core;
    internal override AccessMode IntrinsicAccessMode => _core.AccessMode;

    protected override void BindCore(NodeBinder binder)
    {
        _core.Bind(binder);
        if (_core.StaticLength is { } len) CheckLength(len);
    }

    /// <summary>청크 포트에 붙은 레지스터는 값 출처가 없다 — 장치 주소에서 읽은 숫자를 값인 양 내놓지 않는다.</summary>
    internal override ValueTask<string?> GetMissingValueReasonAsync(CancellationToken ct) => new(_core.ChunkPortReason);


    internal override void DropOwnCache() => _core.DropCache();

    private void CheckLength(int length)
    {
        if (length < 1 || length > 8)
            throw new GenApiException($"IntReg '{Name}' has length {length}; an integer register must be 1..8 bytes.", Name);
    }

    internal override async ValueTask<long> ReadInt64Async(CancellationToken ct)
    {
        var bytes = await _core.ReadAsync(ct).ConfigureAwait(false);
        CheckLength(bytes.Length);
        return NumericCodec.DecodeInt64(bytes, bytes.Length, _def.Endianess, _def.Sign, Name);
    }

    protected override async ValueTask WriteCoreAsync(long value, CancellationToken ct)
    {
        var len = await _core.GetLengthAsync(ct).ConfigureAwait(false);
        CheckLength(len);
        var bytes = new byte[len];
        NumericCodec.EncodeInt64(value, bytes, len, _def.Endianess);
        await _core.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async ValueTask<int> BitsAsync(CancellationToken ct)
    {
        var len = _core.StaticLength ?? await _core.GetLengthAsync(ct).ConfigureAwait(false);
        CheckLength(len);
        return len * 8;
    }

    public override async ValueTask<long> GetMinAsync(CancellationToken ct = default)
        => NumericCodec.MinOfWidth(await BitsAsync(ct).ConfigureAwait(false), _def.Sign);

    public override async ValueTask<long> GetMaxAsync(CancellationToken ct = default)
        => NumericCodec.MaxOfWidth(await BitsAsync(ct).ConfigureAwait(false), _def.Sign);

    public override ValueTask<long> GetIncAsync(CancellationToken ct = default) => new(1L);
}

/// <summary>
/// &lt;MaskedIntReg&gt;/StructReg 항목 — 레지스터 안의 비트 필드. LSB/MSB(또는 Bit)는 레지스터 자체의 비트 번호다:
/// BigEndian 레지스터는 비트 0 이 레지스터 전체의 최상위 비트, LittleEndian 은 최하위 비트. 바이트 순서를 풀어 정수로 만든 뒤
/// 그 정수 위의 (shift, width) 로 정규화한다. 쓰기는 읽기-수정-쓰기로 다른 비트를 보존한다.
/// </summary>
internal sealed class MaskedIntRegNode : IntegerNodeBase
{
    private readonly MaskedIntRegDef _def;
    private readonly RegisterCore _core;

    public MaskedIntRegNode(MaskedIntRegDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
        _core = new RegisterCore(this, def.RegisterSet);
    }

    internal RegisterCore Core => _core;
    internal override AccessMode IntrinsicAccessMode => _core.AccessMode;

    protected override void BindCore(NodeBinder binder)
    {
        _core.Bind(binder);
        if (_core.StaticLength is { } len) FieldOf(len);
    }

    /// <summary>청크 포트에 붙은 레지스터는 값 출처가 없다 — 장치 주소에서 읽은 숫자를 값인 양 내놓지 않는다.</summary>
    internal override ValueTask<string?> GetMissingValueReasonAsync(CancellationToken ct) => new(_core.ChunkPortReason);


    internal override void DropOwnCache() => _core.DropCache();

    /// <summary>바이트 길이에 대해 (shift, width) 를 계산한다. 범위를 벗어난 비트 번호는 오류.</summary>
    private (int Shift, int Width) FieldOf(int lengthBytes)
    {
        if (lengthBytes < 1 || lengthBytes > 8)
            throw new GenApiException($"MaskedIntReg '{Name}' has length {lengthBytes}; a masked register must be 1..8 bytes.", Name);
        var bits = lengthBytes * 8;
        var a = _def.Lsb;
        var b = _def.Msb;
        if (a < 0 || b < 0 || a >= bits || b >= bits)
            throw new GenApiException($"Bit range LSB {_def.Lsb}..MSB {_def.Msb} of node '{Name}' exceeds the {bits}-bit register.", Name);
        if (_def.Endianess == Endianess.BigEndian)
        {
            a = bits - 1 - a;
            b = bits - 1 - b;
        }
        return (Math.Min(a, b), Math.Abs(a - b) + 1);
    }

    internal override async ValueTask<long> ReadInt64Async(CancellationToken ct)
    {
        var bytes = await _core.ReadAsync(ct).ConfigureAwait(false);
        var (shift, width) = FieldOf(bytes.Length);
        var raw = NumericCodec.DecodeUnsigned(bytes, bytes.Length, _def.Endianess);
        var field = (raw >> shift) & NumericCodec.MaskOfWidth(width);
        return NumericCodec.SignExtend(field, width, _def.Sign, Name);
    }

    protected override async ValueTask WriteCoreAsync(long value, CancellationToken ct)
    {
        var bytes = await _core.ReadForModifyAsync(ct).ConfigureAwait(false);
        var (shift, width) = FieldOf(bytes.Length);
        var raw = NumericCodec.DecodeUnsigned(bytes, bytes.Length, _def.Endianess);
        var mask = NumericCodec.MaskOfWidth(width) << shift;
        raw = (raw & ~mask) | ((unchecked((ulong)value) << shift) & mask);
        var dst = new byte[bytes.Length];
        NumericCodec.EncodeInt64(unchecked((long)raw), dst, dst.Length, _def.Endianess);
        await _core.WriteAsync(dst, ct).ConfigureAwait(false);
    }

    private async ValueTask<int> WidthAsync(CancellationToken ct)
    {
        var len = _core.StaticLength ?? await _core.GetLengthAsync(ct).ConfigureAwait(false);
        return FieldOf(len).Width;
    }

    public override async ValueTask<long> GetMinAsync(CancellationToken ct = default)
        => NumericCodec.MinOfWidth(await WidthAsync(ct).ConfigureAwait(false), _def.Sign);

    public override async ValueTask<long> GetMaxAsync(CancellationToken ct = default)
        => NumericCodec.MaxOfWidth(await WidthAsync(ct).ConfigureAwait(false), _def.Sign);

    public override ValueTask<long> GetIncAsync(CancellationToken ct = default) => new(1L);
}

/// <summary>&lt;IntSwissKnife&gt; — 정수 수식. 읽기 전용이며 실수 결과는 반올림한다.</summary>
internal sealed class IntSwissKnifeNode : IntegerNodeBase
{
    private readonly IntSwissKnifeDef _def;
    private FormulaScope _scope = null!;
    private Formula _formula = null!;

    public IntSwissKnifeNode(IntSwissKnifeDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
    }

    internal override AccessMode IntrinsicAccessMode => AccessMode.ReadOnly;

    protected override void BindCore(NodeBinder binder)
    {
        _scope = new FormulaScope(this, _def, binder);
        _formula = _scope.Parse(_def.Formula, "Formula");
    }

    internal override async ValueTask<long> ReadInt64Async(CancellationToken ct)
        => NumericCodec.ToInt64(await _scope.EvaluateAsync(_formula, null, default, ct).ConfigureAwait(false), Name);

    protected override ValueTask WriteCoreAsync(long value, CancellationToken ct)
        => throw new GenApiException($"Node '{Name}' cannot be written: read-only (IntSwissKnife).", Name);

    public override ValueTask<long> GetMinAsync(CancellationToken ct = default) => new(long.MinValue);
    public override ValueTask<long> GetMaxAsync(CancellationToken ct = default) => new(long.MaxValue);
    public override ValueTask<long> GetIncAsync(CancellationToken ct = default) => new(1L);
}

/// <summary>
/// &lt;IntConverter&gt; — pValue 노드와의 정수 양방향 변환. 읽기는 FormulaFrom(변수 TO = 대상 값), 쓰기는 FormulaTo(변수 FROM = 호스트 값).
/// Min/Max 는 Slope 에 따라 대상의 한계값을 FormulaFrom 으로 넘겨 정한다.
/// </summary>
internal sealed class IntConverterNode : IntegerNodeBase
{
    private readonly IntConverterDef _def;
    private NodeBase _pValue = null!;
    private FormulaScope _scope = null!;
    private Formula _to = null!;
    private Formula _from = null!;

    public IntConverterNode(IntConverterDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
    }

    internal override NodeBase? ValueTarget => _pValue;

    protected override void BindCore(NodeBinder binder)
    {
        if (_def.PValue is null) throw new GenApiException($"IntConverter '{Name}' has no pValue.", Name);
        _pValue = binder.Resolve(_def.PValue, RefKind.Value, "pValue", NodeBinder.Numeric);
        _scope = new FormulaScope(this, _def, binder);
        _to = _scope.Parse(_def.FormulaTo, "FormulaTo");
        _from = _scope.Parse(_def.FormulaFrom, "FormulaFrom");
    }

    internal override async ValueTask<long> ReadInt64Async(CancellationToken ct)
    {
        var to = await _pValue.ReadValueAsync(ct).ConfigureAwait(false);
        return NumericCodec.ToInt64(await _scope.EvaluateAsync(_from, "TO", to, ct).ConfigureAwait(false), Name);
    }

    protected override async ValueTask WriteCoreAsync(long value, CancellationToken ct)
    {
        var device = await _scope.EvaluateAsync(_to, "FROM", new GenApiValue(value), ct).ConfigureAwait(false);
        await _pValue.WriteValueAsync(device, ct).ConfigureAwait(false);
    }

    /// <summary>대상이 그쪽 한계를 선언하지 않았으면(열린 끝) 정수가 담을 수 있는 극단을 그대로 쓴다 — 한계가 없다는 뜻이다.</summary>
    public override async ValueTask<long> GetMinAsync(CancellationToken ct = default)
    {
        var min = (await _scope.ConverterLimitsAsync(_pValue, _from, _def.Slope, ct).ConfigureAwait(false)).Min;
        return min is { } v ? NumericCodec.ToInt64(v, Name) : long.MinValue;
    }

    public override async ValueTask<long> GetMaxAsync(CancellationToken ct = default)
    {
        var max = (await _scope.ConverterLimitsAsync(_pValue, _from, _def.Slope, ct).ConfigureAwait(false)).Max;
        return max is { } v ? NumericCodec.ToInt64(v, Name) : long.MaxValue;
    }

    public override ValueTask<long> GetIncAsync(CancellationToken ct = default) => new(1L);
}
