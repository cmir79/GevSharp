using System.IO.Compression;
using System.Reflection;
using System.Text;
using GevSharp.Gvcp;

namespace GevSharp.Xml;

/// <summary>
/// First/Second URL 레지스터가 가리키는 카메라 XML 을 가져온다.
/// Local: 은 장치 메모리를 READMEM 으로, File: 은 호스트 파일을, http(s) 는 HTTP 로 읽고, ZIP 이면 첫 *.xml 항목을 푼다.
/// 장치와의 접점은 <see cref="IGevPort"/> 하나다 — 실장치·시뮬레이터·테스트 메모리 모델 어느 포트든 같은 경로를 탄다.
/// 디스크 캐시는 호출자가 디렉터리를 줄 때만 켜지며, 파일 이름은 장치 식별 문자열과 URL 의 파일 이름으로 고정된다
/// (같은 장치의 같은 URL 은 늘 같은 파일 하나; 내용은 ZIP 이었더라도 풀어낸 XML 텍스트다).
/// </summary>
public static class GevXmlLoader
{
    private const string LogSrc = "GevXmlLoader";

    /// <summary>http(s) 내려받기 한 번의 전체 시한.</summary>
    public const int HttpTimeoutMs = 10_000;

    /// <summary>
    /// XML(또는 ZIP) 한 개의 크기 상한. Local: 의 선언 길이, File: 의 파일 크기, http(s) 응답 본문(선언된 길이든 실제 수신량이든),
    /// ZIP 항목의 선언 크기와 실제 압축 해제량 모두 이 값을 넘으면 메모리에 쌓기 전에 거부한다 — 장치가 준 값 하나로 호스트가 거대 할당을 하지 않게.
    /// </summary>
    public const int MaxXmlBytes = 64 * 1024 * 1024;

    private static readonly Lazy<HttpClient> _http = new(CreateHttpClient, LazyThreadSafetyMode.ExecutionAndPublication);

    // 텍스트 앞뒤에서 걷어내는 것 — NUL 패딩, 공백, BOM.
    private static readonly char[] _trimChars = { '\0', ' ', '\t', '\r', '\n', (char)0xFEFF, (char)0x00A0 };

    // 캐시 파일 이름에서 '_' 로 바꾸는 문자 — OS 와 무관하게 고정해 어느 플랫폼에서든 같은 이름이 나오게 한다.
    private static readonly char[] _unsafeNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
    private static readonly char[] _osInvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>URL 문자열 해석 — <see cref="GevXmlUrl.Parse"/> 와 같다.</summary>
    public static GevXmlUrl ParseUrl(string url) => GevXmlUrl.Parse(url);

    /// <summary>
    /// First URL(0x0200) 을 읽어 XML 을 가져오고, 읽기·해석·가져오기 어느 단계든 실패하면 Second URL(0x0400) 로 넘어간다.
    /// Second URL 이 First URL 과 같은 문자열이면 같은 실패를 되풀이할 뿐이라(HTTP 시한·긴 메모리 읽기가 두 배가 된다) 다시 시도하지 않는다.
    /// 둘 다 실패하면 두 사유를 모두 실은 <see cref="GevException"/>. 취소는 그대로 전파된다.
    /// </summary>
    public static async Task<GevXmlDoc> LoadAsync(IGevPort port, string? cacheDir = null, CancellationToken ct = default)
    {
        if (port is null) throw new ArgumentNullException(nameof(port));

        var failures = new List<string>(2);
        Exception? last = null;
        string? firstRaw = null;
        for (var i = 0; i < 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            var regName = i == 0 ? "First URL" : "Second URL";
            var regAddr = i == 0 ? GvbsAddr.FirstUrl : GvbsAddr.SecondUrl;

            GevXmlUrl url;
            try
            {
                var raw = await ReadUrlRegisterAsync(port, regAddr, ct).ConfigureAwait(false);
                if (raw.Length == 0)
                {
                    failures.Add($"{regName}: register is empty");
                    GevLog.Debug(LogSrc, $"{regName} register is empty.");
                    continue;
                }

                if (firstRaw is not null && string.Equals(raw, firstRaw, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{regName}: identical to the First URL, not retried");
                    GevLog.Debug(LogSrc, $"{regName} register repeats the First URL; not retrying it.");
                    continue;
                }

                firstRaw ??= raw;
                url = GevXmlUrl.Parse(raw);
                GevLog.Info(LogSrc, $"{regName}: {url.Raw}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                failures.Add($"{regName}: {ex.Message}");
                GevLog.Warn(LogSrc, $"Could not read or parse the {regName} register: {ex.Message}", ex);
                continue;
            }

            try
            {
                return await LoadFromUrlAsync(port, url, cacheDir, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                failures.Add($"{regName} '{url.Raw}': {ex.Message}");
                GevLog.Warn(LogSrc, $"Could not load the camera XML from the {regName} '{url.Raw}': {ex.Message}", ex);
            }
        }

        throw new GevException("Could not load the camera XML from the First/Second URL registers: " + string.Join(" | ", failures), last);
    }

    /// <summary>
    /// 해석된 URL 하나로 XML 을 가져온다(First/Second 폴백 없음). cacheDir 가 있으면 장치 식별 문자열로 캐시 파일을 찾고,
    /// 적중하면 XML 본문 전송 없이 캐시 텍스트를 돌려준다. 캐시 읽기·쓰기 실패는 경고 로그로만 남고 결과에는 영향이 없다.
    /// </summary>
    public static async Task<GevXmlDoc> LoadFromUrlAsync(IGevPort port, GevXmlUrl url, string? cacheDir = null, CancellationToken ct = default)
    {
        if (port is null) throw new ArgumentNullException(nameof(port));
        if (url is null) throw new ArgumentNullException(nameof(url));

        string? cachePath = null;
        if (!string.IsNullOrEmpty(cacheDir))
        {
            cachePath = await ResolveCachePathAsync(port, cacheDir!, url.FileName, ct).ConfigureAwait(false);
            if (cachePath is not null)
            {
                var cached = TryReadCache(cachePath);
                if (cached is not null)
                {
                    GevLog.Info(LogSrc, $"Camera XML cache hit: '{cachePath}' ({cached.Length} chars).");
                    return new GevXmlDoc(cached, url.Raw, url.FileName, url.SchemaVersion);
                }
            }
        }

        var bytes = await FetchAsync(port, url, ct).ConfigureAwait(false);
        var xml = ExtractXml(bytes, url.FileName);

        if (cachePath is not null) WriteCache(cachePath, xml);
        return new GevXmlDoc(xml, url.Raw, url.FileName, url.SchemaVersion);
    }

    /// <summary>
    /// 받은 바이트를 XML 텍스트로 만든다. 파일 이름이 .zip 이거나 ZIP 서명으로 시작하면 첫 *.xml 항목을 풀고,
    /// 아니면 BOM 을 보고 인코딩을 정해(기본 UTF-8) 그대로 디코딩한다. 앞뒤 NUL·공백·BOM 을 걷어낸 결과가 '&lt;' 로 시작하지 않으면 <see cref="GevException"/>.
    /// </summary>
    public static string ExtractXml(byte[] bytes, string fileName) => ExtractXml(bytes, fileName, MaxXmlBytes);

    // 상한을 인자로 받는 판 — 테스트가 작은 상한으로 ZIP 크기 검사를 밟아 볼 수 있게 한다.
    internal static string ExtractXml(byte[] bytes, string fileName, int maxBytes)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        if (fileName is null) throw new ArgumentNullException(nameof(fileName));

        var isZipName = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var isZip = isZipName || HasZipMagic(bytes);
        if (isZip && !isZipName) GevLog.Debug(LogSrc, $"'{fileName}' does not end with .zip but starts with a ZIP signature; extracting it as a ZIP archive.");

        var text = isZip ? ReadZipXml(bytes, fileName, maxBytes) : DecodeText(bytes);
        text = text.Trim(_trimChars);
        if (text.Length == 0 || text[0] != '<')
            throw new GevException($"Content of '{fileName}' does not look like XML: expected '<' but found \"{Preview(text)}\" ({bytes.Length} bytes).");
        return text;
    }

    /// <summary>
    /// 캐시 파일 이름 — "{Manufacturer}_{Model}_{DeviceVersion}_{FileName}" 을 파일 이름에 안전한 문자로 정리한 것.
    /// 파일 이름은 URL 에 적힌 그대로 둔다(.zip 도 .zip) — 같은 장치의 cam.zip 과 cam.xml 이 한 캐시 파일을 나눠 쓰면
    /// 먼저 받은 쪽이 다른 쪽 대신 나가므로 확장자를 바꾸지 않는다. 빈 조각은 "unknown".
    /// </summary>
    public static string CacheFileName(string manufacturer, string model, string deviceVersion, string fileName)
    {
        if (manufacturer is null) throw new ArgumentNullException(nameof(manufacturer));
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (deviceVersion is null) throw new ArgumentNullException(nameof(deviceVersion));
        if (fileName is null) throw new ArgumentNullException(nameof(fileName));

        return Sanitize(manufacturer) + "_" + Sanitize(model) + "_" + Sanitize(deviceVersion) + "_" + Sanitize(fileName);
    }

    // ---- 출처별 가져오기 ----

    private static Task<byte[]> FetchAsync(IGevPort port, GevXmlUrl url, CancellationToken ct) => url.Kind switch
    {
        GevXmlUrlKind.Local => ReadDeviceMemoryAsync(port, url, ct),
        GevXmlUrlKind.File => ReadHostFileAsync(url, ct),
        GevXmlUrlKind.Http => DownloadAsync(url, ct),
        _ => throw new GevException($"Unsupported camera XML URL kind {url.Kind} ('{url.Raw}')."),
    };

    private static async Task<byte[]> ReadDeviceMemoryAsync(IGevPort port, GevXmlUrl url, CancellationToken ct)
    {
        if (url.Length > MaxXmlBytes)
            throw new GevException($"Camera XML '{url.FileName}' declares {url.Length} bytes, above the {MaxXmlBytes} byte limit (URL '{url.Raw}').");

        // READMEM 은 4바이트 정렬 주소·4의 배수 길이만 받는다 — 앞뒤를 4 경계로 넓혀 읽고 선언된 구간만 잘라 낸다.
        var start = url.Address & ~3UL;
        var lead = (int)(url.Address - start);
        var total = RoundUp4(lead + url.Length);
        var buf = new byte[total];
        var chunks = 0;
        try
        {
            for (var offset = 0; offset < total; offset += GvcpConst.MaxMemPayload)
            {
                ct.ThrowIfCancellationRequested();
                var n = Math.Min(GvcpConst.MaxMemPayload, total - offset);
                await port.ReadAsync(start + (ulong)offset, new Memory<byte>(buf, offset, n), ct).ConfigureAwait(false);
                chunks++;
                if (GevLog.IsEnabled(GevLogLevel.Trace))
                    GevLog.Trace(LogSrc, $"Read XML chunk {chunks}: 0x{start + (ulong)offset:X8} +{n} ({offset + n}/{total} bytes).");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GevException($"Failed to read camera XML '{url.FileName}' from device memory at 0x{url.Address:X8} ({url.Length} bytes): {ex.Message}", ex);
        }

        GevLog.Info(LogSrc, $"Read camera XML '{url.FileName}' from device memory at 0x{url.Address:X8}: {url.Length} bytes in {chunks} chunk(s).");

        if (lead == 0 && total == url.Length) return buf;
        var data = new byte[url.Length];
        Buffer.BlockCopy(buf, lead, data, 0, url.Length);
        return data;
    }

    private static async Task<byte[]> ReadHostFileAsync(GevXmlUrl url, CancellationToken ct)
    {
        var path = url.FilePath!;
        GevLog.Info(LogSrc, $"Reading camera XML from file '{path}'.");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            if (fs.Length > MaxXmlBytes)
                throw new GevException($"Camera XML file '{path}' is {fs.Length} bytes, above the {MaxXmlBytes} byte limit.");
            using var ms = new MemoryStream((int)fs.Length);
            await fs.CopyToAsync(ms, 81920, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GevException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GevException($"Failed to read camera XML file '{path}': {ex.Message}", ex);
        }
    }

    private static async Task<byte[]> DownloadAsync(GevXmlUrl url, CancellationToken ct)
    {
        var uri = url.HttpUri!;
        GevLog.Info(LogSrc, $"Downloading camera XML from '{uri}'.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // 본문까지 받은 뒤 돌아오는 모드 — HttpClient.Timeout 이 헤더뿐 아니라 본문 수신까지 덮는다(헤더만 받고 돌아오는 모드는
            // 본문 읽기가 시한·취소 밖에 놓이는 런타임이 있다). 본문 버퍼는 클라이언트의 MaxResponseContentBufferSize(= MaxXmlBytes) 로
            // 묶여 있어, Content-Length 가 그보다 크면 할당 전에, 길이 선언 없이 흘러드는 본문은 상한에 닿는 순간 HttpRequestException 으로 끊긴다.
            using var response = await _http.Value.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new GevException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} while downloading camera XML from '{uri}'.");
#if NET8_0_OR_GREATER
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
#else
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif
            if (bytes.Length > MaxXmlBytes)
                throw new GevException($"Camera XML downloaded from '{uri}' is {bytes.Length} bytes, above the {MaxXmlBytes} byte limit.");
            return bytes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // 호출자가 취소하지 않았는데 취소 예외가 났다면 HttpClient.Timeout 이 끊은 것이다.
            throw new GevTimeoutException($"Downloading camera XML from '{uri}' timed out after {HttpTimeoutMs} ms.");
        }
        catch (GevException)
        {
            throw;
        }
#if NET8_0_OR_GREATER
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            // 응답 본문이 버퍼 상한(MaxXmlBytes)을 넘어 클라이언트가 끊은 것 — 다른 런타임에서는 아래 일반 경로로 같은 사실이 메시지에 실린다.
            throw new GevException($"Camera XML downloaded from '{uri}' exceeds the {MaxXmlBytes} byte limit: {ex.Message}", ex);
        }
#endif
        catch (Exception ex)
        {
            throw new GevException($"Failed to download camera XML from '{uri}': {ex.Message}", ex);
        }
    }

    private static HttpClient CreateHttpClient()
        => new()
        {
            Timeout = TimeSpan.FromMilliseconds(HttpTimeoutMs),
            // 응답 본문 버퍼 상한 — 선언된 길이가 이보다 크면 할당 없이, 선언 없는 본문은 이만큼 쌓인 시점에 거부된다.
            MaxResponseContentBufferSize = MaxXmlBytes,
        };

    // ---- 바이트 → 텍스트 ----

    private static string ReadZipXml(byte[] bytes, string fileName, int maxBytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

            ZipArchiveEntry? entry = null;
            foreach (var e in zip.Entries)
            {
                if (e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    entry = e;
                    break;
                }
            }

            if (entry is null)
            {
                var names = string.Join(", ", zip.Entries.Select(e => e.FullName));
                throw new GevException($"ZIP '{fileName}' contains no .xml entry (entries: {(names.Length == 0 ? "none" : names)}).");
            }

            // 헤더가 선언한 크기는 값싼 조기 거부용이고, 실제 상한은 아래 CappedReadStream 이 풀려 나오는 바이트를 세어 지킨다.
            if (entry.Length > maxBytes)
                throw new GevException($"ZIP entry '{entry.FullName}' in '{fileName}' is {entry.Length} bytes, above the {maxBytes} byte limit.");

            if (zip.Entries.Count > 1)
                GevLog.Debug(LogSrc, $"ZIP '{fileName}' has {zip.Entries.Count} entries; using '{entry.FullName}'.");

            using var es = new CappedReadStream(entry.Open(), maxBytes, $"ZIP entry '{entry.FullName}' in '{fileName}'");
            using var reader = new StreamReader(es, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (GevException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GevException($"Failed to read '{fileName}' as a ZIP archive: {ex.Message}", ex);
        }
    }

    // BOM 이 있으면 그 인코딩(UTF-8/16/32), 없으면 UTF-8. StreamReader 가 BOM 을 떼어 준다.
    private static string DecodeText(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(ms, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool HasZipMagic(byte[] bytes)
        => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    // ---- 캐시 ----

    private static async Task<string?> ResolveCachePathAsync(IGevPort port, string cacheDir, string fileName, CancellationToken ct)
    {
        try
        {
            // 제조사·모델·버전 문자열은 GVBS 에 연달아 놓여 있어 한 번에 읽는다.
            var start = GvbsAddr.ManufacturerName;
            var total = (int)(GvbsAddr.DeviceVersion - start) + GvbsAddr.DeviceVersionLen;
            var buf = new byte[total];
            await port.ReadAsync(start, buf, ct).ConfigureAwait(false);

            var manufacturer = DecodeCString(buf, 0, GvbsAddr.ManufacturerNameLen);
            var model = DecodeCString(buf, (int)(GvbsAddr.ModelName - start), GvbsAddr.ModelNameLen);
            var version = DecodeCString(buf, (int)(GvbsAddr.DeviceVersion - start), GvbsAddr.DeviceVersionLen);
            return Path.Combine(cacheDir, CacheFileName(manufacturer, model, version, fileName));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            GevLog.Warn(LogSrc, $"Could not build the XML cache key from the bootstrap registers; continuing without cache: {ex.Message}", ex);
            return null;
        }
    }

    // 캐시 파일이 없거나 읽을 수 없거나 XML 로 보이지 않으면 null — 미스로 취급해 장치에서 다시 받고 덮어쓴다.
    private static string? TryReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8).Trim(_trimChars);
            if (text.Length == 0 || text[0] != '<')
            {
                GevLog.Warn(LogSrc, $"Ignoring XML cache file '{path}': content does not look like XML.");
                return null;
            }

            return text;
        }
        catch (Exception ex)
        {
            GevLog.Warn(LogSrc, $"Could not read XML cache file '{path}': {ex.Message}", ex);
            return null;
        }
    }

    // 임시 파일에 쓴 뒤 이름을 바꿔 넣는다 — 동시에 읽는 쪽이 반쯤 쓰인 파일도, 사라진 파일도 보지 않게.
    // 테스트가 캐시 교체만 따로 여러 번 밟아 볼 수 있도록 internal 이다.
    internal static void WriteCache(string path, string xml)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            File.WriteAllText(tmp, xml, new UTF8Encoding(false));
            SwapIntoPlace(tmp, path);
            GevLog.Info(LogSrc, $"Camera XML cached to '{path}' ({xml.Length} chars).");
        }
        catch (Exception ex)
        {
            GevLog.Warn(LogSrc, $"Could not write XML cache file '{path}': {ex.Message}", ex);
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
                // 임시 파일 정리 실패는 결과와 무관하다.
            }
        }
    }

    // 3인자 덮어쓰기 Move 를 한 번만 찾아 둔다. 이 오버로드는 netstandard2.0·2.1 참조 면에 없어 그 자산은 컴파일로는
    // 불러 쓸 수 없지만, 그 자산을 실제로 실는 런타임에는 있는 경우가 많다(.NET Core 3.0 이상). 있으면 써서 이름이 비는
    // 구간을 없애고, 없으면(.NET Framework·예전 Mono) 교체 경로로 내려간다. 네이티브 호출이 아니라 관리 메서드 하나를 묶어 두는 것뿐이다.
    // net8.0 은 이 지연 경로를 타지 않고 컴파일 시점에 바로 묶인다 — 값은 테스트가 볼 수 있게 어느 자산에서든 채워 둔다.
    internal static Action<string, string, bool>? OverwriteMove { get; } = ResolveOverwriteMove();

    private static Action<string, string, bool>? ResolveOverwriteMove()
    {
        try
        {
            var mi = typeof(File).GetMethod(
                "Move", BindingFlags.Public | BindingFlags.Static,
                binder: null, types: new[] { typeof(string), typeof(string), typeof(bool) }, modifiers: null);

            return mi is null ? null : (Action<string, string, bool>)mi.CreateDelegate(typeof(Action<string, string, bool>));
        }
        catch (Exception ex)
        {
            // 찾지 못하면 교체 경로로 간다 — 기능이 죽는 것이 아니라 교체 구간이 길어지는 정도라 경고로만 남긴다.
            GevLog.Warn(LogSrc, $"Overwrite-move probe failed, falling back to replace-based cache swap: {ex.Message}", ex);
            return null;
        }
    }

    // 다 쓴 임시 파일을 캐시 자리에 밀어 넣는다.
    private static void SwapIntoPlace(string tmp, string path)
    {
        // 덮어쓰기 Move 는 이름을 한 번에 갈아끼운다 — 대상 이름이 사라지는 구간이 없어, 같은 캐시를 보던 다른
        // 프로세스는 옇 내용 아니면 새 내용을 보고 "없음" 은 보지 않는다. 그래서 쓸 수 있으면 언제나 이쪽을 먼저 쓴다.
#if NET8_0_OR_GREATER
        File.Move(tmp, path, overwrite: true);
#else
        var move = OverwriteMove;
        if (move is not null) move(tmp, path, true);
        else SwapByReplace(tmp, path);
#endif
    }

    // 덮어쓰기 Move 가 런타임에도 없는 자리가 타는 경로 — 자산에 따라 갈리지 않게 어디서나 컴파일해 둔다.
    // 이름이 잠깐 비는 구간을 여기서 없앨 수는 없다. 실측으로도 교체(File.Replace)에 그 구간이 있다 —
    // 다만 이름을 두 번 바꾸는 뒤의 경로보다는 짧다. 교체를 먼저 쓰는 진짜 이유는 실패했을 때다:
    // 교체가 실패하면 옇 캐시가 자리에 그대로 남는다. 그래서 실패를 통째로 삼켜 캐시를 버리지 않는다 —
    // 임시 파일이 사라져 난 실패처럼 이미 잃은 것이 있는 실패는 그대로 올려보내 옇 캐시를 지키고
    // (부르는 쪽이 경고만 남기고 넘어간다 — 캐시 이름에 모델·버전이 들어 있어 옇 내용이 어긋날 일은 없다),
    // 교체만 거부당했고 아직 잃은 것이 없는 실패만 아래 비켜 두기 경로로 내려간다.
    // 이름이 없는 순간에 읽는 쪽은 캐시 미스로 보고 장치에서 다시 받는다. 반쯤 쓰인 내용은 어느 경로에서도 보이지 않는다.
    internal static void SwapByReplace(string tmp, string path)
    {
        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }

        try
        {
            File.Replace(tmp, path, destinationBackupFileName: null);
            return;
        }
        catch (FileNotFoundException) when (!File.Exists(path))
        {
            // 그 사이 대상이 사라졌다 — 덮어쓸 것이 없으니 그냥 옮긴다. 임시 파일이 없어서 난 예외라면
            // 대상은 그대로 있으므로 이 조건에 걸리지 않고 올라간다.
            File.Move(tmp, path);
            return;
        }
        catch (Exception ex) when (IsReplaceRefusal(ex) && File.Exists(tmp) && File.Exists(path))
        {
            // 교체는 거부됐지만 임시 파일도 옇 캐시도 그대로 있다 — 아직 잃은 것이 없으니 마지막 수단을 맛본다.
        }

        SwapByMoveAside(tmp, path);
    }

    // 교체를 받아 주지 않는다는 신호는 한 종류로 오지 않는다 — 파일 시스템이 거부하면 IOException,
    // 권한으로 막힐 때는 UnauthorizedAccessException, 교체 자체를 안 갖춘 플랫폼이면 NotSupportedException 이다.
    // 예외 종류만으로는 갈라지지 않아, 불러 쓰는 쪽에서 "아직 잃은 것이 없다" 를 같이 확인해 마지막 수단으로 내려간다.
    private static bool IsReplaceRefusal(Exception ex)
        => ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException;

    // 교체가 안 되는 자리의 마지막 수단. 옇 캐시를 지우지 않고 옆으로 비켜 둔 다음 새 파일을 넣고,
    // 들어간 뒤에야 비켜 둔 것을 지운다 — 중간에 실패하면 도로 돌려놓아 캐시를 잃지 않는다.
    // 지우고 옮기는 것보다 이름이 비는 구간은 오히려 길다(이름 바꾸기가 두 번이라 실측도 그렇게 나왔다).
    // 그래도 이쪽을 택한다: 비는 구간은 캐시 미스 한 번으로 끝나지만, 캐시를 잃으면 다음 쓰기까지 계속 미스다.
    internal static void SwapByMoveAside(string tmp, string path)
    {
        var aside = path + "." + Guid.NewGuid().ToString("N") + ".old";
        File.Move(path, aside);
        try
        {
            File.Move(tmp, path);
        }
        catch
        {
            try
            {
                File.Move(aside, path);
            }
            catch (Exception restoreEx)
            {
                // 되돌리기까지 실패하면 옇 내용은 비켜 둔 이름으로 남는다 — 어디 있는지를 남긴다.
                GevLog.Warn(LogSrc, $"Cache swap failed and the previous content is left at '{aside}': {restoreEx.Message}", restoreEx);
            }

            throw;
        }

        try
        {
            // 읽기 전용 캐시를 비켜 둔 것이면 그대로는 지워지지 않는다 — 쓸 때마다 사본이 하나씩 쌓이지 않게 속성만 내린다.
            var attrs = File.GetAttributes(aside);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(aside, attrs & ~FileAttributes.ReadOnly);
            File.Delete(aside);
        }
        catch
        {
            // 비켜 둔 사본 정리 실패는 결과와 무관하다.
        }
    }

    private static string Sanitize(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return "unknown";

        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c < 0x20 || c == 0x7F || char.IsWhiteSpace(c)
                || Array.IndexOf(_unsafeNameChars, c) >= 0 || Array.IndexOf(_osInvalidNameChars, c) >= 0)
                chars[i] = '_';
        }

        return new string(chars);
    }

    // ---- 레지스터 문자열 ----

    private static async Task<string> ReadUrlRegisterAsync(IGevPort port, uint addr, CancellationToken ct)
    {
        var buf = new byte[GvbsAddr.UrlLen];
        await port.ReadAsync(addr, buf, ct).ConfigureAwait(false);
        return DecodeCString(buf, 0, buf.Length);
    }

    // 고정 길이 필드 안의 NUL 종료 문자열. 문자 집합은 UTF-8 로 본다(ASCII 장치도 그대로 맞는다).
    private static string DecodeCString(byte[] buf, int offset, int length)
    {
        var end = offset;
        var limit = offset + length;
        while (end < limit && buf[end] != 0) end++;
        return Encoding.UTF8.GetString(buf, offset, end - offset).Trim();
    }

    private static int RoundUp4(int n) => (n + 3) & ~3;

    private static string Preview(string text)
    {
        if (text.Length == 0) return "(empty)";
        var n = Math.Min(24, text.Length);
        var chars = new char[n];
        for (var i = 0; i < n; i++)
        {
            var c = text[i];
            chars[i] = c >= 0x20 && c < 0x7F ? c : '.';
        }

        return new string(chars) + (text.Length > n ? "..." : string.Empty);
    }
}
