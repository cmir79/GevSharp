using GevSharp.GenApi.Model;

namespace GevSharp.GenApi.Runtime;

/// <summary>참조의 종류 — 바인더가 역방향 색인과 순환 검사에 어떤 간선으로 넣을지 정한다.</summary>
internal enum RefKind
{
    /// <summary>값 평가가 따라가는 참조(pValue·pAddress·pIndex·pVariable). 순환 검사 대상이며 캐시 무효화가 전파된다.</summary>
    Value,
    /// <summary>
    /// 한계 참조(pMin·pMax·pInc). 무효화는 전파되지만 순환 검사에는 끼지 않는다.
    /// 한계를 따라가면 상대의 <b>값</b>을 읽지 상대의 한계를 읽지 않으므로 이 간선만으로는 재귀가 닫히지 않는다.
    /// 서로의 한계를 가리키는 짝(하한의 Max = 상한, 상한의 Min = 하한)은 GenICam 에서 정상이고 흔한 모양이며,
    /// 이것을 값 순환으로 보면 그 XML 을 가진 장치의 노드맵 전체가 거부된다(실측: 그렇게 카메라 하나를 통째로 못 썼다).
    /// 한계를 지나 진짜로 재귀하는 모양(수식 변수의 .Min/.Max 접미사)은 그 변수가 <see cref="Value"/> 간선이라 그대로 잡힌다.
    /// </summary>
    Limit,
    /// <summary>술어 참조(pIsImplemented/pIsAvailable/pIsLocked). 무효화는 전파되지만 값 평가 재귀에는 끼지 않는다 — 이 간선을 지나야 닫히는 순환은 경고로만 남긴다.</summary>
    Guard,
    /// <summary>바인딩 시점에 값이 확정되는 참조(수식의 <c>.Entry.</c> 변수). 실행 중에 노드를 읽지 않으므로 간선을 남기지 않는다.</summary>
    Constant,
    /// <summary>pInvalidator — 대상이 쓰이면 이 노드가 무효화된다.</summary>
    Invalidator,
    /// <summary>pSelected — 이 노드(셀렉터)가 쓰이면 대상이 무효화된다.</summary>
    Selected,
    /// <summary>pFeature — 카테고리 트리. 간선을 남기지 않는다.</summary>
    Feature,
    /// <summary>pPort — 전송 경계. 간선을 남기지 않는다.</summary>
    Port,
}

/// <summary>수식 변수 접미사(.Min/.Max/.Inc)와 Converter 범위 계산이 묻는 한계값 종류.</summary>
internal enum LimitKind
{
    Min,
    Max,
    Inc,
}

/// <summary>
/// 모든 런타임 노드의 공통 뼈대 — 정의(<see cref="NodeDef"/>)와 노드맵을 쥐고, 술어(pIsImplemented/pIsAvailable/pIsLocked)와
/// 접근 모드 합성, 무효화 전파용 링크(의존 노드·pInvalidator 청취자·pSelected)를 담는다.
/// <para>
/// 값 접근은 두 층이다. 공개 인터페이스(GetAsync/SetAsync…)는 접근 모드를 검사한 뒤 내부 경로를 부르고,
/// 내부 경로(<see cref="ReadValueAsync"/>/<see cref="WriteValueAsync"/>)는 검사 없이 값만 옮긴다 — pValue 사슬을 따라 내려갈 때
/// 대상 노드의 술어는 이미 호출 노드의 접근 모드에 합성돼 있으므로(대상의 모드를 교집합으로 넣는다) 다시 검사하지 않는다.
/// 술어 노드 자체도 내부 경로로 읽는다(술어에 술어가 걸려 되돌아오는 재귀를 끊는다).
/// </para>
/// </summary>
internal abstract class NodeBase : INode
{
    private readonly NodeDef _def;
    private readonly GenApiNodeMap _map;
    private NodeBase? _isImplemented;
    private NodeBase? _isAvailable;
    private NodeBase? _isLocked;
    private bool _isBound;

    protected NodeBase(NodeDef def, GenApiNodeMap map)
    {
        _def = def;
        _map = map;
    }

    internal NodeDef Def => _def;
    internal GenApiNodeMap Map => _map;

    public string Name => _def.Name;
    public abstract NodeKind Kind { get; }
    public string? DisplayName => _def.DisplayName;
    public string? Description => _def.Description;
    public string? ToolTip => _def.ToolTip;
    public Visibility Visibility => _def.Visibility;
    public bool IsStreamable => _def.IsStreamable;

    /// <summary>이 노드를 p* 참조로 가리키는 노드들(역방향 색인). 바인딩 뒤에는 바뀌지 않는다.</summary>
    internal List<NodeBase> Dependents { get; } = new();

    /// <summary>pInvalidator 로 이 노드를 지목한 노드들 — 이 노드가 쓰이면 무효화된다.</summary>
    internal List<NodeBase> InvalidatorListeners { get; } = new();

    /// <summary>이 노드가 셀렉터일 때 pSelected 로 지목한 노드들.</summary>
    internal List<NodeBase> Selected { get; } = new();

    /// <summary>이 노드를 pSelected 로 지목한 셀렉터들(pSelecting — 목록에서 유도).</summary>
    internal List<NodeBase> Selectors { get; } = new();

    internal bool IsSelector => Selected.Count > 0;

    /// <summary>이름 참조를 노드로 해석한다. 한 번만 실행된다(레지스터의 인라인 IntSwissKnife 처럼 먼저 바인딩될 수 있다).</summary>
    internal void Bind(NodeBinder binder)
    {
        if (_isBound) return;
        _isBound = true;
        _isImplemented = binder.ResolveOptional(_def.PIsImplemented, RefKind.Guard, "pIsImplemented", NodeBinder.Numeric);
        _isAvailable = binder.ResolveOptional(_def.PIsAvailable, RefKind.Guard, "pIsAvailable", NodeBinder.Numeric);
        _isLocked = binder.ResolveOptional(_def.PIsLocked, RefKind.Guard, "pIsLocked", NodeBinder.Numeric);
        foreach (var inv in _def.PInvalidators) binder.Resolve(inv, RefKind.Invalidator, "pInvalidator", NodeBinder.Any);
        foreach (var sel in _def.PSelected) binder.Resolve(sel, RefKind.Selected, "pSelected", NodeBinder.Any);
        BindCore(binder);
    }

    /// <summary>종류별 참조 해석.</summary>
    protected abstract void BindCore(NodeBinder binder);

    /// <summary>술어·대상 노드를 빼고 이 노드 자체가 허용하는 접근(레지스터의 AccessMode, 수식 노드의 읽기 전용 등).</summary>
    internal virtual AccessMode IntrinsicAccessMode => AccessMode.ReadWrite;

    /// <summary>값을 위임하는 노드(pValue). <see cref="CollectValueTargets"/> 의 기본 구현이 쓴다.</summary>
    internal virtual NodeBase? ValueTarget => null;

    /// <summary>
    /// 값 사슬 무효화가 따라 내려갈 노드 전부 — 기본은 <see cref="ValueTarget"/> 하나. pIndex 로 슬롯을 고르는 노드는 지금 선택과 무관하게
    /// 모든 슬롯을 내놓는다(어느 슬롯이 낡았는지는 읽어 봐야 알므로 전부 버린다).
    /// </summary>
    internal virtual void CollectValueTargets(List<NodeBase> into)
    {
        if (ValueTarget is { } t) into.Add(t);
    }

    /// <summary>지금 값을 위임하는 노드 — pIndex 선택처럼 읽어야 정해지는 경우가 있어 비동기다. 값 출처가 없으면 null(던지지 않는다).</summary>
    internal virtual ValueTask<NodeBase?> GetAccessTargetAsync(CancellationToken ct) => new(ValueTarget);

    /// <summary>
    /// 지금 값 출처가 없으면 그 사유(예: pIndex 가 어느 슬롯에도 안 맞고 기본값도 없음), 있으면 null.
    /// 접근 모드는 NotAvailable 이 되고 읽기·쓰기 오류 메시지에 이 사유가 실린다 — 접근 모드 조회가 예외를 던지지 않게 한다.
    /// </summary>
    internal virtual ValueTask<string?> GetMissingValueReasonAsync(CancellationToken ct) => new((string?)null);

    /// <summary>이 노드 자신의 캐시만 버린다(전파 없음).</summary>
    internal virtual void DropOwnCache() { }

    // ---- 술어 ----

    public async ValueTask<bool> IsImplementedAsync(CancellationToken ct = default)
    {
        if (_isImplemented is not null && !await EvalPredicateAsync(_isImplemented, ct).ConfigureAwait(false)) return false;
        var target = await GetAccessTargetAsync(ct).ConfigureAwait(false);
        return target is null || await target.IsImplementedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 가용 여부는 합성된 접근 모드 그대로다 — 구현되지 않았거나(pIsImplemented 거짓, 대상이 NotImplemented) 가용하지 않으면 거짓.
    /// 술어를 따로 세면 "구현되지 않았는데 가용하다"는 답이 나와 모드 조회와 어긋난다.
    /// </summary>
    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var mode = (await ComputeAccessAsync(ct).ConfigureAwait(false)).Mode;
        return mode != AccessMode.NotImplemented && mode != AccessMode.NotAvailable;
    }

    public async ValueTask<bool> IsLockedAsync(CancellationToken ct = default)
    {
        if (_isLocked is not null && await EvalPredicateAsync(_isLocked, ct).ConfigureAwait(false)) return true;
        var target = await GetAccessTargetAsync(ct).ConfigureAwait(false);
        return target is not null && await target.IsLockedAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<AccessMode> GetAccessModeAsync(CancellationToken ct = default)
        => (await ComputeAccessAsync(ct).ConfigureAwait(false)).Mode;

    /// <summary>
    /// 접근 모드 합성: 구현 술어 → 가용 술어 → 값 출처 유무 → 고유 모드 ∩ 대상 모드 ∩ ImposedAccessMode → 잠금(쓰기를 뗀다).
    /// 잠금 때문에 쓰기가 빠졌는지와, 모드만으로는 적을 수 없는 사유(값 출처 없음)도 함께 돌려줘 오류 메시지에 싣는다.
    /// </summary>
    private async ValueTask<(AccessMode Mode, bool IsLockDegraded, string? Detail)> ComputeAccessAsync(CancellationToken ct)
    {
        // 술어가 청크 데이터를 읽어야 답할 수 있으면 그 답을 낼 수 없다 — 그것은 곧 "지금 쓸 수 없다" 이므로 NotAvailable 이다.
        // 청크에서 온 예외만 골라 잡는다(표식으로 구분한다) — 장치 거절이나 시한 초과 같은 다른 실패는 그대로 올려보낸다.
        try
        {
            if (_isImplemented is not null && !await EvalPredicateAsync(_isImplemented, ct).ConfigureAwait(false))
                return (AccessMode.NotImplemented, false, null);
            if (_isAvailable is not null && !await EvalPredicateAsync(_isAvailable, ct).ConfigureAwait(false))
                return (AccessMode.NotAvailable, false, null);
        }
        catch (GenApiException ex) when (ex.Data.Contains(GenApiException.ChunkDataKey))
        {
            return (AccessMode.NotAvailable, false, ex.Message);
        }
        if (await GetMissingValueReasonAsync(ct).ConfigureAwait(false) is { } missing)
            return (AccessMode.NotAvailable, false, missing);

        var mode = IntrinsicAccessMode;
        var target = await GetAccessTargetAsync(ct).ConfigureAwait(false);
        if (target is not null)
        {
            var targetMode = await target.GetAccessModeAsync(ct).ConfigureAwait(false);
            if (targetMode == AccessMode.NotImplemented || targetMode == AccessMode.NotAvailable) return (targetMode, false, null);
            mode = Intersect(mode, targetMode);
        }
        if (_def.ImposedAccessMode is { } imposed) mode = Intersect(mode, imposed);

        if (CanWrite(mode) && _isLocked is not null && await EvalPredicateAsync(_isLocked, ct).ConfigureAwait(false))
            return (mode == AccessMode.ReadWrite ? AccessMode.ReadOnly : AccessMode.NotAvailable, true, null);
        return (mode, false, null);
    }

    internal static AccessMode Intersect(AccessMode a, AccessMode b)
    {
        var canRead = CanRead(a) && CanRead(b);
        var canWrite = CanWrite(a) && CanWrite(b);
        if (canRead && canWrite) return AccessMode.ReadWrite;
        if (canRead) return AccessMode.ReadOnly;
        if (canWrite) return AccessMode.WriteOnly;
        return AccessMode.NotAvailable;
    }

    internal static bool CanRead(AccessMode m) => m == AccessMode.ReadOnly || m == AccessMode.ReadWrite;
    internal static bool CanWrite(AccessMode m) => m == AccessMode.WriteOnly || m == AccessMode.ReadWrite;

    /// <summary>읽을 수 없으면 노드 이름과 사유("not implemented"/"not available"/"write-only"/값 출처 없음)를 담아 던진다.</summary>
    internal async ValueTask EnsureReadableAsync(CancellationToken ct)
    {
        var (mode, _, detail) = await ComputeAccessAsync(ct).ConfigureAwait(false);
        if (CanRead(mode)) return;
        throw new GenApiException($"Node '{Name}' cannot be read: {Reason(mode, false, detail)}.", Name);
    }

    /// <summary>쓸 수 없으면 노드 이름과 사유("not implemented"/"not available"/"locked"/"read-only"/값 출처 없음)를 담아 던진다.</summary>
    internal async ValueTask EnsureWritableAsync(CancellationToken ct)
    {
        var (mode, isLockDegraded, detail) = await ComputeAccessAsync(ct).ConfigureAwait(false);
        if (CanWrite(mode)) return;
        throw new GenApiException($"Node '{Name}' cannot be written: {Reason(mode, isLockDegraded, detail)}.", Name);
    }

    private static string Reason(AccessMode mode, bool isLockDegraded, string? detail) => detail ?? mode switch
    {
        AccessMode.NotImplemented => "not implemented",
        AccessMode.NotAvailable => isLockDegraded ? "locked" : "not available",
        AccessMode.WriteOnly => "write-only",
        AccessMode.ReadOnly => isLockDegraded ? "locked" : "read-only",
        _ => "no access",
    };

    public void Invalidate() => _map.InvalidateNode(this);

    // ---- 내부 값 경로(접근 검사 없음) ----

    /// <summary>수치 값으로 읽는다(정수 노드 → 정수, 실수 노드 → 실수, Boolean → 1/0, Enumeration → 항목 값).</summary>
    internal virtual ValueTask<GenApiValue> ReadValueAsync(CancellationToken ct) => throw NoValue("read as a value");

    /// <summary>수치 값을 쓴다. 정수 노드는 실수를 반올림해 받는다. 범위 검사는 각 노드가 한다.</summary>
    internal virtual ValueTask WriteValueAsync(GenApiValue value, CancellationToken ct) => throw NoValue("written as a value");

    /// <summary>Min/Max/Inc 를 수치로. 정의되지 않은 Inc 는 <see cref="GenApiException"/>.</summary>
    internal virtual ValueTask<GenApiValue> ReadLimitAsync(LimitKind kind, CancellationToken ct) => throw NoValue($"queried for {kind}");

    internal virtual ValueTask<string> ReadStringAsync(CancellationToken ct) => throw NoValue("read as a string");

    internal virtual ValueTask WriteStringAsync(string value, CancellationToken ct) => throw NoValue("written as a string");

    private GenApiException NoValue(string what) => new($"Node '{Name}' ({_def.Kind}) cannot be {what}.", Name);

    internal static async ValueTask<long> ReadInt64FromAsync(NodeBase node, CancellationToken ct)
        => NumericCodec.ToInt64(await node.ReadValueAsync(ct).ConfigureAwait(false), node.Name);

    internal static async ValueTask<double> ReadDoubleFromAsync(NodeBase node, CancellationToken ct)
        => (await node.ReadValueAsync(ct).ConfigureAwait(false)).AsDouble;

    /// <summary>술어 노드의 값 — 0 이 아니면 참.</summary>
    private static async ValueTask<bool> EvalPredicateAsync(NodeBase predicate, CancellationToken ct)
        => (await predicate.ReadValueAsync(ct).ConfigureAwait(false)).IsNonZero;

    /// <summary>
    /// 노드 자신과 값 사슬(pValue/pValueDefault/pValueIndexed → … → 레지스터)의 캐시를 버린다 — 의존 노드로의 전파는 없다.
    /// stopAt 에 닿으면 그 아래로는 내려가지 않는다(방금 쓰인 노드 — 그 캐시는 쓰기 정책이 정했다). 순환은 바인딩에서 막히지만
    /// 방문 집합으로 한 번 더 지킨다.
    /// </summary>
    internal static void DropCacheChain(NodeBase? node, NodeBase? stopAt = null)
    {
        if (node is null || ReferenceEquals(node, stopAt)) return;
        var queue = new List<NodeBase> { node };
        var visited = new HashSet<NodeBase>(NodeReferenceComparer.Instance) { node };
        var targets = new List<NodeBase>();
        for (var i = 0; i < queue.Count; i++)
        {
            var n = queue[i];
            n.DropOwnCache();
            targets.Clear();
            n.CollectValueTargets(targets);
            foreach (var t in targets)
            {
                if (!ReferenceEquals(t, stopAt) && visited.Add(t)) queue.Add(t);
            }
        }
    }

    public override string ToString() => $"{_def.Kind} '{Name}'";
}

/// <summary>참조 동일성 비교자 — 노드 객체에 값 동등성은 없지만 명시해 두면 집합·사전이 GetHashCode 재정의에 흔들리지 않는다.</summary>
internal sealed class NodeReferenceComparer : IEqualityComparer<NodeBase>
{
    public static readonly NodeReferenceComparer Instance = new();
    public bool Equals(NodeBase? x, NodeBase? y) => ReferenceEquals(x, y);
    public int GetHashCode(NodeBase obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
