namespace GevSharp;

/// <summary>라이브러리 공통 예외 기반. 메시지는 영어(로그로 흘러든다).</summary>
public class GevException : Exception
{
    public GevException(string message) : base(message) { }
    public GevException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>GVCP 요청이 재시도까지 전부 응답 없이 끝났다.</summary>
public sealed class GevTimeoutException : GevException
{
    public GevTimeoutException(string message) : base(message) { }
}

/// <summary>장치가 오류 상태 코드로 응답했다(GVCP ACK status ≠ SUCCESS).</summary>
public sealed class GevStatusException : GevException
{
    public ushort Status { get; }
    public string Operation { get; }

    /// <summary>WRITEREG/WRITEMEM 이 거절된 항목 번호(호출자 목록 기준). 그 앞 항목은 적용됐고 그 항목부터는 아니다. 없으면 null.</summary>
    public int? FailedIndex { get; internal set; }

    public GevStatusException(string operation, ushort status, int? failedIndex = null)
        : base(failedIndex is null
            ? $"{operation} failed with device status 0x{status:X4} ({Gvcp.GvcpConst.StatusName(status)})"
            : $"{operation} failed with device status 0x{status:X4} ({Gvcp.GvcpConst.StatusName(status)}) at entry {failedIndex}")
    {
        Operation = operation;
        Status = status;
        FailedIndex = failedIndex;
    }
}

/// <summary>제어권(CCP)을 잃었다 — 하트비트 실패 또는 다른 애플리케이션의 인수.</summary>
public sealed class GevControlLostException : GevException
{
    public GevControlLostException(string message) : base(message) { }
}

/// <summary>GenICam XML 이나 GenApi 노드 해석 실패.</summary>
public sealed class GenApiException : GevException
{
    /// <summary>
    /// 값이 프레임의 청크 데이터에 있어 장치에서 읽을 수 없을 때 <see cref="Exception.Data"/> 에 붙는 표식(값 true).
    /// 노드가 없거나 장치가 거절한 것과 구분해야 하는 자리가 있어 형이 아니라 표식으로 남긴다.
    /// </summary>
    public const string ChunkDataKey = "ChunkData";

    /// <summary>
    /// 값이 증분 격자에 어긋나 거절됐을 때 <see cref="Exception.Data"/> 에 붙는 기준점과 간격(둘 다 long).
    /// 격자를 아는 쪽은 이 노드뿐이고, 부르는 쪽은 대개 변환 노드를 통해 들어와 간격을 볼 수 없다 —
    /// 사람에게 값을 받아 맞춰 주려면 그 두 숫자가 메시지가 아니라 값으로 있어야 한다.
    /// </summary>
    public const string GridAnchorKey = "GridAnchor";

    public const string GridIncrementKey = "GridIncrement";

    public string? NodeName { get; }

    public GenApiException(string message, string? nodeName = null, Exception? inner = null) : base(message, inner)
    {
        NodeName = nodeName;
    }
}

/// <summary>GVSP 스트림이 닫혔거나 시작되지 않은 상태에서 수신을 요청했다.</summary>
public sealed class GevStreamClosedException : GevException
{
    public GevStreamClosedException(string message) : base(message) { }
}
