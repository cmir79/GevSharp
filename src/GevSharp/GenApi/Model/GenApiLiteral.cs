using System.Globalization;

namespace GevSharp.GenApi.Model;

/// <summary>
/// GenApi XML 텍스트 리터럴 해석. 정수는 10진(부호 허용)과 0x 16진, 실수는 불변 문화권 표기, 불리언은 Yes/No(true/false/1/0 도 허용).
/// 해석 실패는 어느 노드의 어느 항목인지 붙여 <see cref="GenApiException"/> 으로 낸다 — 조용히 0 으로 흘리지 않는다.
/// </summary>
internal static class GenApiLiteral
{
    public static bool TryParseInt64(string? text, out long value)
    {
        value = 0;
        if (text is null) return false;
        var s = text.Trim();
        if (s.Length == 0) return false;

        var negative = false;
        if (s[0] == '-' || s[0] == '+')
        {
            negative = s[0] == '-';
            s = s.Substring(1);
        }

        if (s.Length > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
        {
            // 16진은 64비트 전체 폭을 허용한다(0xFFFFFFFFFFFFFFFF 같은 마스크) — ulong 으로 읽고 비트 그대로 옮긴다.
            if (!ulong.TryParse(s.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var ul))
                return false;
            value = unchecked((long)ul);
            if (negative) value = unchecked(-value);
            return true;
        }

        // 10진은 부호를 뗀 절대값을 ulong 으로 읽는다 — long.MinValue(절대값이 long.MaxValue + 1)는 받고 그 밖은 거부하기 위해서다.
        if (!ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude))
            return false;
        if (negative)
        {
            if (magnitude > 0x8000000000000000UL) return false;
            value = unchecked((long)(0UL - magnitude));
            return true;
        }
        if (magnitude > long.MaxValue) return false;
        value = (long)magnitude;
        return true;
    }

    public static long ParseInt64(string? text, string what, string? nodeName)
    {
        if (TryParseInt64(text, out var v)) return v;
        throw new GenApiException($"Invalid integer literal '{text}' in {what} of node '{nodeName}'.", nodeName);
    }

    public static int ParseInt32(string? text, string what, string? nodeName)
    {
        var v = ParseInt64(text, what, nodeName);
        if (v < int.MinValue || v > int.MaxValue)
            throw new GenApiException($"Integer literal '{text}' in {what} of node '{nodeName}' is out of the 32-bit range.", nodeName);
        return (int)v;
    }

    public static bool TryParseDouble(string? text, out double value)
    {
        value = 0;
        if (text is null) return false;
        var s = text.Trim();
        if (s.Length == 0) return false;
        // 정수 리터럴(특히 0x 16진)도 실수 자리에 올 수 있다.
        if (TryParseInt64(s, out var l))
        {
            value = l;
            return true;
        }
        // 크기를 넘는 실수 리터럴(예: 1e400)은 런타임마다 다르게 굴러 간다 — .NET Core 3.0+ 는 참을 돌려주며 무한대를 담고,
        // .NET Framework 는 거짓을 돌려준다. 그대로 두면 같은 XML 이 자산에 따라 "무한대 한계" 로 바인딩되거나 예외로 죽는다.
        // 무한대는 값으로 받지 않는다 — 어느 자산에서든 잘못된 리터럴로 보고한다.
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsInfinity(value);
    }

    public static double ParseDouble(string? text, string what, string? nodeName)
    {
        if (TryParseDouble(text, out var v)) return v;
        throw new GenApiException($"Invalid number literal '{text}' in {what} of node '{nodeName}'.", nodeName);
    }

    public static bool ParseYesNo(string? text, string what, string? nodeName)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "yes":
            case "true":
            case "1":
                return true;
            case "no":
            case "false":
            case "0":
                return false;
            default:
                throw new GenApiException($"Invalid Yes/No literal '{text}' in {what} of node '{nodeName}'.", nodeName);
        }
    }

    /// <summary>EventID·ChunkID 같은 접두 없는 16진 문자열(0x 가 붙어 있어도 받는다).</summary>
    public static ulong ParseHex(string? text, string what, string? nodeName)
    {
        var s = text?.Trim() ?? "";
        if (s.Length > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) s = s.Substring(2);
        if (s.Length > 0 && ulong.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            return v;
        throw new GenApiException($"Invalid hexadecimal literal '{text}' in {what} of node '{nodeName}'.", nodeName);
    }
}
