using System.Globalization;

namespace GevSharp.Xml;

/// <summary>카메라 XML 위치 URL 의 종류.</summary>
public enum GevXmlUrlKind
{
    /// <summary>장치 메모리 안 — "Local:&lt;file&gt;;&lt;hexAddr&gt;;&lt;hexLen&gt;". READMEM 으로 읽는다.</summary>
    Local,
    /// <summary>호스트 파일 시스템 — "File:///&lt;path&gt;" 또는 "file:&lt;path&gt;".</summary>
    File,
    /// <summary>HTTP/HTTPS 서버 — "http(s)://…".</summary>
    Http,
}

/// <summary>
/// First/Second URL 레지스터(GVBS 0x0200/0x0400)에 적힌 XML 위치 문자열의 해석 결과.
/// 스킴은 대소문자를 가리지 않는다. Local: 의 주소·길이는 0x 접두 없는 16진수지만 접두가 있어도 받아들인다.
/// 어느 형식이든 "?SchemaVersion=x.y.z" 접미가 붙을 수 있다. 쿼리를 떼는 자리는 스킴마다 다르다 —
/// http(s) 는 첫 '?' 부터가 쿼리(SchemaVersion 외의 인자는 URI 에 남긴다), Local: 은 마지막 필드(길이) 뒤의 '?' 만,
/// File: 은 마지막 '?' 뒤가 SchemaVersion 을 실었을 때만 쿼리로 보고 그 밖의 '?' 는 경로·파일 이름의 일부로 둔다.
/// 첫 NUL 이후와 앞뒤 공백은 잘라 낸다. 해석할 수 없으면 원문을 실은 <see cref="GevException"/> 을 낸다.
/// </summary>
public sealed record GevXmlUrl
{
    /// <summary>레지스터에서 읽은 원문(NUL·공백 정리 후).</summary>
    public string Raw { get; }

    public GevXmlUrlKind Kind { get; }

    /// <summary>파일 이름 — 경로의 마지막 조각(Local: 의 첫 필드에 권한부·디렉터리가 붙어 있어도 마지막 조각만). ".zip" 이면 압축 해제 대상이다.</summary>
    public string FileName { get; }

    /// <summary>Local: 의 장치 메모리 시작 주소. 다른 종류는 0.</summary>
    public ulong Address { get; }

    /// <summary>Local: 의 바이트 길이. 다른 종류는 0.</summary>
    public int Length { get; }

    /// <summary>File: 의 호스트 경로(file:// 접두·localhost 제거, 퍼센트 인코딩 해제). 다른 종류는 null.</summary>
    public string? FilePath { get; }

    /// <summary>http(s) 의 절대 URI(SchemaVersion 쿼리 인자는 뺀 것). 다른 종류는 null.</summary>
    public Uri? HttpUri { get; }

    /// <summary>"?SchemaVersion=" 접미의 값. 없으면 null.</summary>
    public string? SchemaVersion { get; }

    /// <summary>파일 이름이 ".zip" 으로 끝나는지 — 내용을 ZIP 으로 풀어야 한다.</summary>
    public bool IsZip => FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private GevXmlUrl(GevXmlUrlKind kind, string raw, string fileName, ulong address, int length, string? filePath, Uri? httpUri, string? schemaVersion)
    {
        Kind = kind;
        Raw = raw;
        FileName = fileName;
        Address = address;
        Length = length;
        FilePath = filePath;
        HttpUri = httpUri;
        SchemaVersion = schemaVersion;
    }

    public override string ToString() => Raw;

    /// <summary>URL 문자열을 해석한다. 형식이 맞지 않으면 원문을 실은 <see cref="GevException"/>.</summary>
    public static GevXmlUrl Parse(string url)
    {
        if (url is null) throw new ArgumentNullException(nameof(url));

        var text = Clean(url);
        if (text.Length == 0) throw Malformed(url, "the URL is empty");

        var colon = text.IndexOf(':');
        if (colon <= 0) throw Malformed(url, "no scheme prefix (expected Local:, File: or http(s):)");
        var scheme = text.Substring(0, colon);
        var rest = text.Substring(colon + 1);

        if (Eq(scheme, "local")) return ParseLocal(url, text, rest);
        if (Eq(scheme, "file")) return ParseFile(url, text, rest);
        if (Eq(scheme, "http") || Eq(scheme, "https")) return ParseHttp(url, text);
        throw Malformed(url, $"unsupported scheme '{scheme}'");
    }

    /// <summary>예외 없이 해석을 시도한다. 실패하면 false 와 null.</summary>
    public static bool TryParse(string? url, out GevXmlUrl? result)
    {
        result = null;
        if (url is null) return false;
        try
        {
            result = Parse(url);
            return true;
        }
        catch (GevException)
        {
            return false;
        }
    }

    // 레지스터 내용은 C 문자열이다 — 첫 NUL 뒤는 버리고 앞뒤 공백을 걷어낸다.
    private static string Clean(string url)
    {
        var nul = url.IndexOf('\0');
        if (nul >= 0) url = url.Substring(0, nul);
        return url.Trim();
    }

    // "k=v&k=v" 꼴 쿼리에서 SchemaVersion 을 뽑고, 나머지 인자는 http 재조립용으로 돌려준다. 반환값은 SchemaVersion 키가 있었는지.
    private static bool ParseQuery(string query, out string? schemaVersion, out string? otherQuery)
    {
        schemaVersion = null;
        otherQuery = null;
        var hasKey = false;

        List<string>? keep = null;
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            var key = (eq < 0 ? part : part.Substring(0, eq)).Trim();
            var value = eq < 0 ? string.Empty : part.Substring(eq + 1);
            if (Eq(key, "SchemaVersion"))
            {
                hasKey = true;
                var v = Uri.UnescapeDataString(value).Trim();
                if (v.Length > 0) schemaVersion = v;
            }
            else
            {
                (keep ??= new List<string>()).Add(part);
            }
        }

        if (keep is not null) otherQuery = string.Join("&", keep);
        return hasKey;
    }

    private static GevXmlUrl ParseLocal(string raw, string text, string rest)
    {
        // 쿼리는 마지막 필드(길이) 뒤에만 올 수 있다 — 파일 이름 안의 '?' 는 이름의 일부다.
        string? schemaVersion = null;
        var q = rest.IndexOf('?', rest.LastIndexOf(';') + 1);
        if (q >= 0)
        {
            ParseQuery(rest.Substring(q + 1), out schemaVersion, out _);
            rest = rest.Substring(0, q);
        }

        var parts = rest.Split(';');
        if (parts.Length != 3)
            throw Malformed(raw, $"expected 'Local:<file>;<hexAddress>;<hexLength>' but found {parts.Length} field(s)");

        // 첫 필드에 권한부·절대 경로가 붙기도 한다("Local:///cam.xml;…", "Local://host/cam.xml;…") — 마지막 경로 조각만 파일 이름이다.
        var fileName = LastSegment(parts[0].Trim()).Trim();
        if (fileName.Length == 0) throw Malformed(raw, "empty file name");

        var address = ParseHex(raw, parts[1], "address");
        var length = ParseHex(raw, parts[2], "length");
        if (length == 0) throw Malformed(raw, "length is zero");
        if (length > int.MaxValue) throw Malformed(raw, $"length 0x{length:X} is too large");

        // Local: 은 READMEM 으로 읽는 장치 메모리다 — GVCP 주소는 32비트라 구간 끝까지 그 안에 들어가야 한다.
        if (address > uint.MaxValue || address + length > 0x1_0000_0000UL)
            throw Malformed(raw, $"address 0x{address:X} + length 0x{length:X} exceeds the 32-bit GVCP address space");

        return new GevXmlUrl(GevXmlUrlKind.Local, text, fileName, address, (int)length, null, null, schemaVersion);
    }

    private static GevXmlUrl ParseFile(string raw, string text, string rest)
    {
        // 호스트 경로에는 '?' 가 들어갈 수 있다 — 마지막 '?' 뒤가 SchemaVersion 을 실은 쿼리일 때만 떼어 내고, 아니면 경로의 일부로 둔다.
        string? schemaVersion = null;
        var q = rest.LastIndexOf('?');
        if (q >= 0 && ParseQuery(rest.Substring(q + 1), out schemaVersion, out _)) rest = rest.Substring(0, q);

        var path = rest.Trim();
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            path = path.Substring(2);
            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                // "file://호스트/경로" — localhost 는 버리고, 드라이브 문자는 경로로 되돌리고, 그 밖의 호스트는 UNC 로 남긴다.
                var slash = path.IndexOf('/');
                var host = slash < 0 ? path : path.Substring(0, slash);
                var tail = slash < 0 ? string.Empty : path.Substring(slash);
                if (Eq(host, "localhost")) path = tail;
                else if (IsDriveSpec(host)) path = host + tail;
                else path = "//" + host + tail;
            }
        }

        // "/C:/…" 꼴의 드라이브 경로는 앞의 슬래시를 뗀다.
        if (path.Length >= 3 && path[0] == '/' && IsDriveSpec(path.Substring(1, 2))) path = path.Substring(1);

        path = Uri.UnescapeDataString(path);
        if (path.Length == 0) throw Malformed(raw, "empty file path");

        var fileName = LastSegment(path);
        if (fileName.Length == 0) throw Malformed(raw, "file path has no file name");

        return new GevXmlUrl(GevXmlUrlKind.File, text, fileName, 0, 0, path, null, schemaVersion);
    }

    private static GevXmlUrl ParseHttp(string raw, string text)
    {
        // http(s) 는 첫 '?' 부터가 쿼리다 — SchemaVersion 만 떼고 나머지 인자는 URI 에 그대로 남긴다.
        var body = text;
        string? schemaVersion = null;
        string? otherQuery = null;
        var q = text.IndexOf('?');
        if (q >= 0)
        {
            ParseQuery(text.Substring(q + 1), out schemaVersion, out otherQuery);
            body = text.Substring(0, q);
        }

        var uriText = otherQuery is null ? body : body + "?" + otherQuery;
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw Malformed(raw, "not a valid absolute http(s) URL");

        var fileName = Uri.UnescapeDataString(LastSegment(uri.AbsolutePath));
        if (fileName.Length == 0) throw Malformed(raw, "URL path has no file name");

        return new GevXmlUrl(GevXmlUrlKind.Http, text, fileName, 0, 0, null, uri, schemaVersion);
    }

    private static ulong ParseHex(string raw, string token, string what)
    {
        var s = token.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (s.Length == 0 || !ulong.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            throw Malformed(raw, $"{what} '{token.Trim()}' is not a hexadecimal number");
        return value;
    }

    private static string LastSegment(string path)
    {
        var i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return i < 0 ? path : path.Substring(i + 1);
    }

    private static bool IsDriveSpec(string s)
        => s.Length == 2 && s[1] == ':' && ((s[0] >= 'A' && s[0] <= 'Z') || (s[0] >= 'a' && s[0] <= 'z'));

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static GevException Malformed(string raw, string reason)
        => new($"Malformed camera XML URL '{Display(raw)}': {reason}.");

    // 메시지에 싣는 원문 — NUL·개행은 보이게 바꾸고 너무 길면 자른다.
    private static string Display(string raw)
    {
        var s = raw.Replace("\0", "\\0").Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length <= 200 ? s : s.Substring(0, 200) + "...";
    }
}
