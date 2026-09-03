using System.Globalization;

namespace GevSharp.Cli.Commands;

/// <summary>
/// 명령 하나가 받는 옵션 사양. 값 옵션(--timeout 500)과 플래그 옵션(--no-resend)을 이름으로 구분하고 한 글자 별칭(-n → count)을 둔다.
/// 사양에 없는 옵션은 파서가 거절한다 — 오타가 조용히 무시되지 않게.
/// </summary>
public sealed class CliOptSpec
{
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _valued = new(StringComparer.Ordinal);
    private readonly Dictionary<char, string> _shorts = new();

    /// <summary>값 없이 켜기만 하는 옵션.</summary>
    public CliOptSpec Flag(string name, char? shortName = null)
    {
        _flags.Add(name);
        if (shortName is { } c) _shorts[c] = name;
        return this;
    }

    /// <summary>값 하나를 받는 옵션. 여러 번 써도 된다.</summary>
    public CliOptSpec Value(string name, char? shortName = null)
    {
        _valued.Add(name);
        if (shortName is { } c) _shorts[c] = name;
        return this;
    }

    /// <summary>선언된 옵션 이름 전부(짧은 이름 제외) — 사용법 문구와 선언이 어긋나지 않았는지 보는 테스트가 쓴다.</summary>
    public IEnumerable<string> Names => _flags.Concat(_valued);

    public bool IsFlag(string name) => _flags.Contains(name);
    public bool IsValued(string name) => _valued.Contains(name);
    public bool IsKnown(string name) => _flags.Contains(name) || _valued.Contains(name);
    public bool TryResolveShort(char shortName, out string name) => _shorts.TryGetValue(shortName, out name!);

    /// <summary>다른 사양의 옵션을 전부 합친다(전역 옵션을 명령 사양에 얹을 때). 같은 이름이면 이쪽이 이긴다.</summary>
    public CliOptSpec Merge(CliOptSpec other)
    {
        foreach (var f in other._flags) _flags.Add(f);
        foreach (var v in other._valued) _valued.Add(v);
        foreach (var kv in other._shorts)
        {
            if (!_shorts.ContainsKey(kv.Key)) _shorts[kv.Key] = kv.Value;
        }
        return this;
    }
}

/// <summary>
/// 손으로 만든 인자 파서의 결과. 문법: <c>--name value</c> | <c>--name=value</c> | <c>-x value</c> | <c>-xvalue</c> | <c>--</c>(이후 전부 위치 인자).
/// 음수로 읽히는 토큰(-1, -0.5)은 옵션이 아니라 위치 인자다. 같은 옵션이 여러 번 오면 전부 보관하고 단일 값 조회는 마지막 것을 돌려준다.
/// 형 변환 실패는 전부 <see cref="CliUsageException"/> — 종료 코드 1 로 이어진다.
/// </summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly List<string> _positionals = new();

    private CliArgs() { }

    public IReadOnlyList<string> Positionals => _positionals;

    public static CliArgs Parse(IReadOnlyList<string> tokens, CliOptSpec spec)
    {
        if (tokens is null) throw new ArgumentNullException(nameof(tokens));
        if (spec is null) throw new ArgumentNullException(nameof(spec));

        var result = new CliArgs();
        var onlyPositionals = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (onlyPositionals || !LooksLikeOption(token))
            {
                result._positionals.Add(token);
                continue;
            }
            if (token == "--")
            {
                onlyPositionals = true;
                continue;
            }

            string name;
            string? inline;
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var body = token.Substring(2);
                var eq = body.IndexOf('=');
                name = eq < 0 ? body : body.Substring(0, eq);
                inline = eq < 0 ? null : body.Substring(eq + 1);
                if (name.Length == 0) throw new CliUsageException($"malformed option '{token}'");
            }
            else
            {
                // -n 5 / -n5 : 한 글자 별칭. 묶어 쓰기(-ab)는 지원하지 않는다.
                if (!spec.TryResolveShort(token[1], out name)) throw new CliUsageException($"unknown option '{token}'");
                inline = token.Length > 2 ? token.Substring(2) : null;
            }

            if (spec.IsFlag(name))
            {
                if (inline is not null) throw new CliUsageException($"option --{name} does not take a value");
                result._flags.Add(name);
            }
            else if (spec.IsValued(name))
            {
                if (inline is null)
                {
                    if (i + 1 >= tokens.Count) throw new CliUsageException($"option --{name} requires a value");
                    inline = tokens[++i];
                }
                if (!result._values.TryGetValue(name, out var list))
                {
                    list = new List<string>();
                    result._values[name] = list;
                }
                list.Add(inline);
            }
            else
            {
                throw new CliUsageException($"unknown option '{token}'");
            }
        }
        return result;
    }

    /// <summary>'-' 로 시작하되 숫자(음수)는 아닌 토큰.</summary>
    private static bool LooksLikeOption(string token)
    {
        if (token.Length < 2 || token[0] != '-') return false;
        return !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    // ------------------------------------------------------------------ raw access

    /// <summary>플래그가 켜졌거나 값 옵션이 한 번이라도 주어졌는지.</summary>
    public bool Has(string name) => _flags.Contains(name) || _values.ContainsKey(name);

    /// <summary>값 옵션의 마지막 값. 없으면 null.</summary>
    public string? Get(string name) => _values.TryGetValue(name, out var list) ? list[list.Count - 1] : null;

    /// <summary>값 옵션의 모든 값(주어진 순서).</summary>
    public IReadOnlyList<string> GetAll(string name) => _values.TryGetValue(name, out var list) ? list : Array.Empty<string>();

    public string Require(string name) => Get(name) ?? throw new CliUsageException($"option --{name} is required");

    /// <summary>index 번째 위치 인자. 없으면 <paramref name="what"/> 을 실은 사용법 오류.</summary>
    public string Positional(int index, string what)
        => index < _positionals.Count ? _positionals[index] : throw new CliUsageException($"missing argument <{what}>");

    public string? PositionalOrNull(int index) => index < _positionals.Count ? _positionals[index] : null;

    /// <summary>위치 인자가 <paramref name="max"/> 개를 넘으면 사용법 오류 — 오타로 붙은 토큰이 무시되지 않게.</summary>
    public void RejectExtraPositionals(int max)
    {
        if (_positionals.Count > max)
            throw new CliUsageException($"unexpected argument '{_positionals[max]}'");
    }

    // ------------------------------------------------------------------ typed access

    public int GetInt(string name, int fallback, int min = int.MinValue, int max = int.MaxValue)
    {
        var text = Get(name);
        if (text is null) return fallback;
        var value = ParseInt(text, "--" + name);
        if (value < min || value > max)
            throw new CliUsageException($"option --{name} must be between {min} and {max}, got {value}");
        return value;
    }

    public long GetLong(string name, long fallback, long min = long.MinValue, long max = long.MaxValue)
    {
        var text = Get(name);
        if (text is null) return fallback;
        var value = ParseLong(text, "--" + name);
        if (value < min || value > max)
            throw new CliUsageException($"option --{name} must be between {min} and {max}, got {value}");
        return value;
    }

    public double GetDouble(string name, double fallback, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
    {
        var text = Get(name);
        if (text is null) return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value))
            throw new CliUsageException($"option --{name} expects a number, got '{text}'");
        if (value < min || value > max)
            throw new CliUsageException($"option --{name} must be between {min} and {max}, got {text}");
        return value;
    }

    /// <summary>16진 옵션("0x10030" 또는 "10030"). 없으면 null.</summary>
    public uint? GetHex(string name)
    {
        var text = Get(name);
        return text is null ? null : ParseHex(text, "--" + name);
    }

    /// <summary>바이트 수 옵션 — 접미사 k/m/g(1024 배수)를 받는다. 없으면 fallback.</summary>
    public long GetBytes(string name, long fallback, long min = 0, long max = long.MaxValue)
    {
        var text = Get(name);
        if (text is null) return fallback;
        var value = ParseBytes(text, "--" + name);
        if (value < min || value > max)
            throw new CliUsageException($"option --{name} must be between {min} and {max} bytes, got {value}");
        return value;
    }

    /// <summary>열거형 옵션(대소문자 무시). 없으면 fallback, 이름이 틀리면 허용 값 목록을 실은 사용법 오류.</summary>
    public T GetEnum<T>(string name, T fallback) where T : struct, Enum
    {
        var text = Get(name);
        if (text is null) return fallback;
        if (Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(typeof(T), value)) return value;
        var allowed = string.Join(" | ", Enum.GetNames(typeof(T)).Select(n => n.ToLowerInvariant()));
        throw new CliUsageException($"option --{name} expects one of {allowed}, got '{text}'");
    }

    // ------------------------------------------------------------------ parsers (shared with positional arguments)

    public static int ParseInt(string text, string what)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        throw new CliUsageException($"{what} expects an integer, got '{text}'");
    }

    public static long ParseLong(string text, string what)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return hex;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        throw new CliUsageException($"{what} expects an integer (decimal or 0x hex), got '{text}'");
    }

    public static double ParseDouble(string text, string what)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && !double.IsNaN(value)) return value;
        throw new CliUsageException($"{what} expects a number, got '{text}'");
    }

    public static uint ParseHex(string text, string what)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (s.Length > 0 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return value;
        throw new CliUsageException($"{what} expects a hexadecimal address (for example 0x10030), got '{text}'");
    }

    public static long ParseBytes(string text, string what)
    {
        var s = text.Trim();
        long multiplier = 1;
        if (s.Length > 0)
        {
            switch (char.ToLowerInvariant(s[s.Length - 1]))
            {
                case 'k': multiplier = 1024L; s = s.Substring(0, s.Length - 1); break;
                case 'm': multiplier = 1024L * 1024; s = s.Substring(0, s.Length - 1); break;
                case 'g': multiplier = 1024L * 1024 * 1024; s = s.Substring(0, s.Length - 1); break;
            }
        }
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
        {
            // 접미사를 곱해 넘치는 값도 사용자 입력 오류다 — OverflowException 으로 새어 나가 장치 오류(2)로 보이지 않게.
            if (value > long.MaxValue / multiplier)
                throw new CliUsageException($"{what} is too large: '{text}'");
            return value * multiplier;
        }
        throw new CliUsageException($"{what} expects a byte count (optionally with k/m/g suffix), got '{text}'");
    }
}
