using System.Diagnostics;
using GevSharp.GenApi;
using GevSharp.GenApi.Model;

#pragma warning disable xUnit1051

namespace GevSharp.Tests.GenApi.Runtime;

/// <summary>
/// 로컬 전용 코퍼스 검사 — 환경변수 <c>GEVSHARP_VENDOR_XML</c> 가 가리키는 XML 파일(또는 폴더의 *.xml)을 0 으로 채워진 포트에 바인딩하고
/// 모든 노드를 한 번씩 건드린다. 벤더 XML 은 저장소에 넣지 않으므로 변수가 없으면 건너뛴다.
/// 값 오류(<see cref="GenApiException"/> — 0 값이 어느 열거 항목에도 안 맞는 등)는 집계만 하고, 그 밖의 예외는 실패다.
/// </summary>
public class VendorXmlCorpusTests
{
    private readonly ITestOutputHelper _out;

    public VendorXmlCorpusTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static IEnumerable<string> CorpusFiles()
    {
        var path = Environment.GetEnvironmentVariable("GEVSHARP_VENDOR_XML");
        if (string.IsNullOrWhiteSpace(path)) yield break;
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "*.xml")) yield return f;
        }
        else if (File.Exists(path))
        {
            yield return path;
        }
    }

    [Fact]
    public async Task VendorXml_BindsAndEveryNodeCanBeTouched()
    {
        var files = CorpusFiles().ToList();
        Assert.SkipWhen(files.Count == 0, "GEVSHARP_VENDOR_XML is not set");

        foreach (var file in files)
        {
            var sw = Stopwatch.StartNew();
            var model = GenApiXmlParser.Parse(File.ReadAllText(file));
            var parseMs = sw.ElapsedMilliseconds;
            var port = new MemoryPort();
            var map = GenApiNodeMap.Parse(model, port);
            var bindMs = sw.ElapsedMilliseconds - parseMs;

            var counts = new Dictionary<string, int>();
            var samples = new Dictionary<string, string>();
            void Count(string key, string sample)
            {
                counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
                if (!samples.ContainsKey(key)) samples[key] = sample;
            }

            foreach (var node in map.Nodes)
            {
                try
                {
                    var mode = await node.GetAccessModeAsync();
                    Count($"mode:{mode}", node.Name);
                    if (mode != AccessMode.ReadOnly && mode != AccessMode.ReadWrite) continue;
                    switch (node)
                    {
                        case IInteger i:
                            await i.GetAsync();
                            await i.GetMinAsync();
                            await i.GetMaxAsync();
                            await i.GetIncAsync();
                            break;
                        case IFloat f:
                            await f.GetAsync();
                            await f.GetMinAsync();
                            await f.GetMaxAsync();
                            await f.GetIncAsync();
                            break;
                        case IString s:
                            await s.GetAsync();
                            await s.GetMaxLengthAsync();
                            break;
                        case IBoolean b:
                            await b.GetAsync();
                            break;
                        case IEnumeration e:
                            await e.GetAvailableEntriesAsync();
                            await e.GetAsync();
                            break;
                        case ICommand c:
                            await c.IsDoneAsync();
                            break;
                        case IRegister r:
                            await r.GetAddressAsync();
                            await r.GetLengthAsync();
                            break;
                    }
                    Count("ok", node.Name);
                }
                catch (GenApiException ex)
                {
                    Count("genapi:" + Classify(ex.Message), $"{node.Name}: {ex.Message}");
                }
            }

            _out.WriteLine($"{Path.GetFileName(file)}: {model.Nodes.Count} defs, {map.Nodes.Count} nodes, {model.Warnings.Count} warnings, parse {parseMs} ms, bind {bindMs} ms, reads {port.ReadCount}");
            foreach (var kv in counts.OrderBy(k => k.Key))
                _out.WriteLine($"  {kv.Key} = {kv.Value}   e.g. {samples[kv.Key]}");
            foreach (var w in model.Warnings.Take(20)) _out.WriteLine("  warning: " + w);
        }
    }

    private static string Classify(string message)
    {
        if (message.Contains("matches no entry")) return "enum-no-match";
        if (message.Contains("cannot be read")) return "not-readable";
        if (message.Contains("zero")) return "division-by-zero";
        if (message.Contains("negative address")) return "negative-address";
        if (message.Contains("index")) return "index";
        return "other";
    }
}
