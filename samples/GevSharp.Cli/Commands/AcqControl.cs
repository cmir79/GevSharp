using GevSharp.GenApi;

namespace GevSharp.Cli.Commands;

/// <summary>
/// 획득 시작·정지 경로. 노드맵이 있으면 AcquisitionStart/AcquisitionStop 커맨드 노드를 실행하고,
/// 없으면(런타임 미탑재·XML 실패) 사용자가 준 레지스터 주소에 1 을 쓴다 — 시뮬레이터의 0x10030/0x10034 처럼 자기 소거 비트를 가정한다.
/// 주소를 명시하면 노드맵보다 우선한다.
/// </summary>
public sealed class AcqControl
{
    private const uint CommandValue = 1;

    private readonly GevDevice _dev;
    private readonly ICommand? _start;
    private readonly ICommand? _stop;
    private readonly uint? _startAddr;
    private readonly uint? _stopAddr;

    private AcqControl(GevDevice dev, ICommand? start, ICommand? stop, uint? startAddr, uint? stopAddr, string description, int? payloadSize)
    {
        _dev = dev;
        _start = start;
        _stop = stop;
        _startAddr = startAddr;
        _stopAddr = stopAddr;
        Description = description;
        PayloadSize = payloadSize;
    }

    /// <summary>어느 경로를 쓰는지 — 출력용.</summary>
    public string Description { get; }

    /// <summary>노드맵의 PayloadSize 값. 없거나 못 읽으면 null.</summary>
    public int? PayloadSize { get; }

    public static async Task<AcqControl> CreateAsync(GevDevice dev, uint? startAddr, uint? stopAddr, CancellationToken ct)
    {
        if (startAddr is not null)
        {
            var stopText = stopAddr is null
                ? "none: the device keeps acquiring until the stream closes the channel (SCP = 0)"
                : $"0x{stopAddr.Value:X}";
            return new AcqControl(dev, null, null, startAddr, stopAddr,
                $"register writes (start 0x{startAddr.Value:X} <- {CommandValue}, stop {stopText})", null);
        }

        GenApiNodeMap? nodes = null;
        string reason;
        try
        {
            nodes = await dev.GetNodeMapAsync(ct);
            reason = string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (NotImplementedException ex)
        {
            reason = ex.Message;
        }
        catch (GevException ex)
        {
            reason = ex.Message;
        }

        if (nodes is null)
        {
            throw new CliUsageException(
                $"the GenApi node map is not available ({reason}); pass --acq-start-addr <hex> [--acq-stop-addr <hex>] to start "
                + "acquisition with a register write instead (simulator: --acq-start-addr 0x10030 --acq-stop-addr 0x10034)");
        }

        var start = nodes.GetNode("AcquisitionStart") as ICommand
            ?? throw new GevException("the node map has no AcquisitionStart command node; use --acq-start-addr to start acquisition by register");
        var stop = nodes.GetNode("AcquisitionStop") as ICommand;
        if (stop is null) CliApp.Log(GevLogLevel.Warn, "the node map has no AcquisitionStop command node; acquisition will be stopped by closing the stream channel only");

        int? payloadSize = null;
        if (nodes.GetNode("PayloadSize") is IInteger payloadNode)
        {
            try
            {
                var value = await payloadNode.GetAsync(ct);
                if (value > 0 && value <= int.MaxValue) payloadSize = (int)value;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CliApp.Log(GevLogLevel.Warn, $"PayloadSize could not be read ({ex.Message}); the frame pool will size itself from the first leader");
            }
        }

        return new AcqControl(dev, start, stop, null, null, "GenApi AcquisitionStart / AcquisitionStop", payloadSize);
    }

    /// <summary>
    /// 획득을 시작한다. 노드맵 경로에서는 먼저 TLParamsLocked = 1 로 전송 계층 구성이 끝났음을 알린다 —
    /// 벤더 XML 은 이 값으로 획득 커맨드의 잠금을 표현해서(AcquisitionStart 의 pIsLocked), 쓰지 않으면 커맨드가 잠긴 채다.
    /// 스트림이 이미 시작된 뒤에 불러야 한다.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_start is not null)
        {
            await _dev.SetTlParamsLockedAsync(true, ct);
            await _start.ExecuteAsync(ct);
        }
        else
        {
            await _dev.WriteRegAsync(_startAddr!.Value, CommandValue, ct);
        }
    }

    /// <summary>획득을 멈추고 전송 계층 잠금을 푼다(포맷 파라미터가 다시 쓰기 가능해진다).</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (_stop is not null)
        {
            await _stop.ExecuteAsync(ct);
            await _dev.SetTlParamsLockedAsync(false, ct);
        }
        else if (_stopAddr is not null)
        {
            await _dev.WriteRegAsync(_stopAddr.Value, CommandValue, ct);
        }
    }
}
