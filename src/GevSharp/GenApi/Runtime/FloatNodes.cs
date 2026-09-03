using GevSharp.GenApi.Model;

namespace GevSharp.GenApi.Runtime;

/// <summary>
/// <see cref="IFloat"/> 로 노출되는 네 종류의 공통 면. 쓰기는 Min..Max 를 검사하고 NaN 을 거절한다.
/// Inc 는 정의됐을 때만 값이 있다(격자 검사는 하지 않는다 — 실수 격자는 오차로 어긋난다).
/// </summary>
internal abstract class FloatNodeBase : NodeBase, IFloat
{
    private readonly FloatBaseDef _baseDef;

    protected FloatNodeBase(FloatBaseDef def, GenApiNodeMap map) : base(def, map)
    {
        _baseDef = def;
    }

    public override NodeKind Kind => NodeKind.Float;
    public Representation Representation => _baseDef.Representation ?? Representation.PureNumber;
    public string? Unit => _baseDef.Unit;
    public DisplayNotation DisplayNotation => _baseDef.DisplayNotation ?? DisplayNotation.Automatic;
    public int? DisplayPrecision => _baseDef.DisplayPrecision;

    public async ValueTask<double> GetAsync(CancellationToken ct = default)
    {
        await EnsureReadableAsync(ct).ConfigureAwait(false);
        return await ReadDoubleAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask SetAsync(double value, CancellationToken ct = default)
    {
        await EnsureWritableAsync(ct).ConfigureAwait(false);
        await WriteDoubleAsync(value, ct).ConfigureAwait(false);
    }

    public abstract ValueTask<double> GetMinAsync(CancellationToken ct = default);
    public abstract ValueTask<double> GetMaxAsync(CancellationToken ct = default);
    public abstract ValueTask<double?> GetIncAsync(CancellationToken ct = default);

    internal abstract ValueTask<double> ReadDoubleAsync(CancellationToken ct);
    protected abstract ValueTask WriteCoreAsync(double value, CancellationToken ct);

    internal async ValueTask WriteDoubleAsync(double value, CancellationToken ct)
    {
        await ValidateAsync(value, ct).ConfigureAwait(false);
        await WriteCoreAsync(value, ct).ConfigureAwait(false);
        Map.OnWritten(this);
    }

    protected virtual async ValueTask ValidateAsync(double value, CancellationToken ct)
    {
        if (double.IsNaN(value))
            throw new GenApiException($"Value NaN cannot be written to node '{Name}'.", Name);
        var min = await GetMinAsync(ct).ConfigureAwait(false);
        var max = await GetMaxAsync(ct).ConfigureAwait(false);
        if (value < min || value > max)
            throw new GenApiException($"Value {value} for node '{Name}' is outside the range {min}..{max}.", Name);
    }

    internal override ValueTask<GenApiValue> ReadValueAsync(CancellationToken ct) => ReadAsValueAsync(ct);

    private async ValueTask<GenApiValue> ReadAsValueAsync(CancellationToken ct)
        => new GenApiValue(await ReadDoubleAsync(ct).ConfigureAwait(false));

    internal override ValueTask WriteValueAsync(GenApiValue value, CancellationToken ct) => WriteDoubleAsync(value.AsDouble, ct);

    internal override async ValueTask<GenApiValue> ReadLimitAsync(LimitKind kind, CancellationToken ct)
    {
        switch (kind)
        {
            case LimitKind.Min:
                return new GenApiValue(await GetMinAsync(ct).ConfigureAwait(false));
            case LimitKind.Max:
                return new GenApiValue(await GetMaxAsync(ct).ConfigureAwait(false));
            default:
                var inc = await GetIncAsync(ct).ConfigureAwait(false);
                if (inc is null) throw new GenApiException($"Node '{Name}' has no Inc.", Name);
                return new GenApiValue(inc.Value);
        }
    }
}

/// <summary>
/// &lt;Float&gt; — 리터럴 값, pValue 위임(정수 노드여도 된다 — 읽을 때 실수로 넓히고 쓸 때 반올림한다), 또는 pIndex 선택.
/// Min/Max/Inc 는 리터럴 → pMin/pMax/pInc → 위임 대상의 값 → (-double.MaxValue, double.MaxValue, null).
/// </summary>
internal sealed class FloatNode : FloatNodeBase
{
    private readonly FloatDef _def;
    private NodeBase? _pValue;
    private NodeBase[] _copies = Array.Empty<NodeBase>();
    private NodeBase? _pIndex;
    private IndexedSlot[] _slots = Array.Empty<IndexedSlot>();
    private NodeBase? _pValueDefault;
    private NodeBase? _pMin;
    private NodeBase? _pMax;
    private NodeBase? _pInc;
    private readonly object _localLock = new();
    private double _local;

    private readonly struct IndexedSlot
    {
        public IndexedSlot(long index, NodeBase? node, double literal)
        {
            Index = index;
            Node = node;
            Literal = literal;
        }

        public long Index { get; }
        public NodeBase? Node { get; }
        public double Literal { get; }
    }

    public FloatNode(FloatDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
        _local = def.Value ?? def.ValueDefault ?? 0.0;
    }

    protected override void BindCore(NodeBinder binder)
    {
        _pValue = binder.ResolveOptional(_def.PValue, RefKind.Value, "pValue", NodeBinder.Numeric);
        _pIndex = binder.ResolveOptional(_def.PIndex, RefKind.Value, "pIndex", NodeBinder.Numeric);
        _pValueDefault = binder.ResolveOptional(_def.PValueDefault, RefKind.Value, "pValueDefault", NodeBinder.Numeric);
        _pMin = binder.ResolveOptional(_def.PMin, RefKind.Value, "pMin", NodeBinder.Numeric);
        _pMax = binder.ResolveOptional(_def.PMax, RefKind.Value, "pMax", NodeBinder.Numeric);
        _pInc = binder.ResolveOptional(_def.PInc, RefKind.Value, "pInc", NodeBinder.Numeric);

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

    internal override async ValueTask<double> ReadDoubleAsync(CancellationToken ct)
    {
        if (_pValue is not null) return await ReadDoubleFromAsync(_pValue, ct).ConfigureAwait(false);
        if (_pIndex is not null)
        {
            var slot = await SelectSlotAsync(ct).ConfigureAwait(false);
            return slot.Node is null ? slot.Literal : await ReadDoubleFromAsync(slot.Node, ct).ConfigureAwait(false);
        }
        // pIndex 없이 pValueDefault 만 있는 정의 — 기본 노드가 곧 값 출처다
        if (_pValueDefault is not null) return await ReadDoubleFromAsync(_pValueDefault, ct).ConfigureAwait(false);
        lock (_localLock) return _local;
    }

    protected override async ValueTask WriteCoreAsync(double value, CancellationToken ct)
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
            lock (_localLock) _local = value;
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

    private async ValueTask<double?> TargetLimitAsync(LimitKind kind, CancellationToken ct)
    {
        var target = await GetAccessTargetAsync(ct).ConfigureAwait(false);
        switch (target)
        {
            case FloatNodeBase f:
                return kind switch
                {
                    LimitKind.Min => await f.GetMinAsync(ct).ConfigureAwait(false),
                    LimitKind.Max => await f.GetMaxAsync(ct).ConfigureAwait(false),
                    _ => await f.GetIncAsync(ct).ConfigureAwait(false),
                };
            case IntegerNodeBase i:
                return kind switch
                {
                    LimitKind.Min => await i.GetMinAsync(ct).ConfigureAwait(false),
                    LimitKind.Max => await i.GetMaxAsync(ct).ConfigureAwait(false),
                    _ => await i.GetIncAsync(ct).ConfigureAwait(false),
                };
            default:
                return null;
        }
    }

    public override async ValueTask<double> GetMinAsync(CancellationToken ct = default)
    {
        if (_def.Min is { } m) return m;
        if (_pMin is not null) return await ReadDoubleFromAsync(_pMin, ct).ConfigureAwait(false);
        return await TargetLimitAsync(LimitKind.Min, ct).ConfigureAwait(false) ?? -double.MaxValue;
    }

    public override async ValueTask<double> GetMaxAsync(CancellationToken ct = default)
    {
        if (_def.Max is { } m) return m;
        if (_pMax is not null) return await ReadDoubleFromAsync(_pMax, ct).ConfigureAwait(false);
        return await TargetLimitAsync(LimitKind.Max, ct).ConfigureAwait(false) ?? double.MaxValue;
    }

    public override async ValueTask<double?> GetIncAsync(CancellationToken ct = default)
    {
        if (_def.Inc is { } i) return i;
        if (_pInc is not null) return await ReadDoubleFromAsync(_pInc, ct).ConfigureAwait(false);
        return await TargetLimitAsync(LimitKind.Inc, ct).ConfigureAwait(false);
    }
}

/// <summary>&lt;FloatReg&gt; — IEEE 754 실수 레지스터. 길이 4(단정도) 또는 8(배정도), Endianess 로 바이트 순서.</summary>
internal sealed class FloatRegNode : FloatNodeBase
{
    private readonly FloatRegDef _def;
    private readonly RegisterCore _core;

    public FloatRegNode(FloatRegDef def, GenApiNodeMap map) : base(def, map)
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
        if (length != 4 && length != 8)
            throw new GenApiException($"FloatReg '{Name}' has length {length}; a float register must be 4 or 8 bytes.", Name);
    }

    internal override async ValueTask<double> ReadDoubleAsync(CancellationToken ct)
    {
        var bytes = await _core.ReadAsync(ct).ConfigureAwait(false);
        CheckLength(bytes.Length);
        return NumericCodec.DecodeFloat(bytes, bytes.Length, _def.Endianess);
    }

    protected override async ValueTask WriteCoreAsync(double value, CancellationToken ct)
    {
        var len = await _core.GetLengthAsync(ct).ConfigureAwait(false);
        CheckLength(len);
        var bytes = new byte[len];
        NumericCodec.EncodeFloat(value, bytes, len, _def.Endianess);
        await _core.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async ValueTask<int> LengthAsync(CancellationToken ct)
    {
        var len = _core.StaticLength ?? await _core.GetLengthAsync(ct).ConfigureAwait(false);
        CheckLength(len);
        return len;
    }

    public override async ValueTask<double> GetMinAsync(CancellationToken ct = default)
        => await LengthAsync(ct).ConfigureAwait(false) == 4 ? -float.MaxValue : -double.MaxValue;

    public override async ValueTask<double> GetMaxAsync(CancellationToken ct = default)
        => await LengthAsync(ct).ConfigureAwait(false) == 4 ? float.MaxValue : double.MaxValue;

    public override ValueTask<double?> GetIncAsync(CancellationToken ct = default) => new((double?)null);
}

/// <summary>&lt;SwissKnife&gt; — 실수 수식. 읽기 전용.</summary>
internal sealed class SwissKnifeNode : FloatNodeBase
{
    private readonly SwissKnifeDef _def;
    private FormulaScope _scope = null!;
    private Formula _formula = null!;

    public SwissKnifeNode(SwissKnifeDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
    }

    internal override AccessMode IntrinsicAccessMode => AccessMode.ReadOnly;

    protected override void BindCore(NodeBinder binder)
    {
        _scope = new FormulaScope(this, _def, binder);
        _formula = _scope.Parse(_def.Formula, "Formula");
    }

    internal override async ValueTask<double> ReadDoubleAsync(CancellationToken ct)
        => (await _scope.EvaluateAsync(_formula, null, default, ct).ConfigureAwait(false)).AsDouble;

    protected override ValueTask WriteCoreAsync(double value, CancellationToken ct)
        => throw new GenApiException($"Node '{Name}' cannot be written: read-only (SwissKnife).", Name);

    public override ValueTask<double> GetMinAsync(CancellationToken ct = default) => new(-double.MaxValue);
    public override ValueTask<double> GetMaxAsync(CancellationToken ct = default) => new(double.MaxValue);
    public override ValueTask<double?> GetIncAsync(CancellationToken ct = default) => new((double?)null);
}

/// <summary>
/// &lt;Converter&gt; — pValue 노드와의 실수 양방향 변환. 읽기는 FormulaFrom(TO = 대상 값), 쓰기는 FormulaTo(FROM = 호스트 값);
/// 대상이 정수 노드면 쓰기 결과를 반올림해 넣는다. Min/Max 는 Slope 로 정하고 Inc 는 없다.
/// </summary>
internal sealed class ConverterNode : FloatNodeBase
{
    private readonly ConverterDef _def;
    private NodeBase _pValue = null!;
    private FormulaScope _scope = null!;
    private Formula _to = null!;
    private Formula _from = null!;

    public ConverterNode(ConverterDef def, GenApiNodeMap map) : base(def, map)
    {
        _def = def;
    }

    internal override NodeBase? ValueTarget => _pValue;

    protected override void BindCore(NodeBinder binder)
    {
        if (_def.PValue is null) throw new GenApiException($"Converter '{Name}' has no pValue.", Name);
        _pValue = binder.Resolve(_def.PValue, RefKind.Value, "pValue", NodeBinder.Numeric);
        _scope = new FormulaScope(this, _def, binder);
        _to = _scope.Parse(_def.FormulaTo, "FormulaTo");
        _from = _scope.Parse(_def.FormulaFrom, "FormulaFrom");
    }

    internal override async ValueTask<double> ReadDoubleAsync(CancellationToken ct)
    {
        var to = await _pValue.ReadValueAsync(ct).ConfigureAwait(false);
        return (await _scope.EvaluateAsync(_from, "TO", to, ct).ConfigureAwait(false)).AsDouble;
    }

    protected override async ValueTask WriteCoreAsync(double value, CancellationToken ct)
    {
        var device = await _scope.EvaluateAsync(_to, "FROM", new GenApiValue(value), ct).ConfigureAwait(false);
        await _pValue.WriteValueAsync(device, ct).ConfigureAwait(false);
    }

    /// <summary>대상이 그쪽 한계를 선언하지 않았으면(열린 끝) 실수가 담을 수 있는 극단을 그대로 쓴다 — 한계가 없다는 뜻이다.</summary>
    public override async ValueTask<double> GetMinAsync(CancellationToken ct = default)
    {
        var min = (await _scope.ConverterLimitsAsync(_pValue, _from, _def.Slope, ct).ConfigureAwait(false)).Min;
        return min is { } v ? v.AsDouble : -double.MaxValue;
    }

    public override async ValueTask<double> GetMaxAsync(CancellationToken ct = default)
    {
        var max = (await _scope.ConverterLimitsAsync(_pValue, _from, _def.Slope, ct).ConfigureAwait(false)).Max;
        return max is { } v ? v.AsDouble : double.MaxValue;
    }

    public override ValueTask<double?> GetIncAsync(CancellationToken ct = default) => new((double?)null);
}
