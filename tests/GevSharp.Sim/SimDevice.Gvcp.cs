using System.Net;
using System.Net.Sockets;
using GevSharp.Gvcp;

namespace GevSharp.Sim;

/// <summary>
/// GVCP 응답기. 한 스레드가 소켓을 20 ms 단위로 폴링하며 명령을 하나씩 처리한다(실제 장치도 한 번에 하나만 처리한다).
/// 유휴 중에도 폴링 주기마다 하트비트 만료를 검사한다.
/// 접근 제어 요약:
///  - 읽기(READREG/READMEM/DISCOVERY)는 누구나.
///  - 쓰기는 CCP 가 0 이면 누구나, 아니면 제어권 보유자(CCP 를 쓴 엔드포인트)만 — 그 외는 ACCESS_DENIED.
///  - 보유자의 모든 명령이 하트비트 타이머를 되돌린다. 타이머가 HeartbeatTimeout 레지스터를 넘기면 CCP 를 비운다.
///  - PACKETRESEND 는 보유자에게서 온 것만 처리하고 응답은 없다.
/// </summary>
public sealed partial class SimDevice
{
    /// <summary><see cref="SimDeviceOpt.HasReservedWordHoles"/> 가 구현하지 않은 것으로 취급하는 부트스트랩 예약 워드.</summary>
    public static readonly uint[] ReservedWordHoles = { 0x0020, 0x0040 };

    /// <summary>이 워드가 구현되지 않은 것으로 취급되는지 — 옵션이 켜져 있고 <see cref="ReservedWordHoles"/> 에 든 주소.</summary>
    private bool IsReservedWordHole(uint addr)
    {
        if (!Opt.HasReservedWordHoles) return false;
        foreach (var hole in ReservedWordHoles)
        {
            if (hole == addr) return true;
        }
        return false;
    }

    private void GvcpLoop()
    {
        var sock = _gvcpSocket!;
        var buf = new byte[65536];   // 데이터그램 최대 길이 — 어떤 합법 UDP 페이로드도 잘리거나 MessageSize 로 튕기지 않는다
        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (!_isStopping)
        {
            try
            {
                if (!sock.Poll(20_000, SelectMode.SelectRead))
                {
                    CheckHeartbeat();
                    continue;
                }

                int n = sock.ReceiveFrom(buf, ref ep);
                var src = (IPEndPoint)ep;
                var sender = new IPEndPoint(src.Address, src.Port);
                CheckHeartbeat();
                long handleStartNs = NowNs;
                HandleGvcp(buf, n, sender);
                ObserveCommandHandleTime(NowNs - handleStartNs);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
            {
                // 수신 버퍼보다 큰 데이터그램 — 플랫폼에 따라 예외(Windows)이거나 조용한 잘림(Linux)이라 여기서 한 갈래(malformed)로 모은다
                Malformed("datagram larger than the receive buffer");
            }
            catch (SocketException ex)
            {
                if (_isStopping) break;
                SetError("GVCP socket error: " + ex.SocketErrorCode);
            }
            catch (Exception ex)
            {
                if (_isStopping) break;
                SetError("GVCP handler failure: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 명령 하나를 처리하는 데 걸린 시간을 최대값으로 남긴다. 응답기 스레드 혼자 쓰므로 잠금 없이 읽고 쓴다.
    /// </summary>
    private void ObserveCommandHandleTime(long elapsedNs)
    {
        int ms = (int)(elapsedNs / 1_000_000);
        if (ms > Volatile.Read(ref _maxCommandHandleMs)) Volatile.Write(ref _maxCommandHandleMs, ms);
    }

    private void HandleGvcp(byte[] buf, int n, IPEndPoint sender)
    {
        if (n < GvcpConst.HeaderSize)
        {
            Malformed($"datagram of {n} bytes is shorter than the 8-byte header");
            return;
        }
        if (buf[0] != GvcpConst.PacketTypeCmd)
        {
            Malformed($"unexpected packet type 0x{buf[0]:X2}");
            return;
        }

        byte flags = buf[1];
        ushort cmd = SimWire.ReadU16(buf, 2);
        int len = SimWire.ReadU16(buf, 4);
        ushort reqId = SimWire.ReadU16(buf, 6);
        if (GvcpConst.HeaderSize + len > n)
        {
            Malformed($"payload length {len} exceeds the datagram ({n} bytes)");
            return;
        }

        bool ackRequired = (flags & GvcpConst.FlagAckRequired) != 0;
        var payload = new byte[len];
        Buffer.BlockCopy(buf, GvcpConst.HeaderSize, payload, 0, len);

        bool isOwner = IsOwner(sender);
        if (isOwner) TouchHeartbeat();

        switch (cmd)
        {
            case GvcpConst.DiscoveryCmd:
                Interlocked.Increment(ref _discoveryCount);
                if (ackRequired) SendAck(sender, GvcpConst.StatusSuccess, GvcpConst.DiscoveryAck, Registers.ReadBytes(0, GvbsAddr.DiscoveryDataLen), reqId);
                break;

            case GvcpConst.ForceIpCmd:
                HandleForceIp(payload, sender, ackRequired, reqId);
                break;

            case GvcpConst.ReadRegCmd:
                HandleReadReg(payload, sender, isOwner, ackRequired, reqId);
                break;

            case GvcpConst.WriteRegCmd:
                HandleWriteReg(payload, sender, ackRequired, reqId);
                break;

            case GvcpConst.ReadMemCmd:
                HandleReadMem(payload, sender, ackRequired, reqId);
                break;

            case GvcpConst.WriteMemCmd:
                HandleWriteMem(payload, sender, ackRequired, reqId);
                break;

            case GvcpConst.PacketResendCmd:
                HandlePacketResend(payload, flags, sender, isOwner);
                break;

            default:
                SetError($"unsupported GVCP command 0x{cmd:X4}");
                if (ackRequired) SendAck(sender, GvcpConst.StatusNotImplemented, (ushort)(cmd + 1), Array.Empty<byte>(), reqId);
                break;
        }
    }

    // ---- 개별 명령 ----

    private void HandleForceIp(byte[] payload, IPEndPoint sender, bool ackRequired, ushort reqId)
    {
        // u16 reserved, u16 MAC-high, u32 MAC-low, 12 reserved, u32 IP, 12 reserved, u32 subnet, 12 reserved, u32 gateway
        if (payload.Length < 56)
        {
            Malformed($"FORCEIP payload of {payload.Length} bytes (expected 56)");
            return;
        }
        Interlocked.Increment(ref _forceIpCount);
        ushort macHigh = SimWire.ReadU16(payload, 2);
        uint macLow = SimWire.ReadU32(payload, 4);
        bool isMine = macHigh == (ushort)(_mac[0] << 8 | _mac[1])
                   && macLow == (uint)(_mac[2] << 24 | _mac[3] << 16 | _mac[4] << 8 | _mac[5]);
        if (!isMine) return;   // 다른 장치를 향한 브로드캐스트 — 조용히 무시

        // 소켓은 이미 묶여 있어 주소를 실제로 바꾸지는 않는다. 영속 IP 레지스터에만 반영한다.
        Registers.WriteU32(GvbsAddr.PersistentIp0, SimWire.ReadU32(payload, 20));
        Registers.WriteU32(GvbsAddr.PersistentSubnet0, SimWire.ReadU32(payload, 36));
        Registers.WriteU32(GvbsAddr.PersistentGateway0, SimWire.ReadU32(payload, 52));
        if (ackRequired) SendAck(sender, GvcpConst.StatusSuccess, GvcpConst.ForceIpAck, Array.Empty<byte>(), reqId);
    }

    private void HandleReadReg(byte[] payload, IPEndPoint sender, bool isOwner, bool ackRequired, ushort reqId)
    {
        Interlocked.Increment(ref _readRegCount);
        if (payload.Length == 0 || payload.Length % 4 != 0 || payload.Length / 4 > GvcpConst.MaxRegsPerPacket)
        {
            SetError($"READREG with payload length {payload.Length}");
            if (ackRequired) SendAck(sender, GvcpConst.StatusInvalidParameter, GvcpConst.ReadRegAck, Array.Empty<byte>(), reqId);
            return;
        }

        int count = payload.Length / 4;
        var values = new byte[payload.Length];
        ushort status = GvcpConst.StatusSuccess;
        int ok = 0;
        for (int i = 0; i < count; i++)
        {
            uint addr = SimWire.ReadU32(payload, i * 4);
            if (addr % 4 != 0) { status = GvcpConst.StatusBadAlignment; break; }
            if (!Registers.Contains(addr, 4) || IsReservedWordHole(addr)) { status = GvcpConst.StatusInvalidAddress; break; }
            SimWire.WriteU32(values, i * 4, Registers.ReadU32(addr));
            ok++;
            if (addr == GvbsAddr.Ccp && isOwner) Interlocked.Increment(ref _heartbeatObserved);
        }
        if (status != GvcpConst.StatusSuccess) SetError($"READREG failed with {GvcpConst.StatusName(status)} at register {ok}");

        if (!ackRequired) return;
        var reply = new byte[ok * 4];
        Buffer.BlockCopy(values, 0, reply, 0, reply.Length);
        SendAck(sender, status, GvcpConst.ReadRegAck, reply, reqId);
    }

    private void HandleWriteReg(byte[] payload, IPEndPoint sender, bool ackRequired, ushort reqId)
    {
        Interlocked.Increment(ref _writeRegCount);
        if (payload.Length == 0 || payload.Length % 8 != 0 || payload.Length / 8 > GvcpConst.MaxRegsPerPacket)
        {
            SetError($"WRITEREG with payload length {payload.Length}");
            if (ackRequired) SendAck(sender, GvcpConst.StatusInvalidParameter, GvcpConst.WriteRegAck, IndexPayload(0), reqId);
            return;
        }
        if (!HasWriteAccess(sender))
        {
            SetError($"WRITEREG from {sender} denied: device is controlled by {ControlOwner}");
            if (ackRequired) SendAck(sender, GvcpConst.StatusAccessDenied, GvcpConst.WriteRegAck, IndexPayload(0), reqId);
            return;
        }

        int count = payload.Length / 8;
        ushort status = GvcpConst.StatusSuccess;
        int index = count;
        for (int i = 0; i < count; i++)
        {
            uint addr = SimWire.ReadU32(payload, i * 8);
            uint value = SimWire.ReadU32(payload, i * 8 + 4);
            status = WriteRegister(addr, value, sender);
            if (status != GvcpConst.StatusSuccess)
            {
                index = i;
                SetError($"WRITEREG 0x{addr:X8} failed with {GvcpConst.StatusName(status)}");
                break;
            }
        }

        if (!ackRequired) return;
        if (Opt.SupportPendingAck)
        {
            // 장치가 처리에 시간이 걸린다고 알린 뒤 그만큼 기다렸다가 실제 ACK — 호스트의 PENDING_ACK 대기 연장 경로를 검증한다.
            int delayMs = Math.Max(0, Opt.PendingAckDelayMs);
            var pending = new byte[4];
            SimWire.WriteU16(pending, 2, (ushort)Math.Min(delayMs, ushort.MaxValue));
            SendAck(sender, GvcpConst.StatusSuccess, GvcpConst.PendingAck, pending, reqId);
            if (delayMs > 0) Thread.Sleep(delayMs);
        }
        SendAck(sender, status, GvcpConst.WriteRegAck, IndexPayload(index), reqId);
    }

    private void HandleReadMem(byte[] payload, IPEndPoint sender, bool ackRequired, ushort reqId)
    {
        Interlocked.Increment(ref _readMemCount);
        if (payload.Length != 8)
        {
            SetError($"READMEM with payload length {payload.Length}");
            if (ackRequired) SendAck(sender, GvcpConst.StatusInvalidParameter, GvcpConst.ReadMemAck, Array.Empty<byte>(), reqId);
            return;
        }
        uint addr = SimWire.ReadU32(payload, 0);
        int count = SimWire.ReadU16(payload, 6);

        ushort status = GvcpConst.StatusSuccess;
        if (addr % 4 != 0) status = GvcpConst.StatusBadAlignment;
        else if (count == 0 || count % 4 != 0 || count > GvcpConst.MaxMemPayload) status = GvcpConst.StatusInvalidParameter;
        else if (!Registers.Contains(addr, count) || IsReservedWordHole(addr)) status = GvcpConst.StatusInvalidAddress;

        if (status != GvcpConst.StatusSuccess)
        {
            SetError($"READMEM 0x{addr:X8} ({count} bytes) failed with {GvcpConst.StatusName(status)}");
            if (ackRequired)
            {
                var head = new byte[4];
                SimWire.WriteU32(head, 0, addr);
                SendAck(sender, status, GvcpConst.ReadMemAck, head, reqId);
            }
            return;
        }

        if (!ackRequired) return;
        var reply = new byte[4 + count];
        SimWire.WriteU32(reply, 0, addr);
        if (Opt.HasReservedWordHoles) ReadBytesSkippingHoles(addr, reply.AsSpan(4, count));
        else Registers.ReadBytes(addr, reply.AsSpan(4, count));
        SendAck(sender, GvcpConst.StatusSuccess, GvcpConst.ReadMemAck, reply, reqId);
    }

    /// <summary>
    /// 구현하지 않은 워드를 건너뛰며 워드 단위로 읽어 dst 를 채운다 — 건너뛴 만큼 뒤의 워드가 당겨져 오고, 요청 범위 끝을 지나서라도 길이를 채운다.
    /// 영역 끝에 닿으면 나머지는 0 으로 남긴다.
    /// </summary>
    private void ReadBytesSkippingHoles(uint addr, Span<byte> dst)
    {
        int filled = 0;
        uint cursor = addr;
        while (filled < dst.Length)
        {
            if (!IsReservedWordHole(cursor))
            {
                if (!Registers.Contains(cursor, 4)) break;
                int n = Math.Min(4, dst.Length - filled);
                Registers.ReadBytes(cursor, dst.Slice(filled, n));
                filled += n;
            }
            cursor += 4;
        }
    }

    private void HandleWriteMem(byte[] payload, IPEndPoint sender, bool ackRequired, ushort reqId)
    {
        Interlocked.Increment(ref _writeMemCount);
        int dataLen = payload.Length - 4;
        if (dataLen <= 0 || dataLen % 4 != 0 || dataLen > GvcpConst.MaxMemPayload)
        {
            SetError($"WRITEMEM with payload length {payload.Length}");
            if (ackRequired) SendAck(sender, GvcpConst.StatusInvalidParameter, GvcpConst.WriteMemAck, IndexPayload(0), reqId);
            return;
        }
        if (!HasWriteAccess(sender))
        {
            SetError($"WRITEMEM from {sender} denied: device is controlled by {ControlOwner}");
            if (ackRequired) SendAck(sender, GvcpConst.StatusAccessDenied, GvcpConst.WriteMemAck, IndexPayload(0), reqId);
            return;
        }

        uint addr = SimWire.ReadU32(payload, 0);
        ushort status = GvcpConst.StatusSuccess;
        if (addr % 4 != 0) status = GvcpConst.StatusBadAlignment;
        else if (!Registers.Contains(addr, dataLen)) status = GvcpConst.StatusInvalidAddress;
        else if (Registers.IsReadOnly(addr, dataLen)) status = GvcpConst.StatusWriteProtect;

        int written = 0;
        if (status == GvcpConst.StatusSuccess)
        {
            // 워드 단위로 레지스터 쓰기와 같은 경로를 태워 부수효과(CCP·SCPS·명령 비트)를 일관되게 적용한다
            for (int off = 0; off < dataLen; off += 4)
            {
                status = WriteRegister(addr + (uint)off, SimWire.ReadU32(payload, 4 + off), sender);
                if (status != GvcpConst.StatusSuccess) break;
                written += 4;
            }
        }
        if (status != GvcpConst.StatusSuccess) SetError($"WRITEMEM 0x{addr:X8} ({dataLen} bytes) failed with {GvcpConst.StatusName(status)} after {written} bytes");

        if (ackRequired) SendAck(sender, status, GvcpConst.WriteMemAck, IndexPayload(written), reqId);
    }

    private void HandlePacketResend(byte[] payload, byte flags, IPEndPoint sender, bool isOwner)
    {
        bool extended = (flags & GvcpConst.FlagExtendedIds) != 0;
        int needed = extended ? 20 : 12;
        if (payload.Length < needed)
        {
            Malformed($"PACKETRESEND payload of {payload.Length} bytes (expected {needed})");
            return;
        }

        ushort channel = SimWire.ReadU16(payload, 0);
        ulong blockId;
        uint first, last;
        if (extended)
        {
            first = SimWire.ReadU32(payload, 4);
            last = SimWire.ReadU32(payload, 8);
            blockId = SimWire.ReadU64(payload, 12);
        }
        else
        {
            blockId = SimWire.ReadU16(payload, 2);
            first = SimWire.ReadU32(payload, 4) & 0x00FF_FFFF;
            last = SimWire.ReadU32(payload, 8) & 0x00FF_FFFF;
        }

        bool accepted = isOwner && channel == 0;
        lock (_resendRequests)
        {
            _resendRequests.Add(new SimResendRequest(blockId, first, last, sender, accepted));
            // 관찰 목록에 상한을 둔다 — 장시간 소크에서 리센드마다 자라지 않게. 오래된 것부터 버리고 버린 수를 센다.
            int excess = _resendRequests.Count - ResendRequestsCap;
            if (excess > 0)
            {
                _resendRequests.RemoveRange(0, excess);
                Interlocked.Add(ref _resendRequestsTrimmed, excess);
            }
        }
        if (!accepted)
        {
            SetError(isOwner
                ? $"PACKETRESEND for stream channel {channel} ignored: only channel 0 exists"
                : $"PACKETRESEND from {sender} ignored: not the controlling application");
            return;
        }
        Resend(blockId, first, last);
    }

    // ---- 레지스터 쓰기와 부수효과 ----

    /// <summary>정렬·범위·보호·제어권 규칙을 적용한 뒤 저장하고 부수효과를 일으킨다. 반환값은 ACK status.</summary>
    private ushort WriteRegister(uint addr, uint value, IPEndPoint sender)
    {
        if (addr % 4 != 0) return GvcpConst.StatusBadAlignment;
        if (!Registers.Contains(addr, 4)) return GvcpConst.StatusInvalidAddress;
        if (addr == GvbsAddr.Ccp) return WriteCcp(value, sender);
        if (Registers.IsReadOnly(addr, 4)) return GvcpConst.StatusWriteProtect;

        if (addr == GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset))
        {
            // 파이어테스트 비트는 저장하지 않고 즉시 테스트 패킷 하나로 소비한다
            bool fire = (value & GvbsAddr.ScpsFireTest) != 0;
            Registers.WriteU32(addr, value & ~GvbsAddr.ScpsFireTest);
            if (fire) FireTestPacket(value & GvbsAddr.ScpsSizeMask);
            return GvcpConst.StatusSuccess;
        }

        Registers.WriteU32(addr, value);
        ApplySideEffect(addr, value);
        return GvcpConst.StatusSuccess;
    }

    private void ApplySideEffect(uint addr, uint value)
    {
        switch (addr)
        {
            case SimFeatureAddr.AcquisitionStart:
                Registers.WriteU32(addr, 0);
                if (value != 0) StartAcquisition();
                break;

            case SimFeatureAddr.AcquisitionStop:
                Registers.WriteU32(addr, 0);
                if (value != 0) StopAcquisition(join: true);
                break;

            case SimFeatureAddr.UserSetLoad:
                Registers.WriteU32(addr, 0);
                if (value != 0) ResetFeatures();
                break;

            case SimFeatureAddr.TriggerSoftware:
                Registers.WriteU32(addr, 0);
                // TriggerMode = On 일 때만 무장한다 — Off 이면 자유 실행이라 트리거가 뜻이 없고, 나중에 On 으로 바꿀 때 묵은 트리거가 새어 나가면 안 된다
                if (value != 0 && (Registers.ReadU32(SimFeatureAddr.TriggerControl) & SimFeatureAddr.TriggerModeMask) != 0)
                {
                    Interlocked.Exchange(ref _softwareTriggerPending, 1);
                    SignalSender();   // 트리거 대기 중인 송신 스레드를 바로 깨운다 — 폴링 주기만큼 프레임이 늦지 않게
                }
                break;

            case SimFeatureAddr.TriggerControl:
                // TriggerMode 를 바꾸면 송신 스레드가 보는 조건이 바뀐다 — 폴링 주기를 기다리지 않고 바로 깨워
                // 다음 판단(자유 실행 ↔ 트리거 대기)이 즉시 반영되게 한다. 값은 그대로 둔다(평범한 RW 레지스터).
                SignalSender();
                break;

            case GvbsAddr.TimestampControl:
                // 값(LSB 기준): 2 = reset, 1 = latch. 쓰기 전용 성격이라 읽으면 0.
                Registers.WriteU32(addr, 0);
                if ((value & 2) != 0) Volatile.Write(ref _timestampBaseNs, NowNs);
                if ((value & 1) != 0)
                {
                    ulong ts = TimestampTicks;
                    Registers.WriteU32(GvbsAddr.TimestampLatchedHigh, (uint)(ts >> 32));
                    Registers.WriteU32(GvbsAddr.TimestampLatchedLow, (uint)ts);
                }
                break;
        }
    }

    private ushort WriteCcp(uint value, IPEndPoint sender)
    {
        bool changed = false;
        IPEndPoint? ownerAfter;
        lock (_gate)
        {
            if (_owner is not null && !_owner.Equals(sender)) return GvcpConst.StatusAccessDenied;

            if ((value & (GvbsAddr.CcpControl | GvbsAddr.CcpExclusive)) == 0)
            {
                // 0(또는 스위치오버 비트만) → 해제
                Registers.WriteU32(GvbsAddr.Ccp, 0);
                Registers.WriteU32(GvbsAddr.PrimaryAppPort, 0);
                Registers.WriteU32(GvbsAddr.PrimaryAppIp, 0);
                if (_owner is not null) { _owner = null; changed = true; }
            }
            else
            {
                Registers.WriteU32(GvbsAddr.Ccp, value & (GvbsAddr.CcpControl | GvbsAddr.CcpExclusive | GvbsAddr.CcpSwitchoverEnable));
                Registers.WriteU32(GvbsAddr.PrimaryAppPort, (uint)sender.Port);
                Registers.WriteU32(GvbsAddr.PrimaryAppIp, ToU32(sender.Address));
                if (_owner is null) changed = true;
                _owner = sender;
                _ownerClock.Restart();
            }
            ownerAfter = _owner;
        }
        if (changed) ControlOwnerChanged?.Invoke(ownerAfter is null ? null : new IPEndPoint(ownerAfter.Address, ownerAfter.Port));
        return GvcpConst.StatusSuccess;
    }

    private bool HasWriteAccess(IPEndPoint sender)
    {
        lock (_gate) return _owner is null || _owner.Equals(sender);
    }

    private bool IsOwner(IPEndPoint sender)
    {
        lock (_gate) return _owner is not null && _owner.Equals(sender);
    }

    private void TouchHeartbeat()
    {
        lock (_gate) _ownerClock.Restart();
    }

    /// <summary>제어권 보유자의 마지막 명령 이후 HeartbeatTimeout(ms)이 지났으면 CCP 를 비운다. 레지스터가 0 이면 만료하지 않는다.</summary>
    private void CheckHeartbeat()
    {
        bool expired = false;
        lock (_gate)
        {
            if (_owner is null) return;
            uint timeoutMs = Registers.ReadU32(GvbsAddr.HeartbeatTimeout);
            if (timeoutMs == 0) return;
            if (_ownerClock.ElapsedMilliseconds > timeoutMs)
            {
                _owner = null;
                Registers.WriteU32(GvbsAddr.Ccp, 0);
                Registers.WriteU32(GvbsAddr.PrimaryAppPort, 0);
                Registers.WriteU32(GvbsAddr.PrimaryAppIp, 0);
                expired = true;
            }
        }
        if (expired)
        {
            Interlocked.Increment(ref _heartbeatTimeouts);
            ControlOwnerChanged?.Invoke(null);
        }
    }

    // ---- 송신 ----

    private void SendAck(IPEndPoint to, ushort status, ushort ackCmd, byte[] payload, ushort reqId)
    {
        var pkt = new byte[GvcpConst.HeaderSize + payload.Length];
        SimWire.WriteU16(pkt, 0, status);
        SimWire.WriteU16(pkt, 2, ackCmd);
        SimWire.WriteU16(pkt, 4, (ushort)payload.Length);
        SimWire.WriteU16(pkt, 6, reqId);
        Buffer.BlockCopy(payload, 0, pkt, GvcpConst.HeaderSize, payload.Length);
        try
        {
            _gvcpSocket?.SendTo(pkt, to);
        }
        catch (SocketException ex)
        {
            Interlocked.Increment(ref _sendErrors);
            SetError($"GVCP send to {to} failed: {ex.SocketErrorCode}");
        }
        catch (ObjectDisposedException)
        {
            // 정지 중
        }
    }

    private static byte[] IndexPayload(int index)
    {
        var p = new byte[4];
        SimWire.WriteU16(p, 2, (ushort)Math.Min(index, ushort.MaxValue));
        return p;
    }

    private void Malformed(string reason)
    {
        Interlocked.Increment(ref _malformedCount);
        SetError("malformed GVCP packet: " + reason);
    }
}
