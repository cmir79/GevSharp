namespace GevSharp.Gvcp;

/// <summary>
/// 부트스트랩 레지스터 맵(GVBS) — 모든 GVCP 장치가 같은 오프셋에 두는 표준 레지스터. 전부 32비트 빅엔디언.
/// 문자열 레지스터는 NUL 종료 ASCII 로 고정 길이.
/// DISCOVERY_ACK 페이로드(248 바이트)는 이 맵의 0x0000~0x00F7 을 그대로 복사한 것이라 같은 오프셋으로 파싱한다.
/// </summary>
public static class GvbsAddr
{
    public const uint Version = 0x0000;                 // [31:16] major, [15:0] minor
    public const uint DeviceMode = 0x0004;              // bit31 big-endian, [15:0] character set (1 = UTF-8)
    public const uint MacHigh = 0x0008;                 // [15:0] MAC bytes 0-1
    public const uint MacLow = 0x000C;                  // MAC bytes 2-5
    public const uint SupportedIpCfg = 0x0010;          // bit0 persistent, bit1 DHCP, bit2 LLA
    public const uint CurrentIpCfg = 0x0014;
    public const uint CurrentIp = 0x0024;
    public const uint CurrentSubnet = 0x0034;
    public const uint CurrentGateway = 0x0044;
    public const uint ManufacturerName = 0x0048;
    public const int ManufacturerNameLen = 32;
    public const uint ModelName = 0x0068;
    public const int ModelNameLen = 32;
    public const uint DeviceVersion = 0x0088;
    public const int DeviceVersionLen = 32;
    public const uint ManufacturerInfo = 0x00A8;
    public const int ManufacturerInfoLen = 48;
    public const uint SerialNumber = 0x00D8;
    public const int SerialNumberLen = 16;
    public const uint UserDefinedName = 0x00E8;
    public const int UserDefinedNameLen = 16;
    public const int DiscoveryDataLen = 0xF8;

    public const uint FirstUrl = 0x0200;
    public const uint SecondUrl = 0x0400;
    public const int UrlLen = 512;

    public const uint NumNetworkInterfaces = 0x0600;
    public const uint PersistentIp0 = 0x064C;
    public const uint PersistentSubnet0 = 0x065C;
    public const uint PersistentGateway0 = 0x066C;
    public const uint LinkSpeed0 = 0x0670;

    public const uint NumMessageChannels = 0x0900;
    public const uint NumStreamChannels = 0x0904;
    public const uint NumActionSignals = 0x0908;
    public const uint ActionDeviceKey = 0x090C;
    public const uint NumActiveLinks = 0x0910;
    public const uint GvspCapability = 0x092C;
    public const uint MessageChannelCapability = 0x0930;
    public const uint GvcpCapability = 0x0934;
    public const uint HeartbeatTimeout = 0x0938;        // ms
    public const uint TimestampTickFreqHigh = 0x093C;
    public const uint TimestampTickFreqLow = 0x0940;
    public const uint TimestampControl = 0x0944;        // 값(LSB 기준): 2 = reset, 1 = latch
    public const uint TimestampLatchedHigh = 0x0948;
    public const uint TimestampLatchedLow = 0x094C;
    public const uint DiscoveryAckDelay = 0x0950;
    public const uint GvcpConfig = 0x0954;
    public const uint PendingTimeout = 0x0958;
    public const uint ControlSwitchoverKey = 0x095C;
    public const uint GvspConfig = 0x0960;
    public const uint PhysicalLinkCfgCapability = 0x0964;
    public const uint PhysicalLinkCfg = 0x0968;
    public const uint Ieee1588Status = 0x096C;
    public const uint ScheduledActionQueueSize = 0x0970;

    /// <summary>Control Channel Privilege. 값(LSB 기준): 1 = exclusive, 2 = control, 4 = control switchover enable.</summary>
    public const uint Ccp = 0x0A00;
    public const uint PrimaryAppPort = 0x0A04;
    public const uint PrimaryAppIp = 0x0A14;

    public const uint Mcp = 0x0B00;                     // message channel port
    public const uint Mcda = 0x0B10;                    // message channel destination address
    public const uint Mctt = 0x0B14;                    // message channel transmission timeout
    public const uint Mcrc = 0x0B18;                    // message channel retry count
    public const uint Mcsp = 0x0B1C;                    // message channel source port

    /// <summary>스트림 채널 0 블록 시작. 채널 n 은 StreamChannel0 + n * StreamChannelStride.</summary>
    public const uint StreamChannel0 = 0x0D00;
    public const uint StreamChannelStride = 0x40;
    public const uint ScpOffset = 0x00;                 // host port ([15:0]); 0 = channel closed
    public const uint ScpsOffset = 0x04;                // packet size: bit31 fire test, bit30 do-not-fragment, bit29 big-endian, [15:0] size
    public const uint ScpdOffset = 0x08;                // inter-packet delay (timestamp ticks)
    public const uint ScdaOffset = 0x18;                // destination IPv4
    public const uint ScspOffset = 0x1C;                // source port (read-only)
    public const uint SccOffset = 0x20;                 // capability
    public const uint SccfgOffset = 0x24;               // configuration (extended IDs etc.)

    public const uint ManifestTable = 0x9000;

    // ---- bit masks ----
    public const uint CcpExclusive = 0x1;
    public const uint CcpControl = 0x2;
    public const uint CcpSwitchoverEnable = 0x4;

    public const uint ScpsFireTest = 0x8000_0000;
    public const uint ScpsDoNotFragment = 0x4000_0000;
    public const uint ScpsBigEndian = 0x2000_0000;
    public const uint ScpsSizeMask = 0x0000_FFFF;

    public const uint GvcpCapConcatenation = 1u << 0;
    public const uint GvcpCapWriteMem = 1u << 1;
    public const uint GvcpCapPacketResend = 1u << 2;
    public const uint GvcpCapEvent = 1u << 3;
    public const uint GvcpCapEventData = 1u << 4;
    public const uint GvcpCapPendingAck = 1u << 5;
    public const uint GvcpCapAction = 1u << 6;
    public const uint GvcpCapPrimaryAppSwitchover = 1u << 21;
    public const uint GvcpCapExtendedStatusCodes = 1u << 22;
    public const uint GvcpCapDiscoveryAckDelayWritable = 1u << 23;
    public const uint GvcpCapDiscoveryAckDelay = 1u << 24;
    public const uint GvcpCapTestData = 1u << 25;
    public const uint GvcpCapManifestTable = 1u << 26;
    public const uint GvcpCapCcpAppSocket = 1u << 27;
    public const uint GvcpCapLinkSpeed = 1u << 28;
    public const uint GvcpCapHeartbeatDisable = 1u << 29;
    public const uint GvcpCapSerialNumber = 1u << 30;
    public const uint GvcpCapNameRegister = 1u << 31;

    public const uint IpCfgPersistent = 1u << 0;
    public const uint IpCfgDhcp = 1u << 1;
    public const uint IpCfgLla = 1u << 2;

    public const uint DeviceModeBigEndian = 1u << 31;

    public static uint StreamChannel(int index, uint offset) => StreamChannel0 + (uint)index * StreamChannelStride + offset;
}
