using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using GevSharp.Gvcp;

namespace GevSharp;

/// <summary>
/// 장치 식별 정보 — DISCOVERY_ACK 페이로드(= 부트스트랩 0x0000..0x00F7) 를 GVBS 오프셋으로 읽은 결과.
/// 열린 장치에서는 같은 필드들을 각자의 주소에서 다시 읽어 채운다(<see cref="ReadFromDeviceAsync"/>).
/// </summary>
public sealed record GevDeviceInfo
{
    private const string LogSrc = "GevDeviceInfo";

    public required PhysicalAddress Mac { get; init; }
    public required IPAddress Address { get; init; }
    public required IPAddress Subnet { get; init; }
    public required IPAddress Gateway { get; init; }
    /// <summary>이 응답을 들은 호스트 인터페이스 주소.</summary>
    public required IPAddress InterfaceAddress { get; init; }
    public required int SpecMajor { get; init; }
    public required int SpecMinor { get; init; }
    public required uint DeviceMode { get; init; }
    public required uint SupportedIpCfg { get; init; }
    public required uint CurrentIpCfg { get; init; }
    public required string Manufacturer { get; init; }
    public required string Model { get; init; }
    public required string DeviceVersion { get; init; }
    public required string ManufacturerInfo { get; init; }
    public required string SerialNumber { get; init; }
    public required string UserDefinedName { get; init; }

    /// <summary>장치 모드 하위 16비트의 문자 집합 코드 — UTF-8.</summary>
    public const int CharacterSetUtf8 = 1;
    /// <summary>장치 모드 하위 16비트의 문자 집합 코드 — ASCII.</summary>
    public const int CharacterSetAscii = 2;

    public bool IsBigEndianDevice => (DeviceMode & GvbsAddr.DeviceModeBigEndian) != 0;

    /// <summary>장치 모드 하위 16비트의 문자 집합 코드(<see cref="CharacterSetUtf8"/>, <see cref="CharacterSetAscii"/>). 문자열 레지스터 해석에 쓴다.</summary>
    public int CharacterSet => (int)(DeviceMode & 0xFFFF);

    /// <summary>같은 서브넷의 인터페이스로 들은 응답인지 — 여러 인터페이스에서 같은 장치가 보일 때 이쪽을 우선한다.</summary>
    public bool IsReachableDirectly => GevNet.IsSameSubnet(Address, InterfaceAddress, Subnet);

    /// <summary>DISCOVERY_ACK 페이로드 248바이트를 GVBS 오프셋으로 읽는다. 짧으면 <see cref="GevException"/> — 잘린 응답으로 유령 장치를 만들지 않는다.</summary>
    public static GevDeviceInfo ParseDiscoveryAck(ReadOnlySpan<byte> payload, IPAddress interfaceAddress)
    {
        if (interfaceAddress is null) throw new ArgumentNullException(nameof(interfaceAddress));
        if (payload.Length < GvbsAddr.DiscoveryDataLen)
            throw new GevException($"DISCOVERY_ACK payload too short: {payload.Length} bytes (expected {GvbsAddr.DiscoveryDataLen})");

        var version = ReadU32(payload, GvbsAddr.Version);
        var deviceMode = ReadU32(payload, GvbsAddr.DeviceMode);
        var charset = (int)(deviceMode & 0xFFFF);
        var mac = new byte[6];
        payload.Slice((int)GvbsAddr.MacHigh + 2, 2).CopyTo(mac);
        payload.Slice((int)GvbsAddr.MacLow, 4).CopyTo(mac.AsSpan(2));

        return new GevDeviceInfo
        {
            Mac = new PhysicalAddress(mac),
            Address = GvcpPacket.Ipv4FromBytes(payload.Slice((int)GvbsAddr.CurrentIp, 4)),
            Subnet = GvcpPacket.Ipv4FromBytes(payload.Slice((int)GvbsAddr.CurrentSubnet, 4)),
            Gateway = GvcpPacket.Ipv4FromBytes(payload.Slice((int)GvbsAddr.CurrentGateway, 4)),
            InterfaceAddress = interfaceAddress,
            SpecMajor = (int)(version >> 16),
            SpecMinor = (int)(version & 0xFFFF),
            DeviceMode = deviceMode,
            SupportedIpCfg = ReadU32(payload, GvbsAddr.SupportedIpCfg),
            CurrentIpCfg = ReadU32(payload, GvbsAddr.CurrentIpCfg),
            Manufacturer = DecodeNulString(payload.Slice((int)GvbsAddr.ManufacturerName, GvbsAddr.ManufacturerNameLen), charset),
            Model = DecodeNulString(payload.Slice((int)GvbsAddr.ModelName, GvbsAddr.ModelNameLen), charset),
            DeviceVersion = DecodeNulString(payload.Slice((int)GvbsAddr.DeviceVersion, GvbsAddr.DeviceVersionLen), charset),
            ManufacturerInfo = DecodeNulString(payload.Slice((int)GvbsAddr.ManufacturerInfo, GvbsAddr.ManufacturerInfoLen), charset),
            SerialNumber = DecodeNulString(payload.Slice((int)GvbsAddr.SerialNumber, GvbsAddr.SerialNumberLen), charset),
            UserDefinedName = DecodeNulString(payload.Slice((int)GvbsAddr.UserDefinedName, GvbsAddr.UserDefinedNameLen), charset),
        };
    }

    /// <summary>식별 블록의 32비트 레지스터 — 각각 READREG 로 읽는다. 전부 필수 레지스터다.</summary>
    private static readonly uint[] RegisterFields =
    {
        GvbsAddr.Version, GvbsAddr.DeviceMode, GvbsAddr.MacHigh, GvbsAddr.MacLow, GvbsAddr.SupportedIpCfg, GvbsAddr.CurrentIpCfg,
        GvbsAddr.CurrentIp, GvbsAddr.CurrentSubnet, GvbsAddr.CurrentGateway,
    };

    /// <summary>
    /// 식별 블록의 문자열 레지스터 — 각각 그 주소에서 READMEM 한다. 일련번호·사용자 이름은 GVCP 능력 비트로 지원 여부가 갈리는 선택 레지스터라
    /// 장치가 거절하면 빈 문자열로 둔다(그 비트를 읽기 전이므로 응답으로 판단한다).
    /// </summary>
    private static readonly (uint Addr, int Len, bool IsOptional)[] StringFields =
    {
        (GvbsAddr.ManufacturerName, GvbsAddr.ManufacturerNameLen, false),
        (GvbsAddr.ModelName, GvbsAddr.ModelNameLen, false),
        (GvbsAddr.DeviceVersion, GvbsAddr.DeviceVersionLen, false),
        (GvbsAddr.ManufacturerInfo, GvbsAddr.ManufacturerInfoLen, false),
        (GvbsAddr.SerialNumber, GvbsAddr.SerialNumberLen, true),
        (GvbsAddr.UserDefinedName, GvbsAddr.UserDefinedNameLen, true),
    };

    /// <summary>
    /// 열린 채널로 식별 블록을 필드별로 읽어 248바이트 이미지를 조립한 뒤 <see cref="ParseDiscoveryAck"/> 와 같은 방식으로 해석한다.
    /// 블록 전체를 READMEM 한 번으로 읽지 않는다 — 예약 워드(0x0018..0x0023 등)를 구현하지 않은 장치는 벌크 응답에서 그 워드를 빼고
    /// 뒤를 당겨 채우므로 0x0024 이후의 모든 필드(주소·문자열)가 몇 바이트씩 밀려 보인다(실장치에서 확인). 주소를 직접 지정한 READREG 와
    /// 문자열 자리에서 시작하는 READMEM 은 그런 장치에서도 정확하다.
    /// </summary>
    public static async Task<GevDeviceInfo> ReadFromDeviceAsync(GvcpChannel channel, IPAddress interfaceAddress, CancellationToken ct = default)
    {
        if (channel is null) throw new ArgumentNullException(nameof(channel));
        if (interfaceAddress is null) throw new ArgumentNullException(nameof(interfaceAddress));

        var image = new byte[GvbsAddr.DiscoveryDataLen];
        foreach (var addr in RegisterFields)
        {
            var ack = await channel.RequestAsync(GvcpCmd.ReadReg(addr), ct).ConfigureAwait(false);
            if (ack.RegCount < 1)
                throw new GevException($"READREG_ACK for bootstrap register 0x{addr:X4} carries no value");
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan((int)addr), ack.GetRegValue(0));
        }
        foreach (var field in StringFields)
        {
            await ReadStringFieldAsync(channel, field.Addr, field.Len, field.IsOptional, image, ct).ConfigureAwait(false);
        }
        return ParseDiscoveryAck(image, interfaceAddress);
    }

    private static async Task ReadStringFieldAsync(GvcpChannel channel, uint addr, int length, bool isOptional, byte[] image, CancellationToken ct)
    {
        GvcpAck ack;
        try
        {
            ack = await channel.RequestAsync(GvcpCmd.ReadMem(addr, length), ct).ConfigureAwait(false);
        }
        catch (GevStatusException ex) when (isOptional)
        {
            if (GevLog.IsEnabled(GevLogLevel.Debug))
                GevLog.Debug(LogSrc, $"bootstrap string register 0x{addr:X4} is not readable ({GvcpConst.StatusName(ex.Status)}); leaving it empty");
            return;
        }
        if (ack.MemAddress != addr)
            throw new GevException($"READMEM_ACK for bootstrap register 0x{addr:X4} came back with address 0x{ack.MemAddress:X8}");
        var data = ack.MemData;
        // 짧은 응답은 오류(없는 데이터), 긴 응답은 앞에서 요청한 만큼만 쓴다 — 메모리 읽기 경로와 같은 규칙이다.
        if (data.Length < length)
            throw new GevException($"READMEM_ACK for bootstrap register 0x{addr:X4} returned {data.Length} byte(s), expected {length}");
        if (data.Length > length && GevLog.IsEnabled(GevLogLevel.Debug))
            GevLog.Debug(LogSrc, $"READMEM_ACK for bootstrap register 0x{addr:X4} carries {data.Length} byte(s) for a {length}-byte request; using the first {length}");
        data.Slice(0, length).CopyTo(image.AsSpan((int)addr));
    }

    /// <summary>
    /// NUL 종료 고정 길이 문자열을 장치 문자 집합으로 읽는다 — <see cref="CharacterSetAscii"/> 면 ASCII, 그 밖(UTF-8·미지정)은 UTF-8(ASCII 는 부분집합).
    /// 첫 NUL 앞까지를 그대로 돌려준다 — 공백을 다듬지 않으므로 레지스터에 쓴 값과 그대로 대조된다. 깨진 바이트는 대체 문자로 남지 예외를 내지 않는다.
    /// </summary>
    internal static string DecodeNulString(ReadOnlySpan<byte> bytes, int characterSet)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0) end = bytes.Length;
        if (end == 0) return string.Empty;
        var encoding = characterSet == CharacterSetAscii ? Encoding.ASCII : Encoding.UTF8;
        return encoding.GetString(bytes.Slice(0, end).ToArray());
    }

    private static uint ReadU32(ReadOnlySpan<byte> payload, uint offset) => BinaryPrimitives.ReadUInt32BigEndian(payload.Slice((int)offset));

    public override string ToString()
        => $"{Manufacturer} {Model} [{SerialNumber}] {Address} ({Mac}) via {InterfaceAddress}";
}
