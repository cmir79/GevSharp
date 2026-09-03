using GevSharp.Xml;

namespace GevSharp.Tests.Xml;

public class GevXmlUrlTests
{
    [Fact]
    public void LocalBasic()
    {
        var u = GevXmlUrl.Parse("Local:MyCam.zip;F0F00000;3E8B");

        Assert.Equal(GevXmlUrlKind.Local, u.Kind);
        Assert.Equal("MyCam.zip", u.FileName);
        Assert.Equal(0xF0F00000UL, u.Address);
        Assert.Equal(0x3E8B, u.Length);
        Assert.True(u.IsZip);
        Assert.Null(u.SchemaVersion);
        Assert.Null(u.FilePath);
        Assert.Null(u.HttpUri);
        Assert.Equal("Local:MyCam.zip;F0F00000;3E8B", u.Raw);
        Assert.Equal(u.Raw, u.ToString());
    }

    [Theory]
    [InlineData("local:cam.xml;0001c000;1a2b")]
    [InlineData("LOCAL:cam.xml;0x0001C000;0x1A2B")]
    [InlineData("Local: cam.xml ; 1C000 ; 1A2B ")]
    public void LocalToleratesSchemeCaseHexCaseAndPrefix(string raw)
    {
        var u = GevXmlUrl.Parse(raw);

        Assert.Equal(GevXmlUrlKind.Local, u.Kind);
        Assert.Equal("cam.xml", u.FileName);
        Assert.Equal(0x1C000UL, u.Address);
        Assert.Equal(0x1A2B, u.Length);
        Assert.False(u.IsZip);
    }

    [Fact]
    public void LocalWithSchemaVersion()
    {
        var u = GevXmlUrl.Parse("Local:cam.zip;10000;200?SchemaVersion=1.1.0");

        Assert.Equal(GevXmlUrlKind.Local, u.Kind);
        Assert.Equal("cam.zip", u.FileName);
        Assert.Equal(0x10000UL, u.Address);
        Assert.Equal(0x200, u.Length);
        Assert.Equal("1.1.0", u.SchemaVersion);
    }

    [Fact]
    public void SchemaVersionKeyIsCaseInsensitiveAndUnknownKeysAreIgnored()
    {
        var u = GevXmlUrl.Parse("Local:cam.zip;10000;200?foo=bar&schemaversion=1.0.0");
        Assert.Equal("1.0.0", u.SchemaVersion);
    }

    [Theory]
    [InlineData("Local:///virtual-camera.xml;10000;1234", "virtual-camera.xml")]
    [InlineData("Local://host/cam.xml;10000;1234", "cam.xml")]
    [InlineData("Local:/cam.zip;10000;1234", "cam.zip")]
    [InlineData("Local:dir/sub/cam.xml;10000;1234", "cam.xml")]
    public void LocalAuthorityOrPathPrefixLeavesOnlyTheFileName(string raw, string expectedFile)
    {
        var u = GevXmlUrl.Parse(raw);

        Assert.Equal(GevXmlUrlKind.Local, u.Kind);
        Assert.Equal(expectedFile, u.FileName);
        Assert.Equal(0x10000UL, u.Address);
        Assert.Equal(0x1234, u.Length);
    }

    [Fact]
    public void LocalQuestionMarkInsideTheFileNameIsNotAQuery()
    {
        var u = GevXmlUrl.Parse("Local:a?b.xml;1000;10");

        Assert.Equal("a?b.xml", u.FileName);
        Assert.Equal(0x1000UL, u.Address);
        Assert.Equal(0x10, u.Length);
        Assert.Null(u.SchemaVersion);
    }

    [Fact]
    public void TrailingNulsAndWhitespaceAreTrimmed()
    {
        var u = GevXmlUrl.Parse("  Local:cam.zip;10000;200\0\0\0garbage after nul  ");

        Assert.Equal("Local:cam.zip;10000;200", u.Raw);
        Assert.Equal(0x200, u.Length);
    }

    [Theory]
    [InlineData("File:///C:/cams/cam.xml", "C:/cams/cam.xml", "cam.xml")]
    [InlineData("file:///etc/cams/cam.zip", "/etc/cams/cam.zip", "cam.zip")]
    [InlineData("file:/etc/cam.xml", "/etc/cam.xml", "cam.xml")]
    [InlineData("file://localhost/etc/cam.xml", "/etc/cam.xml", "cam.xml")]
    [InlineData("FILE:C:\\cams\\cam.xml", "C:\\cams\\cam.xml", "cam.xml")]
    [InlineData("file://C:/cams/cam.xml", "C:/cams/cam.xml", "cam.xml")]
    [InlineData("file:///C:/my%20cams/cam%20one.xml", "C:/my cams/cam one.xml", "cam one.xml")]
    [InlineData("file://server/share/cam.xml", "//server/share/cam.xml", "cam.xml")]
    public void FileForms(string raw, string expectedPath, string expectedFile)
    {
        var u = GevXmlUrl.Parse(raw);

        Assert.Equal(GevXmlUrlKind.File, u.Kind);
        Assert.Equal(expectedPath, u.FilePath);
        Assert.Equal(expectedFile, u.FileName);
        Assert.Equal(0UL, u.Address);
        Assert.Equal(0, u.Length);
        Assert.Null(u.HttpUri);
    }

    [Fact]
    public void FileWithSchemaVersion()
    {
        var u = GevXmlUrl.Parse("File:///C:/cams/cam.xml?SchemaVersion=1.0.0");

        Assert.Equal("C:/cams/cam.xml", u.FilePath);
        Assert.Equal("1.0.0", u.SchemaVersion);
    }

    [Theory]
    [InlineData("file:///C:/dir?x/cam.xml", "C:/dir?x/cam.xml", "cam.xml", null)]
    [InlineData("file:///tmp/a?b/cam.xml", "/tmp/a?b/cam.xml", "cam.xml", null)]
    [InlineData("file:///tmp/what?/cam.xml?SchemaVersion=2.0.0", "/tmp/what?/cam.xml", "cam.xml", "2.0.0")]
    [InlineData("file:///tmp/cam.xml?SchemaVersion=", "/tmp/cam.xml", "cam.xml", null)]
    [InlineData("file:///tmp/cam.xml?foo=bar&SchemaVersion=1.0.0", "/tmp/cam.xml", "cam.xml", "1.0.0")]
    public void FilePathMayContainQuestionMark(string raw, string expectedPath, string expectedFile, string? expectedSchema)
    {
        var u = GevXmlUrl.Parse(raw);

        Assert.Equal(GevXmlUrlKind.File, u.Kind);
        Assert.Equal(expectedPath, u.FilePath);
        Assert.Equal(expectedFile, u.FileName);
        Assert.Equal(expectedSchema, u.SchemaVersion);
    }

    [Theory]
    [InlineData("http://192.168.0.10/cam.zip", "http://192.168.0.10/cam.zip", "cam.zip", null)]
    [InlineData("HTTPS://host.example/path/cam.xml?SchemaVersion=1.0.0", "https://host.example/path/cam.xml", "cam.xml", "1.0.0")]
    [InlineData("http://h/cam.zip?a=1&SchemaVersion=1.1.0&b=2", "http://h/cam.zip?a=1&b=2", "cam.zip", "1.1.0")]
    [InlineData("http://h/dir/cam%20one.zip", "http://h/dir/cam%20one.zip", "cam one.zip", null)]
    public void HttpForms(string raw, string expectedUri, string expectedFile, string? expectedSchema)
    {
        var u = GevXmlUrl.Parse(raw);

        Assert.Equal(GevXmlUrlKind.Http, u.Kind);
        Assert.NotNull(u.HttpUri);
        Assert.Equal(new Uri(expectedUri), u.HttpUri);
        Assert.Equal(expectedFile, u.FileName);
        Assert.Equal(expectedSchema, u.SchemaVersion);
        Assert.Null(u.FilePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0\0\0")]
    [InlineData("cam.zip")]
    [InlineData(":cam.zip")]
    [InlineData("Local:cam.zip")]
    [InlineData("Local:cam.zip;1000")]
    [InlineData("Local:cam.zip;1000;")]
    [InlineData("Local:cam.zip;ZZZZ;10")]
    [InlineData("Local:cam.zip;1000;0")]
    [InlineData("Local:;1000;10")]
    [InlineData("Local:///;1000;10")]
    [InlineData("Local:dir/;1000;10")]
    [InlineData("Local:cam.zip;1000;10;extra")]
    [InlineData("Local:cam.zip;1000;FFFFFFFFFFFFFFFFFF")]
    [InlineData("Local:cam.zip;1000;80000000")]
    [InlineData("Local:cam.zip;100000000;10")]
    [InlineData("Local:cam.zip;FFFFFFF8;10")]
    [InlineData("ftp://host/cam.zip")]
    [InlineData("http://")]
    [InlineData("http://host/")]
    [InlineData("file:")]
    [InlineData("file:///")]
    [InlineData("nonsense with spaces")]
    public void GarbageThrowsGevExceptionCarryingTheRawString(string raw)
    {
        var ex = Assert.Throws<GevException>(() => GevXmlUrl.Parse(raw));

        Assert.Contains("Malformed camera XML URL", ex.Message);
        var visible = raw.Replace("\0", "\\0").Trim();
        Assert.Contains(visible, ex.Message);
    }

    [Fact]
    public void LocalMayEndExactlyAtTheTopOfTheAddressSpace()
    {
        var u = GevXmlUrl.Parse("Local:cam.zip;FFFFFFF0;10");

        Assert.Equal(0xFFFFFFF0UL, u.Address);
        Assert.Equal(0x10, u.Length);
    }

    [Fact]
    public void NullThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => GevXmlUrl.Parse(null!));
    }

    [Fact]
    public void TryParseReportsSuccessAndFailure()
    {
        Assert.True(GevXmlUrl.TryParse("Local:cam.zip;1000;10", out var ok));
        Assert.NotNull(ok);
        Assert.Equal("cam.zip", ok!.FileName);

        Assert.False(GevXmlUrl.TryParse("Local:cam.zip", out var bad));
        Assert.Null(bad);

        Assert.False(GevXmlUrl.TryParse(null, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void LoaderParseUrlDelegates()
    {
        var u = GevXmlLoader.ParseUrl("Local:cam.zip;1000;10");
        Assert.Equal(GevXmlUrl.Parse("Local:cam.zip;1000;10"), u);
    }
}
