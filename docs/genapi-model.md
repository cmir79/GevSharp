# GenApi XML model (`GevSharp.GenApi.Model`)

The model layer turns a GenApi XML document into an immutable, flat set of node definitions. It does no
name resolution, no register access and no formula evaluation — every `p*` element is stored as the
target node's name and resolved by the runtime layer at bind time (a missing target is a
`GenApiException` there, carrying the referring node's name). The runtime module is written against this
document plus the source in `src/GevSharp/GenApi/Model/`.

## Entry points

```csharp
GenApiXmlModel GenApiXmlParser.Parse(string xml);      // GenApiException on malformed XML / structural errors
GenApiXmlModel GenApiXmlParser.Parse(XDocument doc);

sealed class GenApiXmlModel
{
    RegisterDescriptionInfo Info;                       // root attributes (shared record in GenApiNodeMap.Contract.cs)
    IReadOnlyDictionary<string, NodeDef> Nodes;         // ordinal, XML Name → def
    IReadOnlyList<NodeDef> NodeList;                    // document order (EnumEntry defs directly after their Enumeration)
    IReadOnlyList<string> Warnings;                     // also emitted through GevLog.Warn("GenApi.Model", …)
    NodeDef? Find(string name);
    NodeDef Get(string name);                           // GenApiException if missing
    T Get<T>(string name) where T : NodeDef;            // GenApiException if missing or of another type
}
```

Parsing rules:

| Rule | Behaviour |
|---|---|
| Namespaces | Elements are matched by local name only. `Version_1_0`, `Version_1_1` and no namespace are silent; any other namespace adds a warning. A leading BOM character is tolerated. |
| `<Group>` | Transparent at any depth: children are registered as top-level nodes in document order. `Comment` is ignored. |
| Element depth | Nesting deeper than `GenApiXmlParser.MaxElementDepth` (128, root = 1) → `GenApiException` before any node is read. The document is checked iteratively; without the cap a device-supplied XML of a few hundred KB with thousands of nested `<Group>` (or deep content inside any child element) overflows the call stack, which cannot be caught and kills the process. Real XML nests about 10 levels. |
| DOCTYPE / DTD | Rejected as `Malformed GenApi XML` (DTD processing is prohibited — no entity expansion from an untrusted device). Camera XMLs do not carry a DTD. |
| `<StructReg>` | Expanded into one `MaskedIntRegDef` per `<StructEntry>` (see below). No node is created for the StructReg itself. |
| Unknown element kind | `UnknownDef` (Name from the attribute or `Unknown{n}_{element}` when absent) plus a warning. Never an exception. |
| Unknown child element | Ignored with a warning naming the child and the node. `<Extension>` is ignored silently. |
| Duplicate `Name` | `GenApiException` (`NodeName` = the duplicated name). EnumEntry defs take part under their qualified name (next row). |
| `<EnumEntry>` naming | Entry names are unique only inside their Enumeration — real device XML reuses `Off` across dozens of enumerations and even reuses feature names such as `Width`. `EntryName` is the XML Name with an `EnumEntry_{Enumeration}_` prefix stripped when present (so `Name="EnumEntry_TriggerMode_Off"` gives `EntryName = "Off"`); the registered `Name` is always `EnumEntry_{Enumeration}_{EntryName}`; `Symbolic` defaults to `EntryName`. Two entries with the same `EntryName` inside one Enumeration → `GenApiException`. Because `_` is legal in names, two different (Enumeration, entry) pairs can produce the same qualified name (`Gain`+`Auto_Off` vs `Gain_Auto`+`Off`); that is valid XML, so the later one is registered as `{qualified}#2` (`#3`, …) with a warning. The runtime must find entries through `EnumerationDef.Entries`/`Symbolic`, never by guessing the qualified name. |
| Missing `Name` on a known kind | `GenApiException`. |
| Missing root / wrong root / missing required root attributes | `GenApiException`. Required: `ModelName`, `VendorName`, `SchemaMajorVersion`, `SchemaMinorVersion`, `MajorVersion`, `MinorVersion`, `SubMinorVersion`. Optional: `SchemaSubMinorVersion` (0), `ToolTip`, `StandardNameSpace`, `ProductGuid`, `VersionGuid`. |
| Text values | Always trimmed. An element that is present but empty (`<Unit/>`, `<ValidValueSet></ValidValueSet>`, `<ToolTip>  </ToolTip>`) counts as absent — the field stays null / at its default and a StructEntry inherits the StructReg's value. The one exception is `<String><Value></Value>` where `""` is a value. An empty item in a reference list (`<pInvalidator/>`, `<pFeature/>`, …) is skipped with a warning; an empty literal (`<Address/>`, `<Length/>`) is a `GenApiException`. |
| Integer literals | Decimal (optional sign) or `0x`/`0X` hexadecimal, parsed to `long` (hex may use the full 64-bit width; `0xFFFFFFFFFFFFFFFF` → -1). Anything else → `GenApiException` with the node name and element. |
| Float literals | Invariant-culture `double`; integer literals (including hex) are accepted too. |
| Yes/No literals | `Yes`/`No` (also `true`/`false`/`1`/`0`, case-insensitive). |
| Enumerated literals (`AccessMode`, `Visibility`, …) | Exact schema spelling; anything else → `GenApiException`. |
| Value source | `Integer`/`Float`/`String`/`Boolean`/`Enumeration`/`Command` without any of `Value`/`pValue` (or `ValueIndexed`/`pValueIndexed`/`ValueDefault`/`pValueDefault` for Integer/Float) → `GenApiException`. |
| Register length | A register node without `Length` and without `pLength` (or with `Length` ≤ 0) → `GenApiException`. |
| Formula presence | `IntSwissKnife`/`SwissKnife` without `Formula`, `IntConverter`/`Converter` without `FormulaTo` **and** `FormulaFrom` → `GenApiException`. |

## Enums

| Enum | Values | XML element / default |
|---|---|---|
| `NodeDefKind` | Category, Integer, IntReg, MaskedIntReg, IntSwissKnife, IntConverter, Float, FloatReg, SwissKnife, Converter, String, StringReg, Boolean, Enumeration, EnumEntry, Command, Register, Port, Node, Unknown | element name |
| `NodeNameSpace` | Custom, Standard | `NameSpace` attribute, default Custom |
| `Sign` | Unsigned, Signed | `<Sign>`, default Unsigned |
| `Endianess` | LittleEndian, BigEndian | `<Endianess>` (schema spelling), default LittleEndian |
| `Cachable` | NoCache, WriteThrough, WriteAround | `<Cachable>`, default WriteThrough |
| `Slope` | Automatic, Increasing, Decreasing, Varying | `<Slope>`, default Automatic |
| `DisplayNotation` | Automatic, Fixed, Scientific | `<DisplayNotation>`, stored nullable (runtime default Automatic) |
| `GevSharp.GenApi.AccessMode` (shared) | ReadOnly, WriteOnly, ReadWrite (NotImplemented/NotAvailable never come from XML) | `<AccessMode>`/`<ImposedAccessMode>` as `RO`/`WO`/`RW` |
| `GevSharp.GenApi.Visibility` (shared) | Beginner, Expert, Guru, Invisible | `<Visibility>`, default Beginner |
| `GevSharp.GenApi.Representation` (shared) | Linear, Logarithmic, Boolean, PureNumber, HexNumber, IPV4Address, MACAddress | `<Representation>`, stored nullable (runtime default PureNumber) |

## `NodeDef` — fields shared by every kind

`NodeDef` is an abstract record; `Kind` says which XML element it came from and `InterfaceKind`
(`NodeKind`) says which public interface the runtime should build (all five integer kinds → `Integer`,
all four float kinds → `Float`, `Node`/`Unknown` → `Unknown`).

| Field | Type | XML | Notes |
|---|---|---|---|
| `Name` | string | `Name` attr | unique, case-sensitive |
| `NameSpace` | NodeNameSpace | `NameSpace` attr | default Custom |
| `Comment` | string? | `Comment` attr | StructEntry defs inherit the StructReg's Comment when they have none |
| `ToolTip`, `Description`, `DisplayName`, `DocuUrl` | string? | `ToolTip`, `Description`, `DisplayName`, `DocuURL` | |
| `Visibility` | Visibility | `Visibility` | default Beginner |
| `EventId` | string? | `EventID` | hex text exactly as written (validated as hexadecimal; a `0x` prefix is accepted and kept) |
| `EventIdValue` | ulong? | `EventID` | the same value parsed as hexadecimal (`9002` → 0x9002 = 36866). Use this to match device events — re-parsing `EventId` as decimal silently selects a different event |
| `PIsImplemented`, `PIsAvailable`, `PIsLocked` | string? | `pIsImplemented`, `pIsAvailable`, `pIsLocked` | guard nodes (Integer/Boolean/SwissKnife; non-zero = true) |
| `PBlockPolling` | string? | `pBlockPolling` | |
| `PInvalidators` | IReadOnlyList\<string\> | `pInvalidator`* | writing any of them invalidates this node |
| `ImposedAccessMode` | AccessMode? | `ImposedAccessMode` | |
| `PAlias`, `PCastAlias` | string? | `pAlias`, `pCastAlias` | |
| `IsStreamable` | bool | `Streamable` | default false |
| `PErrors` | IReadOnlyList\<string\> | `pError`* | |
| `IsDeprecated` | bool | `IsDeprecated` | default false |
| `PollingTimeMs` | long? | `PollingTime` | register nodes: treat reads as NoCache; Command: completion polling |
| `PSelected` | IReadOnlyList\<string\> | `pSelected`* | accepted on any kind; meaningful on Integer kinds, Enumeration, Boolean. `pSelecting` is derived by the runtime |

`MergePriority`/`ExposeStatic` attributes and `<Extension>` are ignored.

## Shared building blocks

### `RegisterSet` (on `IRegisterNodeDef`: IntReg, MaskedIntReg, FloatReg, StringReg, Register, StructEntry)

Address = Σ `Addresses` + Σ value(`PAddresses`) + Σ value(`PIndexes[i].PNode`) × offset + Σ eval(`AddressSwissKnives`).

| Field | Type | XML | Notes |
|---|---|---|---|
| `Addresses` | IReadOnlyList\<long\> | `Address`* | literal terms, summed |
| `PAddresses` | IReadOnlyList\<string\> | `pAddress`* | Integer nodes added to the address |
| `PIndexes` | IReadOnlyList\<PIndexDef\> | `pIndex`* | `PIndexDef { PNode, Offset (long?), POffset (string?) }`; both offsets null → offset = register Length |
| `AddressSwissKnives` | IReadOnlyList\<IntSwissKnifeDef\> | inline `IntSwissKnife`* | nested defs; address evaluation always uses this list. A knife **with** a `Name` attribute is a real node name: the same instance is also registered in `Nodes`/`NodeList` (directly after its owner; after the expanded entries for a StructReg) so other nodes can reference it, and it takes part in the duplicate-name check. Without a `Name` it gets `{owner}_AddrSwissKnife{n}` and is **not** in `Nodes` |
| `Length` / `PLength` | long? / string? | `Length` / `pLength` | exactly one is expected (parser requires at least one) |
| `AccessMode` | AccessMode | `AccessMode` | default ReadWrite when absent |
| `PPort` | string? | `pPort` | required by the schema; the runtime throws at bind if null |
| `Cachable` | Cachable | `Cachable` | default WriteThrough |
| `HasStaticAddress`, `StaticAddress` | computed | | true when only literal `Address` terms exist |

### Formula parts (on `IFormulaNodeDef`: IntSwissKnife, SwissKnife, IntConverter, Converter)

| Field | Type | XML |
|---|---|---|
| `Variables` | IReadOnlyList\<FormulaVariableDef(Name, PNode)\> | `<pVariable Name="X">Node</pVariable>` |
| `Constants` | IReadOnlyList\<FormulaConstantDef { Name, Text, IntValue (long?), DoubleValue }\> | `<Constant Name="X">v</Constant>` |
| `Expressions` | IReadOnlyList\<FormulaExpressionDef(Name, Expression)\> | `<Expression Name="X">…</Expression>` |

`ISwissKnifeNodeDef` adds `Formula`; `IConverterNodeDef` adds `FormulaTo` (host → device, variable
`FROM`), `FormulaFrom` (device → host, variable `TO`), `PValue`, `Slope`, `IsLinear`.

### Indexed value selection — `PValueIndexedDef(long Index, string PNode)` / `ValueIndexedDef<T>(long Index, T Value)`

`<pIndex>Sel</pIndex>` on Integer/Float selects one of `<pValueIndexed Index="n">Node</pValueIndexed>` (node) or
`<ValueIndexed Index="n">literal</ValueIndexed>` (literal, `T` = long for Integer, double for Float); the two forms may be
mixed across indexes. When no index matches, `PValueDefault` (node) or `ValueDefault` (literal) applies. The runtime
reads `PIndex`, matches the value against both lists, then falls back to the default.

## Node kinds

| Kind | Record | Extra fields (beyond `NodeDef`) |
|---|---|---|
| Category | `CategoryDef` | `PFeatures` (pFeature*, document order) |
| Node | `GenericNodeDef` | — |
| Unknown | `UnknownDef` | `ElementName` |
| Integer | `IntegerDef : IntegerBaseDef` | `Value` (long?), `PValue`, `PValueCopies` (pValueCopy*), `PIndex`, `ValueIndexed` (ValueIndexedDef\<long\>*), `PValueIndexed`, `ValueDefault` (long?), `PValueDefault`, `Min`/`PMin`, `Max`/`PMax`, `Inc`/`PInc` (long? / string?) |
| IntReg | `IntRegDef : IntegerBaseDef, IRegisterNodeDef` | `RegisterSet`, `Sign`, `Endianess` |
| MaskedIntReg | `MaskedIntRegDef : IntegerBaseDef, IRegisterNodeDef` | `RegisterSet`, `Bit` (int?, raw), `Lsb`, `Msb` (normalized: `Bit` → both = Bit; 0..63 checked), `Sign`, `Endianess`, `IsStructEntry`, `StructRegIndex` (int?) |
| IntSwissKnife | `IntSwissKnifeDef : IntegerBaseDef, ISwissKnifeNodeDef` | formula parts, `Formula` |
| IntConverter | `IntConverterDef : IntegerBaseDef, IConverterNodeDef` | formula parts, `FormulaTo`, `FormulaFrom`, `PValue`, `Slope`, `IsLinear` |
| Float | `FloatDef : FloatBaseDef` | `Value` (double?), `PValue`, `PValueCopies`, `PIndex`, `ValueIndexed` (ValueIndexedDef\<double\>*), `PValueIndexed`, `ValueDefault` (double?), `PValueDefault`, `Min`/`PMin`, `Max`/`PMax`, `Inc`/`PInc` (double? / string?) |
| FloatReg | `FloatRegDef : FloatBaseDef, IRegisterNodeDef` | `RegisterSet`, `Endianess` |
| SwissKnife | `SwissKnifeDef : FloatBaseDef, ISwissKnifeNodeDef` | formula parts, `Formula` |
| Converter | `ConverterDef : FloatBaseDef, IConverterNodeDef` | formula parts, `FormulaTo`, `FormulaFrom`, `PValue`, `Slope`, `IsLinear` |
| String | `StringDef` | `Value` (string?, `""` is a value; null = absent), `PValue` |
| StringReg | `StringRegDef : IRegisterNodeDef` | `RegisterSet` |
| Boolean | `BooleanDef` | `Value` (bool?), `PValue`, `OnValue` (default 1), `OffValue` (default 0) |
| Enumeration | `EnumerationDef` | `Value` (long?), `PValue`, `Entries` (IReadOnlyList\<EnumEntryDef\>, same instances as in `Nodes`), `Representation?` |
| EnumEntry | `EnumEntryDef` | `EntryName` (XML Name with any `EnumEntry_{Enumeration}_` prefix stripped; `Name` is the qualified `EnumEntry_{Enumeration}_{EntryName}`, plus `#n` on a cross-enumeration collision), `Value` (long, required), `NumericValue` (double?), `Symbolic` (defaults to `EntryName`), `IsSelfClearing` |
| Command | `CommandDef` | `Value` (long?), `PValue`, `CommandValue` (long?), `PCommandValue` (runtime default when both absent: 1) |
| Register | `RegisterDef : IRegisterNodeDef` | `RegisterSet` |
| Port | `PortDef` | `ChunkId` (ulong?, hex text), `PChunkId`, `IsEndianessSwapped` (SwapEndianess), `IsChunkDataCached` (CacheChunkData); `EventId` in base |

`IntegerBaseDef` adds `Unit`, `Representation?`, `ValidValueSet` (IReadOnlyList\<long\>?, `;`/whitespace
separated). `FloatBaseDef` adds `Unit`, `Representation?`, `DisplayNotation?`, `DisplayPrecision` (int?).

## StructReg expansion

```xml
<StructReg Comment="…">                       <!-- register set + Endianess (+ Sign, Cachable, PollingTime, pInvalidator*) -->
  <StructEntry Name="A"><Bit>31</Bit></StructEntry>
  <StructEntry Name="B"><LSB>0</LSB><MSB>15</MSB><AccessMode>RO</AccessMode><Cachable>NoCache</Cachable>…</StructEntry>
</StructReg>
```

Each entry becomes a `MaskedIntRegDef` with `IsStructEntry = true` and `StructRegIndex` = ordinal of the
StructReg in the document (entries of one StructReg share the same `RegisterSet` instance unless they
override `AccessMode`/`Cachable`, in which case they get a copy with those two fields changed).
The StructReg's children are copied into every entry and the entry's own children win:

- register set: `Addresses`, `PAddresses`, `PIndexes`, `AddressSwissKnives`, `Length`/`PLength`, `PPort`,
  `AccessMode`, `Cachable` (an entry may override the last two); `Endianess` comes from the StructReg only;
- `Sign`, `Unit`, `Representation`, `ValidValueSet` — entry's own wins;
- every common `NodeDef` field — `Comment`/`NameSpace` attributes, `ToolTip`, `Description`, `DisplayName`,
  `DocuUrl`, `Visibility`, `EventId`, `PIsImplemented`/`PIsAvailable`/`PIsLocked`, `PBlockPolling`,
  `ImposedAccessMode`, `PAlias`/`PCastAlias`, `IsStreamable`, `IsDeprecated`, `PollingTimeMs` — entry's own
  wins (real device XML routinely puts `Visibility`, `ToolTip` and guards on the StructReg itself);
- list fields `PInvalidators`, `PErrors`, `PSelected` = StructReg's list followed by the entry's own.

`Bit`/`LSB`/`MSB` always come from the entry.

## Bit numbering reminder for the runtime

`Lsb`/`Msb` are stored exactly as written in the XML. Per the architecture document, for a `BigEndian`
register bit 0 is the most significant bit of the register and for `LittleEndian` bit 0 is the least
significant; the runtime normalizes to a shift/mask over the little-endian integer after byte-order
decoding. The model does not reinterpret the numbers.

## Fixtures (hand-written, `tests/GevSharp.Tests/Fixtures/GenApi/`)

- `minimal.xml` — `Root` category, `Width` Integer over `WidthReg` IntReg, `Device` port (4 nodes).
- `groups.xml` — three-level nested Groups, a StructReg with 3 entries, every node kind at least once with
  SFNC-style names (98 nodes: 8 Category, 13 Integer, 20 IntReg, 8 MaskedIntReg, 6 IntSwissKnife — 5 top-level
  plus the named inline `LUTValueAddr` —, 1 IntConverter, 3 Float, 2 FloatReg, 1 SwissKnife, 2 Converter,
  1 String, 2 StringReg, 4 Boolean, 6 Enumeration, 14 EnumEntry, 3 Command, 1 Register, 2 Port, 1 Node).
  Parses with zero warnings, and every `p*` reference in it resolves.
