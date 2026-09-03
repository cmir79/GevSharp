using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace GevSharp.GenApi.Model;

/// <summary>
/// GenApi XML 텍스트 → <see cref="GenApiXmlModel"/>. 요소는 로컬 이름으로만 맞춘다(네임스페이스 1.0/1.1/없음 모두 동일 취급).
/// - &lt;Group&gt; 은 깊이에 관계없이 투명하게 풀어 안의 노드를 최상위로 등록한다.
/// - &lt;StructReg&gt; 는 &lt;StructEntry&gt; 마다 <see cref="MaskedIntRegDef"/> 하나로 펼친다(레지스터 집합·Endianess·Sign 과 공통 자식 전부를 물려주고 항목 쪽이 우선).
/// - 모르는 요소 종류는 <see cref="UnknownDef"/> + 경고, 모르는 자식 요소는 경고. 파서는 경고로 죽지 않는다.
/// - &lt;EnumEntry&gt; 는 "EnumEntry_{열거}_{항목}" 으로 한정해 등록한다(항목 이름은 열거 안에서만 유일하다).
/// - 레지스터 안의 인라인 &lt;IntSwissKnife&gt; 는 소유 레지스터의 <see cref="RegisterSet.AddressSwissKnives"/> 에 담고, Name 이 있으면 노드로도 등록한다.
/// - 이름 중복·Name 누락·루트 누락·필수 루트 속성 누락·리터럴 해석 실패·<see cref="MaxElementDepth"/> 초과는 <see cref="GenApiException"/>.
/// 상태가 없어 여러 스레드가 동시에 불러도 된다.
/// </summary>
public static class GenApiXmlParser
{
    private const string LogSrc = "GenApi.Model";

    public const string NamespaceV10 = "http://www.genicam.org/GenApi/Version_1_0";
    public const string NamespaceV11 = "http://www.genicam.org/GenApi/Version_1_1";

    /// <summary>
    /// 허용하는 요소 중첩 깊이(루트가 1). 실제 장치 XML 은 Group 몇 단 + 노드 + 자식으로 10 안팎이다.
    /// 문서 자체는 재귀 없이 읽히지만 Group 풀기와 요소 텍스트 읽기는 깊이만큼 호출 스택을 쓰므로,
    /// 장치가 보내온 문서가 지나치게 깊으면 스택 넘침(잡을 수 없는 프로세스 종료) 대신 <see cref="GenApiException"/> 으로 거절한다.
    /// </summary>
    public const int MaxElementDepth = 128;

    private static readonly HashSet<string> KnownKinds = new(StringComparer.Ordinal)
    {
        "Category", "Integer", "IntReg", "MaskedIntReg", "IntSwissKnife", "IntConverter",
        "Float", "FloatReg", "SwissKnife", "Converter", "String", "StringReg", "Boolean",
        "Enumeration", "Command", "Register", "Port", "Node",
    };

    /// <summary>XML 텍스트를 파싱한다. 문서가 XML 로서 깨져 있으면 <see cref="GenApiException"/>(원인은 InnerException).</summary>
    public static GenApiXmlModel Parse(string xml)
    {
        if (xml is null) throw new ArgumentNullException(nameof(xml));
        // 바이트를 그대로 문자열로 옮긴 경우 앞에 BOM 문자가 남을 수 있다 — XML 파서는 이를 잘못된 데이터로 본다.
        if (xml.Length > 0 && xml[0] == '﻿') xml = xml.Substring(1);

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new GenApiException("Malformed GenApi XML: " + ex.Message, null, ex);
        }
        return Parse(doc);
    }

    /// <summary>이미 읽어 둔 문서를 파싱한다.</summary>
    public static GenApiXmlModel Parse(XDocument doc)
    {
        if (doc is null) throw new ArgumentNullException(nameof(doc));
        var root = doc.Root ?? throw new GenApiException("GenApi XML has no root element.");
        if (root.Name.LocalName != "RegisterDescription")
            throw new GenApiException($"GenApi XML root element must be <RegisterDescription>, found <{root.Name.LocalName}>.");

        CheckDepth(root);

        var ctx = new ParseCtx();
        var ns = root.Name.NamespaceName;
        if (ns.Length != 0 && ns != NamespaceV10 && ns != NamespaceV11)
            ctx.Warn($"Unexpected XML namespace '{ns}' on <RegisterDescription>; elements are matched by local name only.");

        var info = ReadInfo(root);
        ReadChildren(root, ctx);
        return new GenApiXmlModel(info, ctx.Nodes, ctx.Warnings);
    }

    /// <summary>
    /// 요소 중첩이 <see cref="MaxElementDepth"/> 를 넘으면 거절한다. 재귀 없이(명시적 스택) 재므로 아무리 깊은 문서도 여기서는 안전하다.
    /// 이 검사를 지난 문서는 뒤의 Group 재귀와 XElement.Value(자손 재귀) 가 쓰는 스택 깊이가 한계 이내로 보장된다.
    /// </summary>
    private static void CheckDepth(XElement root)
    {
        var stack = new Stack<KeyValuePair<XElement, int>>();
        stack.Push(new KeyValuePair<XElement, int>(root, 1));
        while (stack.Count > 0)
        {
            var top = stack.Pop();
            var depth = top.Value;
            if (depth > MaxElementDepth)
            {
                var el = top.Key;
                var name = el.Attribute("Name")?.Value;
                throw new GenApiException(
                    $"GenApi XML element nesting exceeds {MaxElementDepth} levels at <{el.Name.LocalName}>" + (name is null ? "." : $" '{name}'."), name);
            }
            foreach (var child in top.Key.Elements())
                stack.Push(new KeyValuePair<XElement, int>(child, depth + 1));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 파싱 상태
    // ---------------------------------------------------------------------------------------------

    private sealed class ParseCtx
    {
        public readonly List<NodeDef> Nodes = new();
        public readonly List<string> Warnings = new();
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        public int StructRegCount;
        public int UnknownCount;

        public void Add(NodeDef def)
        {
            if (!_names.Add(def.Name))
                throw new GenApiException($"Duplicate node name '{def.Name}' (<{def.Kind}>).", def.Name);
            Nodes.Add(def);
        }

        /// <summary>이미 등록된 이름인지 — 합성 이름의 충돌을 예외 대신 다른 이름으로 피할 때 쓴다.</summary>
        public bool IsNameTaken(string name) => _names.Contains(name);

        public void Warn(string message)
        {
            Warnings.Add(message);
            GevLog.Warn(LogSrc, message);
        }
    }

    private sealed class FormulaParts
    {
        public IReadOnlyList<FormulaVariableDef> Variables = Array.Empty<FormulaVariableDef>();
        public IReadOnlyList<FormulaConstantDef> Constants = Array.Empty<FormulaConstantDef>();
        public IReadOnlyList<FormulaExpressionDef> Expressions = Array.Empty<FormulaExpressionDef>();
    }

    // ---------------------------------------------------------------------------------------------
    // 루트
    // ---------------------------------------------------------------------------------------------

    private static RegisterDescriptionInfo ReadInfo(XElement root)
    {
        string? Optional(string attr)
        {
            var v = root.Attribute(attr)?.Value.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }

        string Required(string attr)
            => Optional(attr) ?? throw new GenApiException($"<RegisterDescription> is missing the required attribute '{attr}'.");

        int RequiredInt(string attr)
        {
            var v = Required(attr);
            if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i))
                throw new GenApiException($"<RegisterDescription> attribute '{attr}' is not a non-negative integer: '{v}'.");
            return i;
        }

        int OptionalInt(string attr, int fallback)
        {
            var v = Optional(attr);
            if (v is null) return fallback;
            if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i))
                throw new GenApiException($"<RegisterDescription> attribute '{attr}' is not a non-negative integer: '{v}'.");
            return i;
        }

        return new RegisterDescriptionInfo(
            ModelName: Required("ModelName"),
            VendorName: Required("VendorName"),
            ToolTip: Optional("ToolTip"),
            StandardNameSpace: Optional("StandardNameSpace"),
            SchemaMajorVersion: RequiredInt("SchemaMajorVersion"),
            SchemaMinorVersion: RequiredInt("SchemaMinorVersion"),
            SchemaSubMinorVersion: OptionalInt("SchemaSubMinorVersion", 0),
            MajorVersion: RequiredInt("MajorVersion"),
            MinorVersion: RequiredInt("MinorVersion"),
            SubMinorVersion: RequiredInt("SubMinorVersion"),
            ProductGuid: Optional("ProductGuid"),
            VersionGuid: Optional("VersionGuid"));
    }

    // ---------------------------------------------------------------------------------------------
    // 노드 순회 — Group 재귀, StructReg 펼침
    // ---------------------------------------------------------------------------------------------

    /// <summary>Group 은 재귀로 푼다 — 재귀 깊이는 <see cref="CheckDepth"/> 가 <see cref="MaxElementDepth"/> 로 묶어 두었다.</summary>
    private static void ReadChildren(XElement parent, ParseCtx ctx)
    {
        foreach (var el in parent.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "Group":
                    ReadChildren(el, ctx);
                    break;
                case "StructReg":
                    ExpandStructReg(el, ctx);
                    break;
                default:
                {
                    var extra = new List<NodeDef>();
                    var def = ReadNode(el, ctx, extra);
                    ctx.Add(def);
                    foreach (var e in extra) ctx.Add(e);
                    break;
                }
            }
        }
    }

    /// <summary>노드 요소 하나를 정의로 옮긴다. 함께 등록할 중첩 정의(Enumeration 의 EnumEntry, 이름 있는 인라인 IntSwissKnife)는 extra 로 돌려준다.</summary>
    private static NodeDef ReadNode(XElement el, ParseCtx ctx, List<NodeDef> extra)
    {
        var local = el.Name.LocalName;
        var name = el.Attribute("Name")?.Value.Trim();

        if (!KnownKinds.Contains(local))
        {
            if (string.IsNullOrEmpty(name)) name = $"Unknown{ctx.UnknownCount}_{local}";
            ctx.UnknownCount++;
            ctx.Warn($"Unknown element <{local}> (Name='{name}') is not a supported node kind; kept as a placeholder.");
            return new UnknownDef { Name = name!, ElementName = local };
        }

        if (string.IsNullOrEmpty(name))
            throw new GenApiException($"<{local}> element has no Name attribute.");

        var r = new GenApiElementReader(el, name!, ctx.Warn);
        NodeDef def = local switch
        {
            "Category" => new CategoryDef { Name = name!, PFeatures = r.TextList("pFeature") },
            "Integer" => ReadInteger(r),
            "IntReg" => ReadIntReg(r, ctx, extra),
            "MaskedIntReg" => ReadMaskedIntReg(r, ctx, extra),
            "IntSwissKnife" => ReadIntSwissKnife(r),
            "IntConverter" => ReadIntConverter(r),
            "Float" => ReadFloat(r),
            "FloatReg" => ReadFloatReg(r, ctx, extra),
            "SwissKnife" => ReadSwissKnife(r),
            "Converter" => ReadConverter(r),
            "String" => ReadString(r),
            "StringReg" => new StringRegDef { Name = name!, RegisterSet = ReadRegisterSet(r, ctx, extra) },
            "Boolean" => ReadBoolean(r),
            "Enumeration" => ReadEnumeration(r, ctx, extra),
            "Command" => ReadCommand(r),
            "Register" => new RegisterDef { Name = name!, RegisterSet = ReadRegisterSet(r, ctx, extra) },
            "Port" => ReadPort(r),
            "Node" => new GenericNodeDef { Name = name! },
            _ => throw new GenApiException($"Unhandled node kind <{local}>.", name),
        };

        def = ApplyCommon(def, r);
        WarnUnconsumed(r, ctx);
        return def;
    }

    /// <summary>
    /// 모든 노드 종류가 공유하는 속성·자식을 채운다. 파생 형은 유지된다(record with).
    /// inherit 가 있으면(StructReg → StructEntry) 요소에 없는 항목은 거기서 물려받고, 목록 항목은 물려받은 것 뒤에 자기 것을 잇는다.
    /// </summary>
    private static NodeDef ApplyCommon(NodeDef def, GenApiElementReader r, NodeDef? inherit = null)
    {
        r.Consume("Extension");
        // EventID 는 접두 없는 16진이다 — 원문과 함께 읽은 값도 담아 런타임이 10진으로 잘못 다시 읽을 여지를 없앤다.
        var eventId = r.Ref("EventID");
        ulong? eventIdValue = eventId is null ? null : GenApiLiteral.ParseHex(eventId, "EventID", r.NodeName);
        var nameSpace = r.Attr("NameSpace");

        return def with
        {
            NameSpace = nameSpace is not null ? ParseNameSpace(nameSpace, r.NodeName) : inherit?.NameSpace ?? NodeNameSpace.Custom,
            Comment = r.Attr("Comment") ?? inherit?.Comment,
            ToolTip = r.Text("ToolTip") ?? inherit?.ToolTip,
            Description = r.Text("Description") ?? inherit?.Description,
            DisplayName = r.Text("DisplayName") ?? inherit?.DisplayName,
            DocuUrl = r.Text("DocuURL") ?? inherit?.DocuUrl,
            Visibility = ParseVisibility(r.Text("Visibility"), r.NodeName) ?? inherit?.Visibility ?? Visibility.Beginner,
            EventId = eventId ?? inherit?.EventId,
            EventIdValue = eventIdValue ?? inherit?.EventIdValue,
            PIsImplemented = r.Ref("pIsImplemented") ?? inherit?.PIsImplemented,
            PIsAvailable = r.Ref("pIsAvailable") ?? inherit?.PIsAvailable,
            PIsLocked = r.Ref("pIsLocked") ?? inherit?.PIsLocked,
            PBlockPolling = r.Ref("pBlockPolling") ?? inherit?.PBlockPolling,
            PInvalidators = MergeLists(inherit?.PInvalidators, r.TextList("pInvalidator")),
            ImposedAccessMode = ParseAccessMode(r.Text("ImposedAccessMode"), r.NodeName) ?? inherit?.ImposedAccessMode,
            PAlias = r.Ref("pAlias") ?? inherit?.PAlias,
            PCastAlias = r.Ref("pCastAlias") ?? inherit?.PCastAlias,
            IsStreamable = r.YesNo("Streamable") ?? inherit?.IsStreamable ?? false,
            PErrors = MergeLists(inherit?.PErrors, r.TextList("pError")),
            IsDeprecated = r.YesNo("IsDeprecated") ?? inherit?.IsDeprecated ?? false,
            PollingTimeMs = r.Int64("PollingTime") ?? inherit?.PollingTimeMs,
            PSelected = MergeLists(inherit?.PSelected, r.TextList("pSelected")),
        };
    }

    /// <summary>물려받은 목록 뒤에 자기 목록을 잇는다. 한쪽이 비면 다른 쪽을 그대로 돌려준다.</summary>
    private static IReadOnlyList<string> MergeLists(IReadOnlyList<string>? inherited, IReadOnlyList<string> own)
    {
        if (inherited is null || inherited.Count == 0) return own;
        if (own.Count == 0) return inherited;
        var merged = new List<string>(inherited.Count + own.Count);
        merged.AddRange(inherited);
        merged.AddRange(own);
        return merged;
    }

    private static void WarnUnconsumed(GenApiElementReader r, ParseCtx ctx)
    {
        foreach (var n in r.UnconsumedNames())
            ctx.Warn($"Unknown child element <{n}> in <{r.LocalName}> '{r.NodeName}' was ignored.");
    }

    // ---------------------------------------------------------------------------------------------
    // 레지스터 집합 · 수식 부품 · 비트 필드
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// 레지스터 노드 공통의 주소·길이·접근 정보를 읽는다.
    /// 인라인 &lt;IntSwissKnife&gt; 는 반환값의 <see cref="RegisterSet.AddressSwissKnives"/> 에 담고, Name 속성이 있으면 같은 인스턴스를 extra 에도 넣어
    /// 노드로 등록되게 한다 — 이름 있는 노드는 다른 노드가 p* 로 가리킬 수 있어야 한다. Name 이 없으면 "{소유노드}_AddrSwissKnife{n}" 을 합성하고 등록하지 않는다.
    /// </summary>
    private static RegisterSet ReadRegisterSet(GenApiElementReader r, ParseCtx ctx, List<NodeDef> extra)
    {
        var owner = r.NodeName;

        // 주소 리터럴은 비어 있어도 건너뛰지 않는다 — 빈 <Address/> 가 조용히 사라지면 주소 0 의 레지스터가 된다.
        var addrElems = r.Elements("Address");
        var addresses = new long[addrElems.Count];
        for (var i = 0; i < addrElems.Count; i++)
            addresses[i] = GenApiLiteral.ParseInt64(addrElems[i].Value.Trim(), "Address", owner);

        var pAddresses = r.TextList("pAddress");

        var pIndexElems = r.Elements("pIndex");
        var pIndexes = new PIndexDef[pIndexElems.Count];
        for (var i = 0; i < pIndexElems.Count; i++)
        {
            var e = pIndexElems[i];
            var idxNode = e.Value.Trim();
            if (idxNode.Length == 0)
                throw new GenApiException($"<pIndex> of register node '{owner}' has no index node name.", owner);
            var offsetAttr = e.Attribute("Offset")?.Value.Trim();
            var pOffsetAttr = e.Attribute("pOffset")?.Value.Trim();
            pIndexes[i] = new PIndexDef
            {
                PNode = idxNode,
                Offset = string.IsNullOrEmpty(offsetAttr) ? null : GenApiLiteral.ParseInt64(offsetAttr, "pIndex Offset", owner),
                POffset = string.IsNullOrEmpty(pOffsetAttr) ? null : pOffsetAttr,
            };
        }

        var knifeElems = r.Elements("IntSwissKnife");
        var knives = new IntSwissKnifeDef[knifeElems.Count];
        for (var i = 0; i < knifeElems.Count; i++)
        {
            var e = knifeElems[i];
            var skName = e.Attribute("Name")?.Value.Trim();
            var isNamed = !string.IsNullOrEmpty(skName);
            if (!isNamed) skName = $"{owner}_AddrSwissKnife{i}";
            var sr = new GenApiElementReader(e, skName!, ctx.Warn);
            var sk = (IntSwissKnifeDef)ApplyCommon(ReadIntSwissKnife(sr), sr);
            WarnUnconsumed(sr, ctx);
            knives[i] = sk;
            if (isNamed) extra.Add(sk);
        }

        var length = r.Int64("Length");
        var pLength = r.Ref("pLength");
        if (length is null && pLength is null)
            throw new GenApiException($"Register node '{owner}' has neither Length nor pLength.", owner);
        if (length is <= 0)
            throw new GenApiException($"Register node '{owner}' has a non-positive Length {length}.", owner);

        return new RegisterSet
        {
            Addresses = addresses,
            PAddresses = pAddresses,
            PIndexes = pIndexes,
            AddressSwissKnives = knives,
            Length = length,
            PLength = pLength,
            AccessMode = ParseAccessMode(r.Text("AccessMode"), owner) ?? AccessMode.ReadWrite,
            PPort = r.Ref("pPort"),
            Cachable = ParseCachable(r.Text("Cachable"), owner) ?? Cachable.WriteThrough,
        };
    }

    private static FormulaParts ReadFormulaParts(GenApiElementReader r)
    {
        var owner = r.NodeName;
        var parts = new FormulaParts();

        var varElems = r.Elements("pVariable");
        if (varElems.Count > 0)
        {
            var vars = new FormulaVariableDef[varElems.Count];
            for (var i = 0; i < varElems.Count; i++)
            {
                var e = varElems[i];
                var vn = RequiredNameAttr(e, "pVariable", owner);
                var target = e.Value.Trim();
                if (target.Length == 0)
                    throw new GenApiException($"<pVariable Name=\"{vn}\"> of node '{owner}' has no node name.", owner);
                vars[i] = new FormulaVariableDef(vn, target);
            }
            parts.Variables = vars;
        }

        var constElems = r.Elements("Constant");
        if (constElems.Count > 0)
        {
            var consts = new FormulaConstantDef[constElems.Count];
            for (var i = 0; i < constElems.Count; i++)
            {
                var e = constElems[i];
                var cn = RequiredNameAttr(e, "Constant", owner);
                var text = e.Value.Trim();
                consts[i] = new FormulaConstantDef
                {
                    Name = cn,
                    Text = text,
                    IntValue = GenApiLiteral.TryParseInt64(text, out var iv) ? iv : null,
                    DoubleValue = GenApiLiteral.ParseDouble(text, $"Constant '{cn}'", owner),
                };
            }
            parts.Constants = consts;
        }

        var exprElems = r.Elements("Expression");
        if (exprElems.Count > 0)
        {
            var exprs = new FormulaExpressionDef[exprElems.Count];
            for (var i = 0; i < exprElems.Count; i++)
            {
                var e = exprElems[i];
                var en = RequiredNameAttr(e, "Expression", owner);
                var text = e.Value.Trim();
                if (text.Length == 0)
                    throw new GenApiException($"<Expression Name=\"{en}\"> of node '{owner}' is empty.", owner);
                exprs[i] = new FormulaExpressionDef(en, text);
            }
            parts.Expressions = exprs;
        }

        return parts;
    }

    private static string RequiredNameAttr(XElement e, string what, string owner)
    {
        var n = e.Attribute("Name")?.Value.Trim();
        if (string.IsNullOrEmpty(n))
            throw new GenApiException($"<{what}> of node '{owner}' has no Name attribute.", owner);
        return n!;
    }

    private static string RequiredFormula(GenApiElementReader r, string localName)
    {
        var f = r.Text(localName);
        if (string.IsNullOrEmpty(f))
            throw new GenApiException($"<{r.LocalName}> '{r.NodeName}' has no {localName}.", r.NodeName);
        return f!;
    }

    /// <summary>Bit 하나이거나 LSB/MSB 쌍이거나 — 둘 다 없거나 섞이면 오류. 반환은 (원문 Bit, Lsb, Msb).</summary>
    private static (int? Bit, int Lsb, int Msb) ReadBits(GenApiElementReader r)
    {
        var bit = r.Int32("Bit");
        var lsb = r.Int32("LSB");
        var msb = r.Int32("MSB");
        var owner = r.NodeName;

        if (bit is not null)
        {
            if (lsb is not null || msb is not null)
                throw new GenApiException($"<{r.LocalName}> '{owner}' mixes Bit with LSB/MSB.", owner);
            CheckBitRange(bit.Value, "Bit", owner);
            return (bit, bit.Value, bit.Value);
        }

        if (lsb is null || msb is null)
            throw new GenApiException($"<{r.LocalName}> '{owner}' needs either Bit or both LSB and MSB.", owner);
        CheckBitRange(lsb.Value, "LSB", owner);
        CheckBitRange(msb.Value, "MSB", owner);
        return (null, lsb.Value, msb.Value);
    }

    private static void CheckBitRange(int v, string what, string owner)
    {
        if (v < 0 || v > 63)
            throw new GenApiException($"{what} {v} of node '{owner}' is outside 0..63.", owner);
    }

    private static IReadOnlyList<long>? ReadValidValueSet(GenApiElementReader r)
    {
        var t = r.Text("ValidValueSet");
        if (t is null) return null;
        var items = t.Split(new[] { ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var values = new long[items.Length];
        for (var i = 0; i < items.Length; i++)
            values[i] = GenApiLiteral.ParseInt64(items[i], "ValidValueSet", r.NodeName);
        return values;
    }

    /// <summary>ValueIndexed/pValueIndexed 의 Index 속성 — 없으면 오류.</summary>
    private static long ReadIndexAttr(XElement e, string owner)
    {
        var idxAttr = e.Attribute("Index")?.Value.Trim();
        if (string.IsNullOrEmpty(idxAttr))
            throw new GenApiException($"<{e.Name.LocalName}> of node '{owner}' has no Index attribute.", owner);
        return GenApiLiteral.ParseInt64(idxAttr, e.Name.LocalName + " Index", owner);
    }

    private static IReadOnlyList<PValueIndexedDef> ReadPValueIndexed(GenApiElementReader r)
    {
        var elems = r.Elements("pValueIndexed");
        if (elems.Count == 0) return Array.Empty<PValueIndexedDef>();
        var list = new PValueIndexedDef[elems.Count];
        for (var i = 0; i < elems.Count; i++)
        {
            var e = elems[i];
            var index = ReadIndexAttr(e, r.NodeName);
            var target = e.Value.Trim();
            if (target.Length == 0)
                throw new GenApiException($"<pValueIndexed Index=\"{index}\"> of node '{r.NodeName}' has no node name.", r.NodeName);
            list[i] = new PValueIndexedDef(index, target);
        }
        return list;
    }

    private static IReadOnlyList<ValueIndexedDef<long>> ReadValueIndexedInt64(GenApiElementReader r)
    {
        var elems = r.Elements("ValueIndexed");
        if (elems.Count == 0) return Array.Empty<ValueIndexedDef<long>>();
        var list = new ValueIndexedDef<long>[elems.Count];
        for (var i = 0; i < elems.Count; i++)
        {
            var e = elems[i];
            list[i] = new ValueIndexedDef<long>(ReadIndexAttr(e, r.NodeName), GenApiLiteral.ParseInt64(e.Value, "ValueIndexed", r.NodeName));
        }
        return list;
    }

    private static IReadOnlyList<ValueIndexedDef<double>> ReadValueIndexedDouble(GenApiElementReader r)
    {
        var elems = r.Elements("ValueIndexed");
        if (elems.Count == 0) return Array.Empty<ValueIndexedDef<double>>();
        var list = new ValueIndexedDef<double>[elems.Count];
        for (var i = 0; i < elems.Count; i++)
        {
            var e = elems[i];
            list[i] = new ValueIndexedDef<double>(ReadIndexAttr(e, r.NodeName), GenApiLiteral.ParseDouble(e.Value, "ValueIndexed", r.NodeName));
        }
        return list;
    }

    private static void RequireValueSource(GenApiElementReader r, bool hasAny)
    {
        if (!hasAny)
            throw new GenApiException($"<{r.LocalName}> '{r.NodeName}' has no value source (Value, pValue, ValueIndexed/pValueIndexed or ValueDefault/pValueDefault).", r.NodeName);
    }

    /// <summary>
    /// 인덱스로 고르는 값(ValueIndexed/pValueIndexed)은 pIndex 가 있어야 한 슬롯을 고를 수 있다. 슬롯이 유일한 값 출처인데
    /// pIndex 가 없으면 값 출처 검사는 통과하지만 어느 슬롯도 열리지 않아 런타임이 조용히 0 을 내놓는다 — 그 모양만 거절한다.
    /// <para>
    /// Value·pValue·ValueDefault·pValueDefault 중 하나라도 있으면 읽기는 그쪽을 타 제 값을 내므로 건드리지 않는다.
    /// 파싱이 던지면 노드 하나가 아니라 노드맵 전체가 무너지므로, 정말로 값을 못 내는 모양에만 건다.
    /// </para>
    /// </summary>
    private static void RequireIndexForIndexedValues(GenApiElementReader r, bool indexedIsOnlySource, string? pIndex)
    {
        if (indexedIsOnlySource && pIndex is null)
            throw new GenApiException($"<{r.LocalName}> '{r.NodeName}' has ValueIndexed/pValueIndexed as its only value source but no pIndex to select one of them.", r.NodeName);
    }

    // ---------------------------------------------------------------------------------------------
    // 종류별 읽기
    // ---------------------------------------------------------------------------------------------

    private static IntegerDef ReadInteger(GenApiElementReader r)
    {
        var def = new IntegerDef
        {
            Name = r.NodeName,
            Value = r.Int64("Value"),
            PValue = r.Ref("pValue"),
            PValueCopies = r.TextList("pValueCopy"),
            PIndex = r.Ref("pIndex"),
            ValueIndexed = ReadValueIndexedInt64(r),
            PValueIndexed = ReadPValueIndexed(r),
            ValueDefault = r.Int64("ValueDefault"),
            PValueDefault = r.Ref("pValueDefault"),
            Min = r.Int64("Min"),
            PMin = r.Ref("pMin"),
            Max = r.Int64("Max"),
            PMax = r.Ref("pMax"),
            Inc = r.Int64("Inc"),
            PInc = r.Ref("pInc"),
            Unit = r.Text("Unit"),
            Representation = ParseRepresentation(r.Text("Representation"), r.NodeName),
            ValidValueSet = ReadValidValueSet(r),
        };
        RequireValueSource(r, def.Value is not null || def.PValue is not null
            || def.ValueIndexed.Count > 0 || def.PValueIndexed.Count > 0 || def.ValueDefault is not null || def.PValueDefault is not null);
        RequireIndexForIndexedValues(r,
            (def.ValueIndexed.Count > 0 || def.PValueIndexed.Count > 0)
            && def.Value is null && def.PValue is null && def.ValueDefault is null && def.PValueDefault is null,
            def.PIndex);
        return def;
    }

    private static IntRegDef ReadIntReg(GenApiElementReader r, ParseCtx ctx, List<NodeDef> extra) => new()
    {
        Name = r.NodeName,
        RegisterSet = ReadRegisterSet(r, ctx, extra),
        Sign = ParseSign(r.Text("Sign"), r.NodeName) ?? Sign.Unsigned,
        Endianess = ParseEndianess(r.Text("Endianess"), r.NodeName) ?? Endianess.LittleEndian,
        Unit = r.Text("Unit"),
        Representation = ParseRepresentation(r.Text("Representation"), r.NodeName),
        ValidValueSet = ReadValidValueSet(r),
    };

    private static MaskedIntRegDef ReadMaskedIntReg(GenApiElementReader r, ParseCtx ctx, List<NodeDef> extra)
    {
        var rs = ReadRegisterSet(r, ctx, extra);
        var (bit, lsb, msb) = ReadBits(r);
        return new MaskedIntRegDef
        {
            Name = r.NodeName,
            RegisterSet = rs,
            Bit = bit,
            Lsb = lsb,
            Msb = msb,
            Sign = ParseSign(r.Text("Sign"), r.NodeName) ?? Sign.Unsigned,
            Endianess = ParseEndianess(r.Text("Endianess"), r.NodeName) ?? Endianess.LittleEndian,
            Unit = r.Text("Unit"),
            Representation = ParseRepresentation(r.Text("Representation"), r.NodeName),
            ValidValueSet = ReadValidValueSet(r),
        };
    }

    private static IntSwissKnifeDef ReadIntSwissKnife(GenApiElementReader r)
    {
        var parts = ReadFormulaParts(r);
        return new IntSwissKnifeDef
        {
            Name = r.NodeName,
            Variables = parts.Variables,
            Constants = parts.Constants,
            Expressions = parts.Expressions,
            Formula = RequiredFormula(r, "Formula"),
            Unit = r.Text("Unit"),
            Representation = ParseRepresentation(r.Text("Representation"), r.NodeName),
            ValidValueSet = ReadValidValueSet(r),
        };
    }

    private static IntConverterDef ReadIntConverter(GenApiElementReader r)
    {
        var parts = ReadFormulaParts(r);
        return new IntConverterDef
        {
            Name = r.NodeName,
            Variables = parts.Variables,
            Constants = parts.Constants,
            Expressions = parts.Expressions,
            FormulaTo = RequiredFormula(r, "FormulaTo"),
            FormulaFrom = RequiredFormula(r, "FormulaFrom"),
            PValue = r.Ref("pValue"),
            Slope = ParseSlope(r.Text("Slope"), r.NodeName) ?? Slope.Automatic,
            IsLinear = r.YesNo("IsLinear") ?? false,
            Unit = r.Text("Unit"),
            Representation = ParseRepresentation(r.Text("Representation"), r.NodeName),
            ValidValueSet = ReadValidValueSet(r),
        };
    }

    private static FloatDef ReadFloat(GenApiElementReader r)
    {
        var def = new FloatDef
        {
            Name = r.NodeName,
            Value = r.Double("Value"),
            PValue = r.Ref("pValue"),
            PValueCopies = r.TextList("pValueCopy"),
            PIndex = r.Ref("pIndex"),
            ValueIndexed = ReadValueIndexedDouble(r),
            PValueIndexed = ReadPValueIndexed(r),
            ValueDefault = r.Double("ValueDefault"),
            PValueDefault = r.Ref("pValueDefault"),
            Min = r.Double("Min"),
            PMin = r.Ref("pMin"),
            Max = r.Double("Max"),
            PMax = r.Ref("pMax"),
            Inc = r.Double("Inc"),
            PInc = r.Ref("pInc"),
            Unit = r.Text("Unit"),
            Representation = ParseRepresentation(r.Text("Representation"), r.NodeName),
            DisplayNotation = ParseDisplayNotation(r.Text("DisplayNotation"), r.NodeName),
            DisplayPrecision = r.Int32("DisplayPrecision"),
        };
        RequireValueSource(r, def.Value is not null || def.PValue is not null
            || def.ValueIndexed.Count > 0 || def.PValueIndexed.Count > 0 || def.ValueDefault is not null || def.PValueDefault is not null);
        RequireIndexForIndexedValues(r,
            (def.ValueIndexed.Count > 0 || def.PValueIndexed.Count > 0)
            && def.Value is null && def.PValue is null && def.ValueDefault is null && def.PValueDefault is null,
            def.PIndex);
        return def;
    }

    private static FloatRegDef ReadFloatReg(GenApiElementReader r, ParseCtx ctx, List<NodeDef> extra) => new()
    {
        Name = r.NodeName,
        RegisterSet = ReadRegisterSet(r, ctx, extra),
        Endianess = ParseEndianess(r.Text("Endianess"), r.NodeName) ?? Endianess.LittleEndian,
        Unit = r.Text("Unit"),
        Representation = ParseRepresentation(r.Text("Representation"), r.NodeName),
        DisplayNotation = ParseDisplayNotation(r.Text("DisplayNotation"), r.NodeName),
        DisplayPrecision = r.Int32("DisplayPrecision"),
    };

    private static SwissKnifeDef ReadSwissKnife(GenApiElementReader r)
    {
        var parts = ReadFormulaParts(r);
        return new SwissKnifeDef
        {
            Name = r.NodeName,
            Variables = parts.Variables,
            Constants = parts.Constants,
            Expressions = parts.Expressions,
            Formula = RequiredFormula(r, "Formula"),
            Unit = r.Text("Unit"),
            Representation = ParseRepresentation(r.Text("Representation"), r.NodeName),
            DisplayNotation = ParseDisplayNotation(r.Text("DisplayNotation"), r.NodeName),
            DisplayPrecision = r.Int32("DisplayPrecision"),
        };
    }

    private static ConverterDef ReadConverter(GenApiElementReader r)
    {
        var parts = ReadFormulaParts(r);
        return new ConverterDef
        {
            Name = r.NodeName,
            Variables = parts.Variables,
            Constants = parts.Constants,
            Expressions = parts.Expressions,
            FormulaTo = RequiredFormula(r, "FormulaTo"),
            FormulaFrom = RequiredFormula(r, "FormulaFrom"),
            PValue = r.Ref("pValue"),
            Slope = ParseSlope(r.Text("Slope"), r.NodeName) ?? Slope.Automatic,
            IsLinear = r.YesNo("IsLinear") ?? false,
            Unit = r.Text("Unit"),
            Representation = ParseRepresentation(r.Text("Representation"), r.NodeName),
            DisplayNotation = ParseDisplayNotation(r.Text("DisplayNotation"), r.NodeName),
            DisplayPrecision = r.Int32("DisplayPrecision"),
        };
    }

    private static StringDef ReadString(GenApiElementReader r)
    {
        var def = new StringDef
        {
            Name = r.NodeName,
            // 문자열은 빈 값도 값이다(<Value></Value> = "") — 다른 곳과 달리 빈 요소를 없는 것으로 보지 않는다.
            Value = r.TextAllowEmpty("Value"),
            PValue = r.Ref("pValue"),
        };
        RequireValueSource(r, def.Value is not null || def.PValue is not null);
        return def;
    }

    private static BooleanDef ReadBoolean(GenApiElementReader r)
    {
        var valueText = r.Text("Value");
        var def = new BooleanDef
        {
            Name = r.NodeName,
            Value = valueText is null ? null : GenApiLiteral.ParseYesNo(valueText, "Value", r.NodeName),
            PValue = r.Ref("pValue"),
            OnValue = r.Int64("OnValue") ?? 1,
            OffValue = r.Int64("OffValue") ?? 0,
        };
        RequireValueSource(r, def.Value is not null || def.PValue is not null);
        return def;
    }

    private static EnumerationDef ReadEnumeration(GenApiElementReader r, ParseCtx ctx, List<NodeDef> extra)
    {
        var entryElems = r.Elements("EnumEntry");
        var entries = new EnumEntryDef[entryElems.Count];
        // 항목 이름은 열거 안에서만 유일하다 — 실제 장치 XML 은 여러 열거에 "Off" 를 두고 "Width" 같은 피처 이름과도 겹친다.
        // 전역 등록 이름은 "EnumEntry_{열거}_{항목}" 으로 한정한다. XML 이 이미 그 접두를 붙여 두었으면 접두를 뗀 나머지가 항목 이름이다
        // (그래야 Symbolic 기본값이 "Off" 이지 "EnumEntry_TriggerMode_Off" 가 아니다).
        // 같은 열거 안의 항목 이름 중복은 XML 오류다. 열거 이름에 '_' 가 들어가 다른 열거의 항목과 한정 이름이 겹치는 것
        // ("Gain"+"Auto_Off" 와 "Gain_Auto"+"Off")은 정당한 XML 이므로 "#n" 을 붙여 피하고 경고만 남긴다 — 런타임은 항목을 Entries 로 찾는다.
        var prefix = "EnumEntry_" + r.NodeName + "_";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entryElems.Count; i++)
        {
            var e = entryElems[i];
            var raw = e.Attribute("Name")?.Value.Trim();
            if (string.IsNullOrEmpty(raw))
                throw new GenApiException($"<EnumEntry> in Enumeration '{r.NodeName}' has no Name attribute.", r.NodeName);
            var entryName = raw!.Length > prefix.Length && raw.StartsWith(prefix, StringComparison.Ordinal) ? raw.Substring(prefix.Length) : raw;
            var en = prefix + entryName;
            if (!seen.Add(entryName))
                throw new GenApiException($"Duplicate EnumEntry name '{entryName}' in Enumeration '{r.NodeName}'.", en);
            if (ctx.IsNameTaken(en))
            {
                var n = 2;
                while (ctx.IsNameTaken(en + "#" + n)) n++;
                var previous = ctx.Nodes.Find(d => d.Name == en);
                var previousOwner = previous is EnumEntryDef pe ? $"entry '{pe.EntryName}' of another Enumeration" : $"<{previous?.Kind}>";
                en = en + "#" + n;
                ctx.Warn($"EnumEntry '{entryName}' of Enumeration '{r.NodeName}' collides with {previousOwner} on the qualified name '{prefix + entryName}'; registered as '{en}'.");
            }
            var er = new GenApiElementReader(e, en, ctx.Warn);
            var valueText = er.Text("Value");
            if (valueText is null)
                throw new GenApiException($"<EnumEntry> '{en}' has no Value.", en);
            var symbolic = er.Text("Symbolic");
            NodeDef entry = new EnumEntryDef
            {
                Name = en,
                EntryName = entryName,
                Value = GenApiLiteral.ParseInt64(valueText, "Value", en),
                NumericValue = er.Double("NumericValue"),
                Symbolic = symbolic ?? entryName,
                IsSelfClearing = er.YesNo("IsSelfClearing") ?? false,
            };
            entry = ApplyCommon(entry, er);
            WarnUnconsumed(er, ctx);
            entries[i] = (EnumEntryDef)entry;
            extra.Add(entry);
        }
        if (entries.Length == 0)
            ctx.Warn($"<Enumeration> '{r.NodeName}' has no EnumEntry.");

        var def = new EnumerationDef
        {
            Name = r.NodeName,
            Value = r.Int64("Value"),
            PValue = r.Ref("pValue"),
            Entries = entries,
            Representation = ParseRepresentation(r.Text("Representation"), r.NodeName),
        };
        RequireValueSource(r, def.Value is not null || def.PValue is not null);
        return def;
    }

    private static CommandDef ReadCommand(GenApiElementReader r)
    {
        var def = new CommandDef
        {
            Name = r.NodeName,
            Value = r.Int64("Value"),
            PValue = r.Ref("pValue"),
            CommandValue = r.Int64("CommandValue"),
            PCommandValue = r.Ref("pCommandValue"),
        };
        RequireValueSource(r, def.Value is not null || def.PValue is not null);
        return def;
    }

    private static PortDef ReadPort(GenApiElementReader r)
    {
        var chunkText = r.Ref("ChunkID");
        return new PortDef
        {
            Name = r.NodeName,
            ChunkId = chunkText is null ? null : GenApiLiteral.ParseHex(chunkText, "ChunkID", r.NodeName),
            PChunkId = r.Ref("pChunkID"),
            IsEndianessSwapped = r.YesNo("SwapEndianess") ?? false,
            IsChunkDataCached = r.YesNo("CacheChunkData") ?? false,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // StructReg → 항목별 MaskedIntReg
    // ---------------------------------------------------------------------------------------------

    private static void ExpandStructReg(XElement el, ParseCtx ctx)
    {
        var idx = ctx.StructRegCount++;
        var label = $"StructReg#{idx}";
        var r = new GenApiElementReader(el, label, ctx.Warn);
        var structComment = r.Attr("Comment");

        // 이름 있는 인라인 IntSwissKnife 는 항목들 뒤에 등록한다(StructReg 자체는 노드가 아니다).
        var nested = new List<NodeDef>();
        var rs = ReadRegisterSet(r, ctx, nested);
        var endianess = ParseEndianess(r.Text("Endianess"), label) ?? Endianess.LittleEndian;
        var structSign = ParseSign(r.Text("Sign"), label);
        var structUnit = r.Text("Unit");
        var structRepresentation = ParseRepresentation(r.Text("Representation"), label);
        var structValidValueSet = ReadValidValueSet(r);
        // StructReg 자체에 적힌 공통 자식(ToolTip·Visibility·술어·pInvalidator…)은 모든 항목에 복사된다 — 항목이 같은 자식을 가지면 항목 쪽이 이긴다.
        // 실제 장치 XML 은 Visibility·ToolTip·pIsImplemented·pIsLocked 를 StructReg 에 두는 일이 흔하다.
        var structCommon = ApplyCommon(new GenericNodeDef { Name = label }, r);

        var entryElems = r.Elements("StructEntry");
        if (entryElems.Count == 0)
            ctx.Warn($"<StructReg> #{idx} (Comment='{structComment}') has no StructEntry.");

        foreach (var e in entryElems)
        {
            var name = e.Attribute("Name")?.Value.Trim();
            if (string.IsNullOrEmpty(name))
                throw new GenApiException($"<StructEntry> in <StructReg> #{idx} (Comment='{structComment}') has no Name attribute.");

            var er = new GenApiElementReader(e, name!, ctx.Warn);
            var (bit, lsb, msb) = ReadBits(er);
            var entryAccess = ParseAccessMode(er.Text("AccessMode"), name);
            var entryCachable = ParseCachable(er.Text("Cachable"), name);
            var entryRs = entryAccess is null && entryCachable is null
                ? rs
                : rs with { AccessMode = entryAccess ?? rs.AccessMode, Cachable = entryCachable ?? rs.Cachable };

            NodeDef def = new MaskedIntRegDef
            {
                Name = name!,
                RegisterSet = entryRs,
                Bit = bit,
                Lsb = lsb,
                Msb = msb,
                Sign = ParseSign(er.Text("Sign"), name) ?? structSign ?? Sign.Unsigned,
                Endianess = endianess,
                IsStructEntry = true,
                StructRegIndex = idx,
                Unit = er.Text("Unit") ?? structUnit,
                Representation = ParseRepresentation(er.Text("Representation"), name) ?? structRepresentation,
                ValidValueSet = ReadValidValueSet(er) ?? structValidValueSet,
            };
            def = ApplyCommon(def, er, structCommon);

            WarnUnconsumed(er, ctx);
            ctx.Add(def);
        }

        foreach (var n in nested) ctx.Add(n);
        WarnUnconsumed(r, ctx);
    }

    // ---------------------------------------------------------------------------------------------
    // 열거 리터럴 — 스키마 값 그대로(대소문자 구분). 모르는 값은 오류.
    // ---------------------------------------------------------------------------------------------

    private static GenApiException BadEnum(string what, string value, string? node)
        => new($"Invalid {what} value '{value}' in node '{node}'.", node);

    private static NodeNameSpace ParseNameSpace(string? t, string? node) => t switch
    {
        null => NodeNameSpace.Custom,
        "Custom" => NodeNameSpace.Custom,
        "Standard" => NodeNameSpace.Standard,
        _ => throw BadEnum("NameSpace", t, node),
    };

    private static AccessMode? ParseAccessMode(string? t, string? node) => t switch
    {
        null => null,
        "RO" => AccessMode.ReadOnly,
        "WO" => AccessMode.WriteOnly,
        "RW" => AccessMode.ReadWrite,
        _ => throw BadEnum("AccessMode", t, node),
    };

    private static Visibility? ParseVisibility(string? t, string? node) => t switch
    {
        null => null,
        "Beginner" => Visibility.Beginner,
        "Expert" => Visibility.Expert,
        "Guru" => Visibility.Guru,
        "Invisible" => Visibility.Invisible,
        _ => throw BadEnum("Visibility", t, node),
    };

    private static Representation? ParseRepresentation(string? t, string? node) => t switch
    {
        null => null,
        "Linear" => Representation.Linear,
        "Logarithmic" => Representation.Logarithmic,
        "Boolean" => Representation.Boolean,
        "PureNumber" => Representation.PureNumber,
        "HexNumber" => Representation.HexNumber,
        "IPV4Address" => Representation.IPV4Address,
        "MACAddress" => Representation.MACAddress,
        _ => throw BadEnum("Representation", t, node),
    };

    private static Sign? ParseSign(string? t, string? node) => t switch
    {
        null => null,
        "Signed" => Sign.Signed,
        "Unsigned" => Sign.Unsigned,
        _ => throw BadEnum("Sign", t, node),
    };

    private static Endianess? ParseEndianess(string? t, string? node) => t switch
    {
        null => null,
        "LittleEndian" => Endianess.LittleEndian,
        "BigEndian" => Endianess.BigEndian,
        _ => throw BadEnum("Endianess", t, node),
    };

    private static Cachable? ParseCachable(string? t, string? node) => t switch
    {
        null => null,
        "NoCache" => Cachable.NoCache,
        "WriteThrough" => Cachable.WriteThrough,
        "WriteAround" => Cachable.WriteAround,
        _ => throw BadEnum("Cachable", t, node),
    };

    private static Slope? ParseSlope(string? t, string? node) => t switch
    {
        null => null,
        "Increasing" => Slope.Increasing,
        "Decreasing" => Slope.Decreasing,
        "Varying" => Slope.Varying,
        "Automatic" => Slope.Automatic,
        _ => throw BadEnum("Slope", t, node),
    };

    private static DisplayNotation? ParseDisplayNotation(string? t, string? node) => t switch
    {
        null => null,
        "Automatic" => DisplayNotation.Automatic,
        "Fixed" => DisplayNotation.Fixed,
        "Scientific" => DisplayNotation.Scientific,
        _ => throw BadEnum("DisplayNotation", t, node),
    };
}
