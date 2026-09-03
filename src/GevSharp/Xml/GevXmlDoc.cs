namespace GevSharp.Xml;

/// <summary>
/// 장치에서 가져온 GenICam XML 본문과 출처.
/// <see cref="Xml"/> 은 BOM·NUL·앞뒤 공백을 걷어낸 텍스트라 그대로 파서에 넘길 수 있다.
/// <see cref="Url"/> 은 레지스터에서 읽은 URL 원문, <see cref="FileName"/> 은 그 URL 이 가리키던 파일 이름(.zip 이었으면 .zip 그대로).
/// </summary>
public sealed record GevXmlDoc(string Xml, string Url, string FileName, string? SchemaVersion)
{
    // XML 전문이 로그·테스트 출력에 쏟아지지 않게 요약만 낸다.
    public override string ToString()
        => $"GevXmlDoc(FileName={FileName}, {Xml.Length} chars, Url={Url}, SchemaVersion={SchemaVersion ?? "-"})";
}
