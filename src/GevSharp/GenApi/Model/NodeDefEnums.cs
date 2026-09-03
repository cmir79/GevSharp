namespace GevSharp.GenApi.Model;

/// <summary>
/// 노드 정의가 어느 XML 요소에서 왔는지. 런타임은 이 값으로 구현 클래스를 고른다.
/// StructReg 는 항목(StructEntry)마다 <see cref="MaskedIntReg"/> 정의로 펼쳐지므로 자기 값이 없다(<see cref="MaskedIntRegDef.IsStructEntry"/> 로 구분).
/// 스키마에 없는 요소는 <see cref="Unknown"/> 으로 남기고 경고만 낸다 — 파서는 알 수 없는 요소 때문에 죽지 않는다.
/// </summary>
public enum NodeDefKind
{
    Category,
    Integer,
    IntReg,
    MaskedIntReg,
    IntSwissKnife,
    IntConverter,
    Float,
    FloatReg,
    SwissKnife,
    Converter,
    String,
    StringReg,
    Boolean,
    Enumeration,
    EnumEntry,
    Command,
    Register,
    Port,
    /// <summary>제네릭 &lt;Node&gt; — 값이 없는 순수 노드(별칭·그룹핑 용도).</summary>
    Node,
    Unknown,
}

/// <summary>Name 속성이 속한 이름 공간(XML NameSpace 속성). 표준 피처(SFNC)는 Standard, 벤더 고유는 Custom(기본).</summary>
public enum NodeNameSpace
{
    Custom,
    Standard,
}

/// <summary>정수 레지스터의 부호 해석(XML &lt;Sign&gt;). 기본 Unsigned.</summary>
public enum Sign
{
    Unsigned,
    Signed,
}

/// <summary>
/// 레지스터 바이트 순서(XML &lt;Endianess&gt; — 스키마 철자 그대로). 기본 LittleEndian.
/// GVCP 장치는 실제로 대부분 BigEndian 을 명시한다. MaskedIntReg 의 LSB/MSB 비트 번호 규약도 이 값을 따른다.
/// </summary>
public enum Endianess
{
    LittleEndian,
    BigEndian,
}

/// <summary>레지스터 캐시 정책(XML &lt;Cachable&gt;). 기본 WriteThrough.</summary>
public enum Cachable
{
    NoCache,
    WriteThrough,
    WriteAround,
}

/// <summary>Converter 변환식의 단조성(XML &lt;Slope&gt;) — Min/Max 를 어느 방향으로 변환할지 정한다. 기본 Automatic.</summary>
public enum Slope
{
    Automatic,
    Increasing,
    Decreasing,
    Varying,
}

/// <summary>실수 표시 표기(XML &lt;DisplayNotation&gt;). 기본 Automatic.</summary>
public enum DisplayNotation
{
    Automatic,
    Fixed,
    Scientific,
}
