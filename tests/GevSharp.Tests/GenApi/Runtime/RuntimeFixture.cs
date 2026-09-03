using GevSharp.GenApi;
using GevSharp.Tests.GenApi.Model;

namespace GevSharp.Tests.GenApi.Runtime;

/// <summary>손으로 쓴 XML 조각을 노드맵으로 바인딩한다. 루트 카테고리와 Device 포트는 자동으로 붙는다.</summary>
internal static class RuntimeFixture
{
    /// <summary>body 의 노드들을 담은 노드맵. Root 카테고리는 features 로 준 이름들(없으면 빈 카테고리)을 가리킨다.</summary>
    public static GenApiNodeMap Bind(string body, MemoryPort port, params string[] features)
        => GenApiNodeMap.Parse(Xml(body, features), port);

    public static string Xml(string body, params string[] features)
    {
        var pf = "";
        foreach (var f in features) pf += $"<pFeature>{f}</pFeature>";
        return GenApiFixtures.Wrap($"<Category Name=\"Root\">{pf}</Category>\n{GenApiFixtures.DevicePort}\n{body}");
    }

    /// <summary>BigEndian 4 바이트 IntReg 조각.</summary>
    public static string IntReg(string name, string address, string extra = "", string access = "RW", int length = 4)
        => $"<IntReg Name=\"{name}\"><Address>{address}</Address><Length>{length}</Length><AccessMode>{access}</AccessMode><pPort>Device</pPort>{extra}<Endianess>BigEndian</Endianess></IntReg>";

    /// <summary>레지스터를 가리키는 Integer 조각.</summary>
    public static string Integer(string name, string pValue, string extra = "")
        => $"<Integer Name=\"{name}\"><pValue>{pValue}</pValue>{extra}</Integer>";

    public static string MaskedIntReg(string name, string address, string bits, string endianess = "BigEndian", string extra = "", string access = "RW")
        => $"<MaskedIntReg Name=\"{name}\"><Address>{address}</Address><Length>4</Length><AccessMode>{access}</AccessMode><pPort>Device</pPort>{extra}{bits}<Endianess>{endianess}</Endianess></MaskedIntReg>";
}
