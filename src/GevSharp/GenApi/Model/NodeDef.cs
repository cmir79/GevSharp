namespace GevSharp.GenApi.Model;

/// <summary>
/// XML 노드 요소 하나를 옮겨 적은 불변 정의. 파서가 만들고 런타임이 읽는다 — 값·주소·의존 노드는 전부 이름(문자열)으로만 담고,
/// 이름 해석·레지스터 접근·수식 평가는 런타임 몫이다(빠진 이름은 바인딩 시점에 <see cref="GenApiException"/>).
/// 공통 속성/자식은 여기, 종류별 항목은 파생 레코드에 있다. p 접두 필드는 다른 노드의 Name 을 가리킨다.
/// </summary>
public abstract record NodeDef
{
    /// <summary>XML Name 속성 그대로(대소문자 구분). 문서 안에서 유일하다.</summary>
    public required string Name { get; init; }

    /// <summary>어느 XML 요소에서 왔는지.</summary>
    public abstract NodeDefKind Kind { get; }

    /// <summary>런타임이 이 정의로 만들 공개 인터페이스 종류(<see cref="NodeKind"/>). 정수 계열 다섯 종류는 전부 Integer 로 모인다.</summary>
    public NodeKind InterfaceKind => Kind switch
    {
        NodeDefKind.Category => NodeKind.Category,
        NodeDefKind.Integer or NodeDefKind.IntReg or NodeDefKind.MaskedIntReg or NodeDefKind.IntSwissKnife or NodeDefKind.IntConverter => NodeKind.Integer,
        NodeDefKind.Float or NodeDefKind.FloatReg or NodeDefKind.SwissKnife or NodeDefKind.Converter => NodeKind.Float,
        NodeDefKind.String or NodeDefKind.StringReg => NodeKind.String,
        NodeDefKind.Boolean => NodeKind.Boolean,
        NodeDefKind.Enumeration => NodeKind.Enumeration,
        NodeDefKind.EnumEntry => NodeKind.EnumEntry,
        NodeDefKind.Command => NodeKind.Command,
        NodeDefKind.Register => NodeKind.Register,
        NodeDefKind.Port => NodeKind.Port,
        _ => NodeKind.Unknown,
    };

    // ---- 공통 속성(XML attribute) ----

    /// <summary>NameSpace 속성. 기본 Custom.</summary>
    public NodeNameSpace NameSpace { get; init; } = NodeNameSpace.Custom;

    /// <summary>Comment 속성 — 사람용 메모. 동작에 영향 없음.</summary>
    public string? Comment { get; init; }

    // ---- 공통 자식(XML child element) ----

    public string? ToolTip { get; init; }
    public string? Description { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>DocuURL — 외부 문서 링크.</summary>
    public string? DocuUrl { get; init; }

    /// <summary>Visibility. 기본 Beginner.</summary>
    public Visibility Visibility { get; init; } = Visibility.Beginner;

    /// <summary>EventID — 장치 이벤트 식별자. XML 에 적힌 16진 문자열 그대로(접두 0x 없음). 없으면 null. 값은 <see cref="EventIdValue"/>.</summary>
    public string? EventId { get; init; }

    /// <summary>
    /// <see cref="EventId"/> 를 16진으로 읽은 값(예: "9002" → 0x9002). 런타임은 이 값으로 장치 이벤트 ID 와 맞춘다 —
    /// 문자열을 10진으로 다시 읽으면 조용히 다른 이벤트가 된다. 없으면 null.
    /// </summary>
    public ulong? EventIdValue { get; init; }

    /// <summary>pIsImplemented — 구현 여부 술어 노드(Integer/Boolean/SwissKnife, 0 이 아니면 참). 없으면 항상 구현됨.</summary>
    public string? PIsImplemented { get; init; }

    /// <summary>pIsAvailable — 가용 여부 술어 노드. 없으면 항상 가용.</summary>
    public string? PIsAvailable { get; init; }

    /// <summary>pIsLocked — 잠금 여부 술어 노드. 없으면 잠기지 않음.</summary>
    public string? PIsLocked { get; init; }

    /// <summary>pBlockPolling — 참이면 이 노드의 폴링을 막는 술어 노드.</summary>
    public string? PBlockPolling { get; init; }

    /// <summary>pInvalidator 목록 — 이들 노드에 쓰기가 일어나면 이 노드의 캐시를 버린다.</summary>
    public IReadOnlyList<string> PInvalidators { get; init; } = Array.Empty<string>();

    /// <summary>ImposedAccessMode — XML 이 강제로 좁힌 접근 모드(RO/WO/RW). 없으면 null.</summary>
    public AccessMode? ImposedAccessMode { get; init; }

    /// <summary>pAlias — GUI 가 이 노드 대신 보여 줄 노드.</summary>
    public string? PAlias { get; init; }

    /// <summary>pCastAlias — 형 변환 별칭(예: Integer ↔ Enumeration 짝).</summary>
    public string? PCastAlias { get; init; }

    /// <summary>Streamable — 레시피 저장/복원 대상 피처. 기본 false.</summary>
    public bool IsStreamable { get; init; }

    /// <summary>pError 목록 — 쓰기 실패 원인을 설명하는 Enumeration 노드들.</summary>
    public IReadOnlyList<string> PErrors { get; init; } = Array.Empty<string>();

    /// <summary>IsDeprecated — 더는 권장하지 않는 피처. 기본 false.</summary>
    public bool IsDeprecated { get; init; }

    /// <summary>
    /// PollingTime(ms). 레지스터 노드에서는 읽기 캐시를 쓰지 말라는 뜻(장치가 값을 스스로 바꾼다), Command 에서는 완료 폴링 주기.
    /// 어느 요소에나 올 수 있어 공통 필드로 둔다. 없으면 null.
    /// </summary>
    public long? PollingTimeMs { get; init; }

    /// <summary>
    /// pSelected 목록 — 이 노드가 셀렉터일 때 값에 따라 달라지는 피처들. Integer 계열·Enumeration·Boolean 에 온다(파서는 어느 요소에서든 받는다).
    /// 역방향(pSelecting)은 런타임이 이 목록에서 유도한다.
    /// </summary>
    public IReadOnlyList<string> PSelected { get; init; } = Array.Empty<string>();
}

// ---------------------------------------------------------------------------------------------------
// 수식(SwissKnife/Converter) 구성 요소
// ---------------------------------------------------------------------------------------------------

/// <summary>&lt;pVariable Name="X"&gt;NodeName&lt;/pVariable&gt; — 수식 변수 X 는 노드 NodeName 의 값.</summary>
public sealed record FormulaVariableDef(string Name, string PNode);

/// <summary>&lt;Expression Name="X"&gt;formula&lt;/Expression&gt; — 이름 붙은 부분식. 수식 안에서 변수처럼 쓴다.</summary>
public sealed record FormulaExpressionDef(string Name, string Expression);

/// <summary>&lt;Constant Name="X"&gt;value&lt;/Constant&gt; — 이름 붙은 상수. 정수로 읽히면 <see cref="IntValue"/> 도 채운다.</summary>
public sealed record FormulaConstantDef
{
    public required string Name { get; init; }

    /// <summary>XML 에 적힌 텍스트(공백 제거).</summary>
    public required string Text { get; init; }

    /// <summary>정수 리터럴(10진·0x 16진)로 읽혔으면 값, 실수 리터럴이면 null.</summary>
    public long? IntValue { get; init; }

    /// <summary>실수로 본 값 — 정수 리터럴도 여기 채워진다.</summary>
    public required double DoubleValue { get; init; }
}

/// <summary>수식을 가진 정의(IntSwissKnife/SwissKnife/IntConverter/Converter)의 공통 면.</summary>
public interface IFormulaNodeDef
{
    string Name { get; }
    IReadOnlyList<FormulaVariableDef> Variables { get; }
    IReadOnlyList<FormulaConstantDef> Constants { get; }
    IReadOnlyList<FormulaExpressionDef> Expressions { get; }
}

/// <summary>단일 Formula 를 가진 정의(IntSwissKnife/SwissKnife).</summary>
public interface ISwissKnifeNodeDef : IFormulaNodeDef
{
    string Formula { get; }
}

/// <summary>
/// 양방향 변환식을 가진 정의(IntConverter/Converter).
/// FormulaTo: 호스트 값(변수 FROM) → 장치 값, FormulaFrom: 장치 값(변수 TO) → 호스트 값. 장치 쪽 노드는 <see cref="PValue"/>.
/// </summary>
public interface IConverterNodeDef : IFormulaNodeDef
{
    string FormulaTo { get; }
    string FormulaFrom { get; }
    string? PValue { get; }
    Slope Slope { get; }
    bool IsLinear { get; }
}

// ---------------------------------------------------------------------------------------------------
// 값 간접 참조 — Integer/Float 의 인덱스 선택(pIndex + pValueIndexed + pValueDefault)
// ---------------------------------------------------------------------------------------------------

/// <summary>&lt;pValueIndexed Index="n"&gt;NodeName&lt;/pValueIndexed&gt; — pIndex 값이 n 일 때 값을 주는 노드.</summary>
public sealed record PValueIndexedDef(long Index, string PNode);

/// <summary>&lt;ValueIndexed Index="n"&gt;literal&lt;/ValueIndexed&gt; — pIndex 값이 n 일 때의 리터럴 값(Integer 는 long, Float 는 double).</summary>
public sealed record ValueIndexedDef<T>(long Index, T Value) where T : struct;

// ---------------------------------------------------------------------------------------------------
// 레지스터 집합(주소·길이·접근·포트) — IntReg/MaskedIntReg/FloatReg/StringReg/Register/StructEntry 가 공유
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// &lt;pIndex Offset="…" | pOffset="…"&gt;IndexNode&lt;/pIndex&gt; — 주소에 index × offset 을 더한다.
/// Offset/pOffset 둘 다 없으면 레지스터 Length 가 offset 이다.
/// </summary>
public sealed record PIndexDef
{
    /// <summary>인덱스 값을 주는 Integer 노드.</summary>
    public required string PNode { get; init; }

    /// <summary>리터럴 오프셋(바이트).</summary>
    public long? Offset { get; init; }

    /// <summary>오프셋을 주는 Integer 노드.</summary>
    public string? POffset { get; init; }
}

/// <summary>
/// 레지스터 노드의 주소·길이·접근 정보. 실제 주소 = Σ<see cref="Addresses"/> + Σ<see cref="PAddresses"/> 노드 값
/// + Σ(<see cref="PIndexes"/> 인덱스 × 오프셋) + Σ<see cref="AddressSwissKnives"/> 평가값.
/// 길이는 <see cref="Length"/> 리터럴 또는 <see cref="PLength"/> 노드 값 중 하나가 반드시 있다.
/// </summary>
public sealed record RegisterSet
{
    /// <summary>&lt;Address&gt; 리터럴들(16진 0x… 또는 10진). 여럿이면 합산.</summary>
    public IReadOnlyList<long> Addresses { get; init; } = Array.Empty<long>();

    /// <summary>&lt;pAddress&gt; — 주소에 더할 Integer 노드들.</summary>
    public IReadOnlyList<string> PAddresses { get; init; } = Array.Empty<string>();

    /// <summary>&lt;pIndex&gt; 항들.</summary>
    public IReadOnlyList<PIndexDef> PIndexes { get; init; } = Array.Empty<PIndexDef>();

    /// <summary>
    /// 주소 계산용 인라인 &lt;IntSwissKnife&gt; — 소유 노드 안에 중첩된 정의. 주소 계산은 항상 이 목록으로 한다.
    /// Name 속성이 있으면 같은 인스턴스가 모델의 Nodes 사전에도 등록되어 다른 노드가 p* 로 가리킬 수 있다.
    /// Name 이 없으면 "{소유노드}_AddrSwissKnife{n}" 으로 합성하고 Nodes 에는 넣지 않는다.
    /// </summary>
    public IReadOnlyList<IntSwissKnifeDef> AddressSwissKnives { get; init; } = Array.Empty<IntSwissKnifeDef>();

    /// <summary>&lt;Length&gt; 바이트 리터럴. <see cref="PLength"/> 와 택일.</summary>
    public long? Length { get; init; }

    /// <summary>&lt;pLength&gt; — 길이를 주는 Integer 노드.</summary>
    public string? PLength { get; init; }

    /// <summary>&lt;AccessMode&gt; RO/WO/RW. 없으면 ReadWrite 로 둔다.</summary>
    public AccessMode AccessMode { get; init; } = AccessMode.ReadWrite;

    /// <summary>&lt;pPort&gt; — 이 레지스터가 붙는 Port 노드. 스키마상 필수지만 모델은 비어 있어도 담고 런타임 바인딩에서 잡는다.</summary>
    public string? PPort { get; init; }

    /// <summary>&lt;Cachable&gt;. 없으면 WriteThrough.</summary>
    public Cachable Cachable { get; init; } = Cachable.WriteThrough;

    /// <summary>주소 항이 리터럴뿐이라 런타임 평가 없이 주소가 정해지는지.</summary>
    public bool HasStaticAddress => PAddresses.Count == 0 && PIndexes.Count == 0 && AddressSwissKnives.Count == 0;

    /// <summary>리터럴 &lt;Address&gt; 들의 합. <see cref="HasStaticAddress"/> 가 참일 때가 곧 최종 주소다.</summary>
    public long StaticAddress
    {
        get
        {
            long sum = 0;
            foreach (var a in Addresses) sum += a;
            return sum;
        }
    }
}

/// <summary>레지스터 집합을 가진 정의(IntReg/MaskedIntReg/FloatReg/StringReg/Register).</summary>
public interface IRegisterNodeDef
{
    string Name { get; }
    RegisterSet RegisterSet { get; }
}

// ---------------------------------------------------------------------------------------------------
// Category / Node / Unknown
// ---------------------------------------------------------------------------------------------------

/// <summary>&lt;Category&gt; — 피처 트리의 가지. Root 카테고리가 트리 진입점.</summary>
public sealed record CategoryDef : NodeDef
{
    public override NodeDefKind Kind => NodeDefKind.Category;

    /// <summary>&lt;pFeature&gt; 순서 그대로.</summary>
    public IReadOnlyList<string> PFeatures { get; init; } = Array.Empty<string>();
}

/// <summary>제네릭 &lt;Node&gt; — 값 없는 노드. 공통 속성만 가진다.</summary>
public sealed record GenericNodeDef : NodeDef
{
    public override NodeDefKind Kind => NodeDefKind.Node;
}

/// <summary>스키마에 없는 요소의 자리표시자. 파서는 경고를 남기고 이름과 요소명만 담는다. 런타임은 접근 시 <see cref="GenApiException"/>.</summary>
public sealed record UnknownDef : NodeDef
{
    public override NodeDefKind Kind => NodeDefKind.Unknown;

    /// <summary>XML 요소의 로컬 이름.</summary>
    public required string ElementName { get; init; }
}

// ---------------------------------------------------------------------------------------------------
// 정수 계열
// ---------------------------------------------------------------------------------------------------

/// <summary>정수 인터페이스(IInteger)로 노출되는 다섯 종류의 공통 면.</summary>
public abstract record IntegerBaseDef : NodeDef
{
    public string? Unit { get; init; }

    /// <summary>Representation. 없으면 null — 런타임은 PureNumber 로 본다.</summary>
    public Representation? Representation { get; init; }

    /// <summary>ValidValueSet — 허용 값 목록(';' 또는 공백 구분). 없으면 null.</summary>
    public IReadOnlyList<long>? ValidValueSet { get; init; }
}

/// <summary>
/// &lt;Integer&gt; — 리터럴 값이거나 다른 정수 노드로의 간접 참조.
/// 값 출처는 <see cref="Value"/> · <see cref="PValue"/> · (<see cref="PIndex"/> + <see cref="ValueIndexed"/>/<see cref="PValueIndexed"/> + <see cref="ValueDefault"/>/<see cref="PValueDefault"/>) 셋 중 하나.
/// </summary>
public sealed record IntegerDef : IntegerBaseDef
{
    public override NodeDefKind Kind => NodeDefKind.Integer;

    /// <summary>&lt;Value&gt; 리터럴.</summary>
    public long? Value { get; init; }

    /// <summary>&lt;pValue&gt; — 값을 주는 정수 노드.</summary>
    public string? PValue { get; init; }

    /// <summary>&lt;pValueCopy&gt; — 쓰기 시 같은 값을 더 써 넣을 노드들.</summary>
    public IReadOnlyList<string> PValueCopies { get; init; } = Array.Empty<string>();

    /// <summary>&lt;pIndex&gt; — <see cref="PValueIndexed"/> 중 하나를 고르는 정수 노드.</summary>
    public string? PIndex { get; init; }

    /// <summary>&lt;ValueIndexed Index="n"&gt; 리터럴 항들 — <see cref="PValueIndexed"/> 와 섞여 올 수 있다(인덱스별로 리터럴이거나 노드).</summary>
    public IReadOnlyList<ValueIndexedDef<long>> ValueIndexed { get; init; } = Array.Empty<ValueIndexedDef<long>>();

    /// <summary>&lt;pValueIndexed Index="n"&gt; 항들.</summary>
    public IReadOnlyList<PValueIndexedDef> PValueIndexed { get; init; } = Array.Empty<PValueIndexedDef>();

    /// <summary>&lt;ValueDefault&gt; — 인덱스가 어느 항에도 안 맞을 때의 리터럴 값. <see cref="PValueDefault"/> 와 택일.</summary>
    public long? ValueDefault { get; init; }

    /// <summary>&lt;pValueDefault&gt; — 인덱스가 어느 항에도 안 맞을 때의 값 노드.</summary>
    public string? PValueDefault { get; init; }

    public long? Min { get; init; }
    public string? PMin { get; init; }
    public long? Max { get; init; }
    public string? PMax { get; init; }
    public long? Inc { get; init; }
    public string? PInc { get; init; }
}

/// <summary>&lt;IntReg&gt; — 정수 레지스터.</summary>
public sealed record IntRegDef : IntegerBaseDef, IRegisterNodeDef
{
    public override NodeDefKind Kind => NodeDefKind.IntReg;
    public required RegisterSet RegisterSet { get; init; }

    /// <summary>기본 Unsigned. Signed 면 Length &lt; 8 일 때 부호 확장.</summary>
    public Sign Sign { get; init; } = Sign.Unsigned;

    /// <summary>기본 LittleEndian.</summary>
    public Endianess Endianess { get; init; } = Endianess.LittleEndian;
}

/// <summary>
/// &lt;MaskedIntReg&gt; 또는 &lt;StructReg&gt; 의 항목 — 레지스터 안의 비트 필드.
/// XML 의 &lt;Bit&gt; 는 <see cref="Lsb"/>=<see cref="Msb"/>=Bit 로 정규화해 두었고, 원문은 <see cref="Bit"/> 에 남긴다.
/// 비트 번호 규약: BigEndian 레지스터는 비트 0 이 레지스터의 최상위 비트, LittleEndian 은 최하위 비트 — 정규화는 런타임 몫.
/// </summary>
public sealed record MaskedIntRegDef : IntegerBaseDef, IRegisterNodeDef
{
    public override NodeDefKind Kind => NodeDefKind.MaskedIntReg;
    public required RegisterSet RegisterSet { get; init; }

    /// <summary>XML 에 &lt;Bit&gt; 로 적혔으면 그 값, LSB/MSB 로 적혔으면 null.</summary>
    public int? Bit { get; init; }

    /// <summary>필드의 LSB 비트 번호(Bit 지정이면 Bit 와 같다).</summary>
    public required int Lsb { get; init; }

    /// <summary>필드의 MSB 비트 번호(Bit 지정이면 Bit 와 같다).</summary>
    public required int Msb { get; init; }

    public Sign Sign { get; init; } = Sign.Unsigned;
    public Endianess Endianess { get; init; } = Endianess.LittleEndian;

    /// <summary>&lt;StructReg&gt; 의 &lt;StructEntry&gt; 에서 펼쳐진 정의인지.</summary>
    public bool IsStructEntry { get; init; }

    /// <summary>펼쳐진 정의라면 문서 안 StructReg 의 순번(0 부터) — 같은 번호는 같은 레지스터를 공유한다.</summary>
    public int? StructRegIndex { get; init; }
}

/// <summary>&lt;IntSwissKnife&gt; — 정수 수식. 읽기 전용.</summary>
public sealed record IntSwissKnifeDef : IntegerBaseDef, ISwissKnifeNodeDef
{
    public override NodeDefKind Kind => NodeDefKind.IntSwissKnife;
    public IReadOnlyList<FormulaVariableDef> Variables { get; init; } = Array.Empty<FormulaVariableDef>();
    public IReadOnlyList<FormulaConstantDef> Constants { get; init; } = Array.Empty<FormulaConstantDef>();
    public IReadOnlyList<FormulaExpressionDef> Expressions { get; init; } = Array.Empty<FormulaExpressionDef>();
    public required string Formula { get; init; }
}

/// <summary>&lt;IntConverter&gt; — 정수 양방향 변환. 값은 <see cref="PValue"/> 노드에 FormulaTo 로 쓰고 FormulaFrom 으로 읽는다.</summary>
public sealed record IntConverterDef : IntegerBaseDef, IConverterNodeDef
{
    public override NodeDefKind Kind => NodeDefKind.IntConverter;
    public IReadOnlyList<FormulaVariableDef> Variables { get; init; } = Array.Empty<FormulaVariableDef>();
    public IReadOnlyList<FormulaConstantDef> Constants { get; init; } = Array.Empty<FormulaConstantDef>();
    public IReadOnlyList<FormulaExpressionDef> Expressions { get; init; } = Array.Empty<FormulaExpressionDef>();
    public required string FormulaTo { get; init; }
    public required string FormulaFrom { get; init; }
    public string? PValue { get; init; }
    public Slope Slope { get; init; } = Slope.Automatic;
    public bool IsLinear { get; init; }
}

// ---------------------------------------------------------------------------------------------------
// 실수 계열
// ---------------------------------------------------------------------------------------------------

/// <summary>실수 인터페이스(IFloat)로 노출되는 네 종류의 공통 면.</summary>
public abstract record FloatBaseDef : NodeDef
{
    public string? Unit { get; init; }

    /// <summary>Representation. 없으면 null — 런타임은 PureNumber 로 본다.</summary>
    public Representation? Representation { get; init; }

    /// <summary>DisplayNotation. 없으면 null — 런타임은 Automatic 으로 본다.</summary>
    public DisplayNotation? DisplayNotation { get; init; }

    /// <summary>DisplayPrecision — 표시 소수 자릿수. 없으면 null.</summary>
    public int? DisplayPrecision { get; init; }
}

/// <summary>&lt;Float&gt; — 리터럴 값이거나 다른 실수/정수 노드로의 간접 참조. 값 출처 규칙은 <see cref="IntegerDef"/> 와 같다.</summary>
public sealed record FloatDef : FloatBaseDef
{
    public override NodeDefKind Kind => NodeDefKind.Float;
    public double? Value { get; init; }
    public string? PValue { get; init; }
    public IReadOnlyList<string> PValueCopies { get; init; } = Array.Empty<string>();
    public string? PIndex { get; init; }
    public IReadOnlyList<ValueIndexedDef<double>> ValueIndexed { get; init; } = Array.Empty<ValueIndexedDef<double>>();
    public IReadOnlyList<PValueIndexedDef> PValueIndexed { get; init; } = Array.Empty<PValueIndexedDef>();
    public double? ValueDefault { get; init; }
    public string? PValueDefault { get; init; }
    public double? Min { get; init; }
    public string? PMin { get; init; }
    public double? Max { get; init; }
    public string? PMax { get; init; }
    public double? Inc { get; init; }
    public string? PInc { get; init; }
}

/// <summary>&lt;FloatReg&gt; — IEEE 실수 레지스터(Length 4 또는 8).</summary>
public sealed record FloatRegDef : FloatBaseDef, IRegisterNodeDef
{
    public override NodeDefKind Kind => NodeDefKind.FloatReg;
    public required RegisterSet RegisterSet { get; init; }
    public Endianess Endianess { get; init; } = Endianess.LittleEndian;
}

/// <summary>&lt;SwissKnife&gt; — 실수 수식. 읽기 전용.</summary>
public sealed record SwissKnifeDef : FloatBaseDef, ISwissKnifeNodeDef
{
    public override NodeDefKind Kind => NodeDefKind.SwissKnife;
    public IReadOnlyList<FormulaVariableDef> Variables { get; init; } = Array.Empty<FormulaVariableDef>();
    public IReadOnlyList<FormulaConstantDef> Constants { get; init; } = Array.Empty<FormulaConstantDef>();
    public IReadOnlyList<FormulaExpressionDef> Expressions { get; init; } = Array.Empty<FormulaExpressionDef>();
    public required string Formula { get; init; }
}

/// <summary>&lt;Converter&gt; — 실수 양방향 변환.</summary>
public sealed record ConverterDef : FloatBaseDef, IConverterNodeDef
{
    public override NodeDefKind Kind => NodeDefKind.Converter;
    public IReadOnlyList<FormulaVariableDef> Variables { get; init; } = Array.Empty<FormulaVariableDef>();
    public IReadOnlyList<FormulaConstantDef> Constants { get; init; } = Array.Empty<FormulaConstantDef>();
    public IReadOnlyList<FormulaExpressionDef> Expressions { get; init; } = Array.Empty<FormulaExpressionDef>();
    public required string FormulaTo { get; init; }
    public required string FormulaFrom { get; init; }
    public string? PValue { get; init; }
    public Slope Slope { get; init; } = Slope.Automatic;
    public bool IsLinear { get; init; }
}

// ---------------------------------------------------------------------------------------------------
// 문자열 / 불리언 / 열거 / 명령 / 레지스터 / 포트
// ---------------------------------------------------------------------------------------------------

/// <summary>&lt;String&gt; — 리터럴 문자열이거나 StringReg 로의 간접 참조.</summary>
public sealed record StringDef : NodeDef
{
    public override NodeDefKind Kind => NodeDefKind.String;

    /// <summary>&lt;Value&gt; 리터럴(빈 문자열도 값이다). 요소가 없으면 null.</summary>
    public string? Value { get; init; }

    public string? PValue { get; init; }
}

/// <summary>&lt;StringReg&gt; — 고정 길이 문자열 레지스터(NUL 패딩).</summary>
public sealed record StringRegDef : NodeDef, IRegisterNodeDef
{
    public override NodeDefKind Kind => NodeDefKind.StringReg;
    public required RegisterSet RegisterSet { get; init; }
}

/// <summary>&lt;Boolean&gt; — 정수 노드(pValue) 위의 참/거짓. 참은 <see cref="OnValue"/>, 거짓은 <see cref="OffValue"/> 로 쓴다.</summary>
public sealed record BooleanDef : NodeDef
{
    public override NodeDefKind Kind => NodeDefKind.Boolean;

    /// <summary>&lt;Value&gt; 리터럴(true/false/1/0/Yes/No). <see cref="PValue"/> 와 택일.</summary>
    public bool? Value { get; init; }

    public string? PValue { get; init; }

    /// <summary>기본 1.</summary>
    public long OnValue { get; init; } = 1;

    /// <summary>기본 0.</summary>
    public long OffValue { get; init; }
}

/// <summary>
/// &lt;EnumEntry&gt; — 열거 항목. 모델의 Nodes 사전에도 들어가지만 등록 이름은 열거로 한정된다:
/// 항목 이름은 열거 안에서만 유일하고(여러 열거가 "Off" 같은 항목을 함께 쓰며, "Width" 처럼 피처 이름과 겹치기도 한다)
/// <see cref="NodeDef.Name"/> 은 "EnumEntry_{열거}_{항목}" 이다. XML Name 이 이미 그 접두를 달고 있으면 접두를 뗀 나머지가 <see cref="EntryName"/> 이다.
/// 열거 이름의 '_' 때문에 다른 열거의 항목과 한정 이름이 겹치면 "#n" 이 붙는다 — 런타임은 항목을 <see cref="EnumerationDef.Entries"/> 와 <see cref="Symbolic"/> 으로 찾는다.
/// </summary>
public sealed record EnumEntryDef : NodeDef
{
    public override NodeDefKind Kind => NodeDefKind.EnumEntry;

    /// <summary>열거 안에서의 항목 이름 — XML Name 속성에서 "EnumEntry_{열거}_" 접두를 뗀 것(접두가 없었으면 원문 그대로).</summary>
    public required string EntryName { get; init; }

    /// <summary>레지스터에 쓰이는 정수 값.</summary>
    public required long Value { get; init; }

    /// <summary>NumericValue — 물리량이 있는 열거(예: 프레임레이트 프리셋). 없으면 null.</summary>
    public double? NumericValue { get; init; }

    /// <summary>사용자에게 보이는 이름. XML 에 &lt;Symbolic&gt; 이 없으면 <see cref="EntryName"/>.</summary>
    public required string Symbolic { get; init; }

    /// <summary>IsSelfClearing — 쓰면 장치가 스스로 되돌리는 값. 기본 false.</summary>
    public bool IsSelfClearing { get; init; }
}

/// <summary>&lt;Enumeration&gt; — 정수 노드(pValue) 값에 이름을 붙인 것.</summary>
public sealed record EnumerationDef : NodeDef
{
    public override NodeDefKind Kind => NodeDefKind.Enumeration;

    /// <summary>&lt;Value&gt; 정수 리터럴. <see cref="PValue"/> 와 택일.</summary>
    public long? Value { get; init; }

    public string? PValue { get; init; }

    /// <summary>XML 순서 그대로. 같은 인스턴스가 모델의 Nodes 사전에도 들어 있다.</summary>
    public IReadOnlyList<EnumEntryDef> Entries { get; init; } = Array.Empty<EnumEntryDef>();

    public Representation? Representation { get; init; }
}

/// <summary>&lt;Command&gt; — <see cref="PValue"/> 노드에 <see cref="CommandValue"/>(또는 <see cref="PCommandValue"/> 값)를 써서 실행.</summary>
public sealed record CommandDef : NodeDef
{
    public override NodeDefKind Kind => NodeDefKind.Command;

    /// <summary>&lt;Value&gt; 리터럴 — 레지스터 없는 명령(실행이 곧 완료).</summary>
    public long? Value { get; init; }

    public string? PValue { get; init; }

    /// <summary>실행 시 쓸 값. 없고 <see cref="PCommandValue"/> 도 없으면 런타임은 1 로 본다.</summary>
    public long? CommandValue { get; init; }

    public string? PCommandValue { get; init; }
}

/// <summary>&lt;Register&gt; — 원시 바이트 레지스터.</summary>
public sealed record RegisterDef : NodeDef, IRegisterNodeDef
{
    public override NodeDefKind Kind => NodeDefKind.Register;
    public required RegisterSet RegisterSet { get; init; }
}

/// <summary>&lt;Port&gt; — 레지스터 노드가 붙는 전송 경계. 청크 어댑터용 속성은 담기만 하고 런타임은 무시할 수 있다.</summary>
public sealed record PortDef : NodeDef
{
    public override NodeDefKind Kind => NodeDefKind.Port;

    /// <summary>ChunkID — 16진 문자열을 읽은 값. 없으면 null.</summary>
    public ulong? ChunkId { get; init; }

    /// <summary>pChunkID — 청크 ID 를 주는 Integer 노드.</summary>
    public string? PChunkId { get; init; }

    /// <summary>SwapEndianess Yes/No. 기본 false.</summary>
    public bool IsEndianessSwapped { get; init; }

    /// <summary>CacheChunkData Yes/No. 기본 false.</summary>
    public bool IsChunkDataCached { get; init; }
}
