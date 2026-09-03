using System.Reflection;
using System.Text;
using GevSharp.GenApi.Model;

namespace GevSharp.Tests.GenApi.Model;

/// <summary>임베디드 자작 XML 픽스처(tests/Fixtures/GenApi/*.xml)를 이름으로 읽는다. 리소스 이름의 구분 문자는 빌드 환경마다 달라 접미사로 맞춘다.</summary>
internal static class GenApiFixtures
{
    private static readonly Lazy<GenApiXmlModel> _minimal = new(() => GenApiXmlParser.Parse(Load("minimal.xml")));
    private static readonly Lazy<GenApiXmlModel> _groups = new(() => GenApiXmlParser.Parse(Load("groups.xml")));

    /// <summary>한 번만 파싱해 모든 테스트가 공유한다(모델은 불변).</summary>
    public static GenApiXmlModel Minimal => _minimal.Value;
    public static GenApiXmlModel Groups => _groups.Value;

    public static string Load(string fileName)
    {
        var asm = typeof(GenApiFixtures).Assembly;
        string? match = null;
        foreach (var n in asm.GetManifestResourceNames())
        {
            if (!n.Contains("GenApi")) continue;
            if (n.EndsWith("\\" + fileName, StringComparison.Ordinal)
                || n.EndsWith("/" + fileName, StringComparison.Ordinal)
                || n.EndsWith("." + fileName, StringComparison.Ordinal))
            {
                match = n;
                break;
            }
        }
        if (match is null)
            throw new FileNotFoundException($"Embedded GenApi fixture '{fileName}' not found. Available: {string.Join(", ", asm.GetManifestResourceNames())}");

        using var s = asm.GetManifestResourceStream(match)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>필수 루트 속성을 전부 갖춘 RegisterDescription 으로 감싼다 — 인라인 오류/경고 케이스용.</summary>
    public static string Wrap(string body, string xmlns = "http://www.genicam.org/GenApi/Version_1_1", string extraRootAttrs = "")
    {
        var ns = xmlns.Length == 0 ? "" : $" xmlns=\"{xmlns}\"";
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<RegisterDescription ModelName=\"Inline\" VendorName=\"GevSharp\" SchemaMajorVersion=\"1\" SchemaMinorVersion=\"1\" "
            + "MajorVersion=\"1\" MinorVersion=\"0\" SubMinorVersion=\"0\" " + extraRootAttrs + ns + ">\n"
            + body
            + "\n</RegisterDescription>";
    }

    /// <summary>주소 하나짜리 IntReg 조각.</summary>
    public static string IntReg(string name, string address = "0x1000", string extra = "")
        => $"<IntReg Name=\"{name}\"><Address>{address}</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort>{extra}</IntReg>";

    public const string DevicePort = "<Port Name=\"Device\"/>";
}
