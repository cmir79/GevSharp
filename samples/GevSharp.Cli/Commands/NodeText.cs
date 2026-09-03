using System.Globalization;
using System.Text;
using GevSharp.GenApi;

namespace GevSharp.Cli.Commands;

/// <summary>
/// GenApi 노드의 접근 모드·값을 사람이 읽는 문자열로 바꾸고, set 명령이 받은 문자열을 노드 종류에 맞게 쓴다.
/// 값 접근은 전부 <see cref="INode"/> 계열 인터페이스로만 한다 — 런타임 구현이 바뀌어도 여기는 그대로다.
/// </summary>
public static class NodeText
{
    /// <summary>레지스터 노드 값을 보여 줄 때 읽는 최대 길이. 그보다 길면 주소·길이만 보여 준다.</summary>
    private const int MaxRegisterDumpBytes = 4096;
    private const int RegisterPreviewBytes = 16;

    public static string AccessTag(AccessMode mode) => mode switch
    {
        AccessMode.ReadWrite => "RW",
        AccessMode.ReadOnly => "RO",
        AccessMode.WriteOnly => "WO",
        AccessMode.NotAvailable => "NA",
        AccessMode.NotImplemented => "NI",
        _ => "??",
    };

    public static bool IsReadable(AccessMode mode) => mode is AccessMode.ReadWrite or AccessMode.ReadOnly;

    /// <summary>값이 없는 종류(카테고리·포트·열거 엔트리)는 접근 모드와 무관하게 설명 문자열을 낸다.</summary>
    public static bool HasValue(INode node) => node.Kind is not (NodeKind.Category or NodeKind.Port or NodeKind.EnumEntry);

    /// <summary>읽기 실패를 인라인 텍스트로 바꾼다 — 트리 순회가 노드 하나 때문에 멈추지 않게. 취소는 그대로 올린다.</summary>
    public static async Task<string> ReadValueOrErrorAsync(INode node, AccessMode access, CancellationToken ct)
    {
        if (HasValue(node) && !IsReadable(access)) return "-";
        try
        {
            return await ReadValueAsync(node, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
    }

    // 종류 분기는 구체적인 인터페이스부터 본다 — 열거 노드가 정수 값을, 레지스터 기반 정수가 IRegister 를 함께 구현할 수 있으므로
    // IInteger/IRegister 를 먼저 잡으면 PixelFormat 이 숫자로 찍히고 심볼 쓰기가 막힌다. 순서: 열거·커맨드·불리언·문자열·엔트리·실수·정수·레지스터·카테고리·포트.

    /// <summary>노드 종류별 값 문자열. 실패는 예외로 올린다.</summary>
    public static async Task<string> ReadValueAsync(INode node, CancellationToken ct)
    {
        switch (node)
        {
            case IEnumeration e:
                return await e.GetAsync(ct);
            case ICommand c:
                return await c.IsDoneAsync(ct) ? "(command; idle)" : "(command; executing)";
            case IBoolean b:
                return await b.GetAsync(ct) ? "true" : "false";
            case IString s:
                return Quote(await s.GetAsync(ct));
            case IEnumEntry ee:
                return ee.Value.ToString(CultureInfo.InvariantCulture);
            case IFloat f:
                return FormatFloat(await f.GetAsync(ct), f.Unit);
            case IInteger i:
                return FormatInteger(await i.GetAsync(ct), i.Representation, i.Unit);
            case IRegister r:
                return await FormatRegisterAsync(r, ct);
            case ICategory cat:
                return $"({cat.Features.Count} feature(s))";
            case IPortNode:
                return "(port)";
            default:
                return $"({node.Kind})";
        }
    }

    /// <summary>get --detail 이 보여 주는 부가 정보 줄들. 개별 항목의 읽기 실패는 인라인으로 남긴다.</summary>
    public static async Task<IReadOnlyList<string>> DescribeAsync(INode node, CancellationToken ct)
    {
        var lines = new List<string>
        {
            $"kind: {node.Kind}, visibility: {node.Visibility}, streamable: {(node.IsStreamable ? "yes" : "no")}",
        };
        if (!string.IsNullOrEmpty(node.DisplayName)) lines.Add($"display name: {node.DisplayName}");
        if (!string.IsNullOrEmpty(node.ToolTip)) lines.Add($"tooltip: {node.ToolTip}");
        if (!string.IsNullOrEmpty(node.Description)) lines.Add($"description: {node.Description}");

        switch (node)
        {
            case IEnumeration e:
                foreach (var entry in e.Entries)
                {
                    var access = await TryAsync(() => entry.GetAccessModeAsync(ct), AccessTag, ct);
                    var numeric = entry.NumericValue is null ? string.Empty : $", numeric {FormatFloat(entry.NumericValue.Value, null)}";
                    lines.Add($"entry: {entry.Symbolic} = {entry.Value}{numeric} [{access}]");
                }
                break;
            case IString s:
                lines.Add($"max length: {await TryAsync(() => s.GetMaxLengthAsync(ct), v => v.ToString(CultureInfo.InvariantCulture), ct)}");
                break;
            case IFloat f:
                lines.Add($"min: {await TryAsync(() => f.GetMinAsync(ct), v => FormatFloat(v, null), ct)}, " +
                          $"max: {await TryAsync(() => f.GetMaxAsync(ct), v => FormatFloat(v, null), ct)}, " +
                          $"inc: {await TryAsync(() => f.GetIncAsync(ct), v => v is null ? "(none)" : FormatFloat(v.Value, null), ct)}, " +
                          $"unit: {NetText.Text(f.Unit)}, representation: {f.Representation}");
                break;
            case IInteger i:
                lines.Add($"min: {await TryAsync(() => i.GetMinAsync(ct), v => FormatInteger(v, i.Representation, null), ct)}, " +
                          $"max: {await TryAsync(() => i.GetMaxAsync(ct), v => FormatInteger(v, i.Representation, null), ct)}, " +
                          $"inc: {await TryAsync(() => i.GetIncAsync(ct), v => v.ToString(CultureInfo.InvariantCulture), ct)}, " +
                          $"unit: {NetText.Text(i.Unit)}, representation: {i.Representation}");
                break;
            case IRegister r:
                lines.Add($"address: {await TryAsync(() => r.GetAddressAsync(ct), v => $"0x{v:X}", ct)}, " +
                          $"length: {await TryAsync(() => r.GetLengthAsync(ct), v => v.ToString(CultureInfo.InvariantCulture), ct)}");
                break;
            case ICategory cat:
                foreach (var feature in cat.Features) lines.Add($"feature: {feature.Name} [{feature.Kind}]");
                break;
        }
        return lines;
    }

    private static async Task<string> TryAsync<T>(Func<ValueTask<T>> read, Func<T, string> format, CancellationToken ct)
    {
        try
        {
            return format(await read());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
    }

    // ------------------------------------------------------------------ formatting

    public static string FormatInteger(long value, Representation representation, string? unit)
    {
        string text;
        switch (representation)
        {
            case Representation.HexNumber:
                text = "0x" + value.ToString("X", CultureInfo.InvariantCulture);
                break;
            case Representation.IPV4Address:
                text = NetText.Ipv4(unchecked((uint)value)).ToString();
                break;
            case Representation.MACAddress:
            {
                var sb = new StringBuilder(17);
                for (var shift = 40; shift >= 0; shift -= 8)
                {
                    if (sb.Length > 0) sb.Append(':');
                    sb.Append(((value >> shift) & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
                }
                text = sb.ToString();
                break;
            }
            default:
                text = value.ToString(CultureInfo.InvariantCulture);
                break;
        }
        return string.IsNullOrEmpty(unit) ? text : text + " " + unit;
    }

    public static string FormatFloat(double value, string? unit)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(unit) ? text : text + " " + unit;
    }

    public static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static async Task<string> FormatRegisterAsync(IRegister register, CancellationToken ct)
    {
        var address = await register.GetAddressAsync(ct);
        var length = await register.GetLengthAsync(ct);
        if (length <= 0 || length > MaxRegisterDumpBytes)
            return $"@0x{address:X} length {length} (not dumped)";
        var buffer = new byte[length];
        await register.GetAsync(buffer, ct);
        var preview = (int)Math.Min(length, RegisterPreviewBytes);
        var sb = new StringBuilder(preview * 2 + 32);
        sb.Append("@0x").Append(address.ToString("X", CultureInfo.InvariantCulture)).Append(" length ").Append(length).Append(": ");
        for (var i = 0; i < preview; i++) sb.Append(buffer[i].ToString("X2", CultureInfo.InvariantCulture));
        if (length > preview) sb.Append("...");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ writing

    /// <summary>문자열을 노드 종류에 맞게 해석해 쓴다. Command 는 값 없이 실행한다. 형식 오류는 <see cref="CliUsageException"/>.</summary>
    public static async Task WriteValueAsync(INode node, string? text, CancellationToken ct)
    {
        switch (node)
        {
            case IEnumeration e:
            {
                var symbolic = Require(text, node);
                if (e.GetEntry(symbolic) is not null)
                {
                    await e.SetAsync(symbolic, ct);
                }
                else if (TryParseLong(symbolic, out var numeric))
                {
                    await e.SetIntValueAsync(numeric, ct);
                }
                else
                {
                    var entries = string.Join(", ", e.Entries.Select(x => x.Symbolic));
                    throw new CliUsageException($"'{symbolic}' is not an entry of {node.Name}; entries: {entries}");
                }
                break;
            }
            case ICommand c:
                await c.ExecuteAsync(ct);
                break;
            case IBoolean b:
                await b.SetAsync(ParseBool(Require(text, node)), ct);
                break;
            case IString s:
                await s.SetAsync(text ?? string.Empty, ct);
                break;
            case IFloat f:
                await f.SetAsync(CliArgs.ParseDouble(Require(text, node), "value"), ct);
                break;
            case IInteger i:
                await i.SetAsync(CliArgs.ParseLong(Require(text, node), "value"), ct);
                break;
            case IRegister r:
                await r.SetAsync(ParseHexBytes(Require(text, node)), ct);
                break;
            default:
                throw new CliUsageException($"{node.Name} is a {node.Kind} node and cannot be written");
        }
    }

    private static string Require(string? text, INode node)
        => text ?? throw new CliUsageException($"missing <value> for {node.Name} ({node.Kind})");

    public static bool ParseBool(string text)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "on": case "yes": return true;
            case "0": case "false": case "off": case "no": return false;
            default: throw new CliUsageException($"expected true/false (or 1/0, on/off, yes/no), got '{text}'");
        }
    }

    private static bool TryParseLong(string text, out long value)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>"0A0B0C0D" 또는 "0x0A0B0C0D" 꼴의 16진 바이트열.</summary>
    public static byte[] ParseHexBytes(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        s = s.Replace(" ", string.Empty).Replace(":", string.Empty);
        if (s.Length == 0 || s.Length % 2 != 0)
            throw new CliUsageException($"register value must be an even number of hex digits, got '{text}'");
        var bytes = new byte[s.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                throw new CliUsageException($"register value must be hex digits, got '{text}'");
        }
        return bytes;
    }
}
