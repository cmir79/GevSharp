using GevSharp.Pfnc;

namespace GevSharp.Tests.Pfnc;

/// <summary>enum 값이 공개 PFNC 표와 일치하는지, 표(Desc)와 enum 이 서로 빠짐없이 맞물리는지.</summary>
public class PixelFormatTests
{
    // 공개 표 두 곳 이상에서 확인한 값 — 벤더 문서 없이도 널리 재현되는 코드들.
    public static TheoryData<string, uint> KnownCodes => new()
    {
        { "Mono8", 0x01080001 },
        { "Mono8s", 0x01080002 },
        { "Mono10", 0x01100003 },
        { "Mono10Packed", 0x010C0004 },
        { "Mono10p", 0x010A0046 },
        { "Mono12", 0x01100005 },
        { "Mono12Packed", 0x010C0006 },
        { "Mono12p", 0x010C0047 },
        { "Mono14", 0x01100025 },
        { "Mono14p", 0x010E0104 },
        { "Mono16", 0x01100007 },
        { "Mono1p", 0x01010037 },
        { "Mono2p", 0x01020038 },
        { "Mono4p", 0x01040039 },
        { "BayerGR8", 0x01080008 },
        { "BayerRG8", 0x01080009 },
        { "BayerGB8", 0x0108000A },
        { "BayerBG8", 0x0108000B },
        { "BayerGR10", 0x0110000C },
        { "BayerBG10", 0x0110000F },
        { "BayerGR12", 0x01100010 },
        { "BayerBG12", 0x01100013 },
        { "BayerGR10Packed", 0x010C0026 },
        { "BayerBG10Packed", 0x010C0029 },
        { "BayerGR12Packed", 0x010C002A },
        { "BayerBG12Packed", 0x010C002D },
        { "BayerBG10p", 0x010A0052 },
        { "BayerGB10p", 0x010A0054 },
        { "BayerGR10p", 0x010A0056 },
        { "BayerRG10p", 0x010A0058 },
        { "BayerBG12p", 0x010C0053 },
        { "BayerGB12p", 0x010C0055 },
        { "BayerGR12p", 0x010C0057 },
        { "BayerRG12p", 0x010C0059 },
        { "BayerGR16", 0x0110002E },
        { "BayerRG16", 0x0110002F },
        { "BayerGB16", 0x01100030 },
        { "BayerBG16", 0x01100031 },
        { "RGB8", 0x02180014 },
        { "BGR8", 0x02180015 },
        { "RGBa8", 0x02200016 },
        { "BGRa8", 0x02200017 },
        { "RGB10", 0x02300018 },
        { "BGR10", 0x02300019 },
        { "RGB12", 0x0230001A },
        { "BGR12", 0x0230001B },
        { "RGB16", 0x02300033 },
        { "BGR16", 0x0230004B },
        { "RGB10p32", 0x0220001D },
        { "RGB565p", 0x02100035 },
        { "YUV411_8_UYYVYY", 0x020C001E },
        { "YUV422_8_UYVY", 0x0210001F },
        { "YUV422_8", 0x02100032 },
        { "YUV8_UYV", 0x02180020 },
        { "YCbCr8_CbYCr", 0x0218003A },
        { "YCbCr8", 0x0218005B },
        { "YCbCr422_8", 0x0210003B },
        { "YCbCr411_8_CbYYCrYY", 0x020C003C },
        { "YCbCr411_8", 0x020C005A },
        { "Coord3D_ABC32f", 0x026000C0 },
        { "Confidence8", 0x010800C6 },
    };

    [Theory]
    [MemberData(nameof(KnownCodes))]
    public void EnumValueMatchesPublicTable(string name, uint code)
    {
        var f = (PixelFormat)Enum.Parse(typeof(PixelFormat), name);
        Assert.Equal(code, (uint)f);
        Assert.Equal(name, PixelFormatInfo.Name(code));
        Assert.Equal(name, PixelFormatInfo.Name(f));
    }

    [Fact]
    public void AllValuesAreUnique()
    {
        var values = Enum.GetValues(typeof(PixelFormat)).Cast<uint>().ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Fact]
    public void EveryMemberHasDescriptorWithMatchingName()
    {
        foreach (PixelFormat f in Enum.GetValues(typeof(PixelFormat)))
        {
            if (f == PixelFormat.Unknown)
            {
                Assert.False(PixelFormatInfo.IsKnown(f));
                continue;
            }
            Assert.True(PixelFormatInfo.IsKnown(f), $"{f} missing from the descriptor table");
            Assert.Equal(Enum.GetName(typeof(PixelFormat), f), PixelFormatInfo.Name(f));
            Assert.True(PixelFormatInfo.BitsPerPixel(f) > 0, $"{f} has zero bpp");
        }
    }

    [Fact]
    public void DescriptorTableHasNoStrayEntries()
    {
        var names = Enum.GetNames(typeof(PixelFormat)).ToHashSet(StringComparer.Ordinal);
        foreach (var d in PixelFormatInfo.All)
        {
            Assert.Contains(d.Name, names);
            Assert.Equal(d.Format, (PixelFormat)Enum.Parse(typeof(PixelFormat), d.Name));
        }
        Assert.Equal(names.Count - 1, PixelFormatInfo.All.Count);
    }

    [Fact]
    public void SeriesByteMatchesComponentCount()
    {
        // 계열 바이트: 단일 성분은 0x01, 다성분은 0x02 — 표의 성분 수가 코드 구조와 어긋나면 안 된다.
        foreach (var d in PixelFormatInfo.All)
        {
            var code = (uint)d.Format;
            Assert.Equal(d.ComponentCount > 1, PixelFormatInfo.IsColor(code));
            Assert.False(PixelFormatInfo.IsCustom(code));
        }
    }
}
