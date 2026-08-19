using System.Buffers.Binary;
using System.Text.Json.Serialization;
using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Config;
using Kunling.RobotClient.Core.Models;
using Kunling.RobotClient.Devices.HikvisionMobileRobot;
using Kunling.RobotClient.Devices.Recipes;
using HikvisionProtocolClient = Kunling.RobotClient.Devices.HikvisionMobileRobot.HikvisionMobileRobot;

namespace Kunling.RobotClient.Devices.Chassis;

/// <summary>
/// 海康潜伏式移动机器人底盘适配器。
/// <para>
/// 与 <see cref="SimulatedRobotChassis"/> 实现相同的 <see cref="IChassis"/>，上层只传站点和可选port；
/// 本类解析 MOVE.Templates.json 后转换为海康0x0302空车直线移动报文。
/// </para>
/// <para>
/// 0x0303回复只表示设备已接收任务，不代表运动完成。最终成功必须等待0x0300状态上报，
/// 并使用实际X/Y/角度满足到位误差且稳定一段时间后确认。
/// </para>
/// </summary>
[DeviceModel("HikvisionRobotChassis")]
public sealed class HikvisionRobotChassis : IChassis, IDisposable
{
    private readonly HikvisionProtocolClient _client;
    private readonly ChassisArrivalConfig _arrivalConfig;
    private readonly HikvisionChassisOptions _options;
    private readonly SemaphoreSlim _motionLock = new(1, 1);
    private readonly object _stateLock = new();
    private RobotPose? _actualPose;
    private int? _battery;
    private bool _moving;
    private string? _faultCode;
    private ushort _taskId;
    private ushort? _reportedTaskId;
    private byte? _reportedSubTaskId;
    private uint? _reportedDeviceState;
    private string? _currentMap;
    private bool _disposed;

    public HikvisionRobotChassis(HikvisionProtocolClient client,
        ChassisArrivalConfig arrivalConfig, HikvisionChassisOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _arrivalConfig = arrivalConfig ?? throw new ArgumentNullException(nameof(arrivalConfig));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _currentMap = _options.Map;
        _client.MessageReceived += OnMessageReceived;
        _client.ConnectionFaulted += OnConnectionFaulted;
    }

    /// <summary>
    /// 根据应用配置创建唯一海康协议客户端和底盘参数，并注册到ComponentFactory。
    /// 返回的协议客户端由Program通过await using释放；对象创建本身不会连接真实设备。
    /// </summary>
    public static HikvisionProtocolClient CreateAndRegister(HikvisionRobotRegistrationOptions config, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var client = new HikvisionProtocolClient(new HikvisionMobileRobotOptions
        {
            RemoteHost = config.Host,
            RemotePort = config.Port,
            LocalHost = config.LocalHost,
            LocalPort = config.LocalPort,
            Transport = config.Transport.Equals("TCP", StringComparison.OrdinalIgnoreCase)
                ? HikvisionTransport.Tcp : HikvisionTransport.Udp,
            RequestTimeoutMs = config.RequestTimeoutMs,
            AckRetryIntervalMs = config.AckRetryIntervalMs,
            TaskRetryIntervalMs = config.TaskRetryIntervalMs,
            HeartbeatTimeoutMs = config.HeartbeatTimeoutMs,
            ReconnectDelayMs = config.ReconnectDelayMs,
            ExpectedDeviceId = config.DeviceId,
            Log = log
        });
        var options = new HikvisionChassisOptions
        {
            DeviceId = config.DeviceId,
            Model = config.Model,
            Map = config.Map,
            DefaultSpeed = config.DefaultSpeed,
            MaxSpeedMmPerSecond = config.MaxSpeedMmPerSecond,
            Log = log
        };
        Kunling.RobotClient.Core.Controller.ComponentFactory.RegisterInstance(client);
        Kunling.RobotClient.Core.Controller.ComponentFactory.RegisterInstance(options);
        return client;
    }

    public string Vendor => "HIKROBOT";
    public string Model => _options.Model;

    /// <summary>解析目标站点、下发海康移动任务，并等待状态上报证明真实到位。</summary>
    public async Task<DeviceResult<MoveResult>> MoveAsync(MoveRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(request.PointName))
            return DeviceResult<MoveResult>.Fail(new(4000, "pointName 不能为空。", Model));
        if (request.Speed <= 0 || double.IsNaN(request.Speed) || double.IsInfinity(request.Speed))
            return DeviceResult<MoveResult>.Fail(new(4000, "speed 必须是大于0的有效数值。", Model));

        if (request.Pose is null)
            return DeviceResult<MoveResult>.Fail(new(4000,
                "pose 不能为空；服务器必须随 MOVE 下发完整位置。", Model));
        var requestedArrival = request.Arrival ?? new MoveArrivalRequest(
            _arrivalConfig.XyToleranceMm, _arrivalConfig.YawToleranceDeg);
        var recipe = new ChassisRecipe(request.Pose,
            new ArrivalCriteria(requestedArrival.PositionToleranceMm, requestedArrival.AngleToleranceDeg,
                TimeoutMs: requestedArrival.TimeoutMs), request.Speed);
        if (string.IsNullOrWhiteSpace(_currentMap))
            return DeviceResult<MoveResult>.Fail(new(4400,
                "海康当前地图未知；请在appsettings.json配置hikvisionRobot.map，或完成切图状态同步后再MOVE。", Model));
        if (!string.Equals(_currentMap, recipe.Pose.Map, StringComparison.OrdinalIgnoreCase))
            return DeviceResult<MoveResult>.Fail(new(4400,
                $"目标地图与海康当前地图不一致：current={_currentMap}, target={recipe.Pose.Map}。", Model));

        // 全局底盘误差配置覆盖站点文件中的默认值，所有底盘实现保持一致。
        var arrival = recipe.Arrival;

        await _motionLock.WaitAsync(ct).ConfigureAwait(false);
        ushort taskId = 0;
        try
        {
            // 海康 AGV 是主动注册方；未注册时禁止下发控制命令，避免命令发往错误或尚未就绪的端点。
            await _client.WaitForRegistrationAsync(ct).ConfigureAwait(false);
            taskId = NextTaskId();
            var body = BuildStraightMoveBody(taskId, recipe, request.Speed);
            lock (_stateLock)
            {
                _moving = true;
                _faultCode = null;
                _reportedTaskId = null;
                _reportedSubTaskId = null;
                _reportedDeviceState = null;
            }
            _options.WriteLog("MOVE",
                $"send signal=0x0302 task={taskId}, pointName={request.PointName}, " +
                $"port={request.Port ?? "-"}, pose={recipe.Pose}, speed={request.Speed}");

            HikvisionResponse response;
            try
            {
                response = await _client.SendRawAsync(HikvisionSignals.MoveStraight, body,
                    HikvisionContentType.Binary, ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                return DeviceResult<MoveResult>.Fail(new(5100, ex.Message, Model, false, true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return DeviceResult<MoveResult>.Fail(new(5100, $"海康移动指令发送失败：{ex.Message}", Model, false, true));
            }

            if (!TryReadCommandReply(response.Body.Span, out var resultCode) || resultCode != 200)
                return DeviceResult<MoveResult>.Fail(new(5103,
                    $"海康底盘拒绝移动指令，reply={resultCode}。", Model, true, resultCode != 201));

            _options.WriteLog("ACCEPTED", $"task={taskId}, reply={resultCode}；等待0x0300状态上报到位");
            RobotPose? actual;
            try { actual = await WaitArrivalAsync(taskId, 0, recipe.Pose, arrival, ct).ConfigureAwait(false); }
            catch (InvalidOperationException ex)
            {
                await TryStopAsync(taskId).ConfigureAwait(false);
                return DeviceResult<MoveResult>.Fail(new(5104, ex.Message, Model, true, false));
            }
            if (actual is null)
            {
                // 状态上报超时时任务物理结果未知，尽力发送停车，避免只终止软件等待。
                await TryStopAsync(taskId).ConfigureAwait(false);
                return DeviceResult<MoveResult>.Fail(new(5101,
                    "海康底盘到位判定超时，未获得满足误差条件的状态上报。", Model, false, true));
            }

            _options.WriteLog("ARRIVAL", $"task={taskId}, actual={actual}");
            return DeviceResult<MoveResult>.Ok(new(request.PointName, actual));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 软件取消不能只结束等待；使用独立安全令牌尽力停止仍可能运动的真实底盘。
            if (taskId != 0) await TryStopAsync(taskId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_stateLock) _moving = false;
            _motionLock.Release();
        }
    }

    /// <summary>按照统一约定返回MOVE.Templates.json中的STANDBY站点。</summary>
    public Task<DeviceResult<MoveResult>> ReturnToStandbyAsync(CancellationToken ct) =>
        Task.FromResult(DeviceResult<MoveResult>.Fail(new(4000,
            "客户端不保存 STANDBY 位姿；请由服务器使用 MOVE 下发 STANDBY 的完整 pose。", Model)));

    /// <summary>返回最近一次海康状态上报形成的底盘快照；尚未收到状态时Pose为空。</summary>
    public Task<DeviceResult<ChassisStatus>> GetStatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_stateLock)
            return Task.FromResult(DeviceResult<ChassisStatus>.Ok(
                new(_client.IsConnected, _moving, _battery, _actualPose, _faultCode)));
    }

    /// <summary>
    /// 构造协议规定的128字节0x0302消息体：任务信息28字节、目标48字节、最终目标8字节、避障信息44字节。
    /// 坐标单位mm；角度由系统的度转换为1/1000度。
    /// </summary>
    private byte[] BuildStraightMoveBody(ushort taskId, ChassisRecipe recipe, double speed)
    {
        var body = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), _options.DeviceId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), taskId);
        body[6] = 0;                                      // 任务子ID
        body[7] = (byte)(recipe.MoveType ?? _options.MoveType); // 可按port配置前进、后退或全向运动方向。
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8, 2), _options.MajorTaskType);
        // 10-27为动作同步、语音、灯光、反光板、货架和协议预留，本动作均保持0。

        const int targetOffset = 28;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(targetOffset, 4), ToInt32(recipe.Pose.X));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(targetOffset + 4, 4), ToInt32(recipe.Pose.Y));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(targetOffset + 8, 4), DegreesToMilliDegrees(recipe.Pose.Yaw));
        body[targetOffset + 12] = recipe.TargetType ?? _options.TargetType;
        body[targetOffset + 13] = recipe.AccuracyLevel ?? _options.AccuracyLevel;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(targetOffset + 14, 2), ToUInt16(_arrivalConfig.XyToleranceMm));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(targetOffset + 16, 4), DegreesToMilliDegrees(_arrivalConfig.YawToleranceDeg));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(targetOffset + 20, 4), DegreesToMilliDegrees(_arrivalConfig.YawToleranceDeg));
        var speedMmPerSecond = Math.Clamp(
            (int)Math.Round(speed * _options.SpeedScaleMmPerSecond), 1, _options.MaxSpeedMmPerSecond);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(targetOffset + 24, 4), speedMmPerSecond);
        // targetOffset+28 为全向车路径角度；+32~47为预留。

        const int finalTargetOffset = 76;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(finalTargetOffset, 4), ToInt32(recipe.Pose.X));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(finalTargetOffset + 4, 4), ToInt32(recipe.Pose.Y));
        // 84~127为避障参数。保持0表示使用设备当前避障方案，不在适配器中暗改安全区。
        return body;
    }

    private async Task<RobotPose?> WaitArrivalAsync(ushort taskId, byte subTaskId,
        RobotPose target, ArrivalCriteria criteria, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        DateTime? stableSince = null;
        while ((DateTime.UtcNow - started).TotalMilliseconds < criteria.TimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            RobotPose? actual;
            ushort? reportedTaskId;
            byte? reportedSubTaskId;
            uint? deviceState;
            string? fault;
            lock (_stateLock)
            {
                actual = _actualPose;
                reportedTaskId = _reportedTaskId;
                reportedSubTaskId = _reportedSubTaskId;
                deviceState = _reportedDeviceState;
                fault = _faultCode;
            }
            if (deviceState.HasValue && HikvisionDeviceStates.IsFaultOrBlocked(deviceState.Value))
                throw new InvalidOperationException(fault ?? $"海康底盘状态异常：0x{deviceState:X}。");
            // 0x0303 仅表示接收成功；必须由同一 task/subTask 的 0x0300“任务完成”状态证明动作完成。
            var taskCompleted = reportedTaskId == taskId && reportedSubTaskId == subTaskId
                && deviceState.HasValue && HikvisionDeviceStates.IsCompletion(deviceState.Value);
            if (taskCompleted && actual is not null && IsArrived(actual, target, criteria))
            {
                stableSince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - stableSince.Value).TotalMilliseconds >= criteria.SettleMs) return actual;
            }
            else stableSince = null;
            await Task.Delay(Math.Max(10, criteria.PollMs), ct).ConfigureAwait(false);
        }
        return null;
    }

    private static bool IsArrived(RobotPose actual, RobotPose target, ArrivalCriteria criteria)
    {
        if (!string.IsNullOrWhiteSpace(actual.Map) && !string.IsNullOrWhiteSpace(target.Map)
            && !actual.Map.Equals(target.Map, StringComparison.OrdinalIgnoreCase)) return false;
        var xy = Math.Sqrt(Math.Pow(actual.X - target.X, 2) + Math.Pow(actual.Y - target.Y, 2));
        return xy <= criteria.PositionToleranceMm
            && AngleDifference(actual.Yaw, target.Yaw) <= criteria.AngleToleranceDeg;
    }

    private void OnMessageReceived(object? sender, HikvisionMessage message)
    {
        if (message.Signal != HikvisionSignals.ReportDeviceState) return;
        try
        {
            if (message.ContentType == HikvisionContentType.Json)
            {
                var state = message.DeserializeJson<HikvisionStateJson>();
                if (state is not null) UpdateState(new(state.X, state.Y, state.Theta / 1000d, state.Map),
                    state.Battery, state.DeviceState, state.ErrorCode, state.TaskId, state.SubTaskId);
            }
            else if (TryReadBinaryState(message.Body.Span, out var snapshot))
            {
                UpdateState(snapshot.Pose, snapshot.Battery, snapshot.DeviceState, snapshot.Error,
                    snapshot.TaskId, snapshot.SubTaskId);
                _options.WriteLog("TELEMETRY",
                    $"speed={snapshot.SpeedMmPerSecond}mm/s, poseTrusted={snapshot.PoseTrusted}, mapCode={snapshot.MapCode}, navigation={snapshot.NavigationType}");
            }
            _ = AcknowledgeStateAsync(message);
        }
        catch (Exception ex) { _options.WriteLog("STATE_ERROR", ex.Message); }
    }

    private async Task AcknowledgeStateAsync(HikvisionMessage message)
    {
        try
        {
            var ack = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(0, 4), _options.DeviceId);
            BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(4, 4), 200);
            ack[8] = 0; // 异常码；9~11为协议对齐保留区。
            await _client.ReplyRawAsync(message, ack).ConfigureAwait(false);
        }
        catch (Exception ex) { _options.WriteLog("STATE_ACK_ERROR", ex.Message); }
    }

    private void UpdateState(RobotPose pose, int? battery, uint state, string? error,
        ushort? taskId, byte? subTaskId)
    {
        lock (_stateLock)
        {
            _actualPose = pose;
            if (battery.HasValue) _battery = battery;
            _moving = HikvisionDeviceStates.IsRunning(state);
            _faultCode = !string.IsNullOrWhiteSpace(error) ? error : HikvisionDeviceStates.DescribeFault(state);
            _reportedTaskId = taskId;
            _reportedSubTaskId = subTaskId;
            _reportedDeviceState = state;
        }
        _options.WriteLog("STATE", $"task={taskId}/{subTaskId}, pose={pose}, state={state}, battery={battery}, error={error}");
    }

    private void OnConnectionFaulted(object? sender, Exception exception)
    {
        lock (_stateLock) { _faultCode = exception.Message; _moving = false; }
    }

    private async Task TryStopAsync(ushort taskId)
    {
        try
        {
            using var safetyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var body = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), _options.DeviceId);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), taskId);
            _options.WriteLog("STOP_SENT", $"task={taskId}");
            var response = await _client.SendRawAsync(HikvisionSignals.Stop, body, HikvisionContentType.Binary,
                safetyTimeout.Token).ConfigureAwait(false);
            if (!TryReadCommandReply(response.Body.Span, out var result) || result != 200)
            {
                _options.WriteLog("STOP_FAILED", $"task={taskId}, reply={result}");
                return;
            }
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                uint? state;
                lock (_stateLock) state = _reportedDeviceState;
                if (state.HasValue && !HikvisionDeviceStates.IsRunning(state.Value))
                {
                    _options.WriteLog("STOP_CONFIRMED", $"task={taskId}, state=0x{state:X}");
                    return;
                }
                await Task.Delay(100, safetyTimeout.Token).ConfigureAwait(false);
            }
            _options.WriteLog("STOP_UNCONFIRMED", $"task={taskId}，应答成功但未收到停止状态");
        }
        catch (Exception ex) { _options.WriteLog("STOP_ERROR", ex.Message); }
    }

    private static bool TryReadCommandReply(ReadOnlySpan<byte> body, out uint result)
    {
        result = 0;
        if (body.Length < 8) return false;
        result = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4));
        return true;
    }

    private bool TryReadBinaryState(ReadOnlySpan<byte> body, out HikvisionStateSnapshot snapshot)
    {
        snapshot = default!;
        // 固定状态区至少108字节；后面是由执行机构索引决定的可变联合数据。
        if (body.Length < 108) return false;
        var deviceId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
        if (deviceId != _options.DeviceId) throw new InvalidDataException($"状态设备编号不匹配：{deviceId}。");
        var taskId = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(4, 2));
        var subTaskId = body[6];
        var poseTrusted = body[7] == 1;
        var deviceState = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(8, 4));
        var x = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(12, 4));
        var y = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(16, 4));
        var theta = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(20, 4)) / 1000d;
        var speed = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(24, 4));
        var mapCode = System.Text.Encoding.ASCII.GetString(body.Slice(44, 2));
        var majorAlarm = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(48, 2));
        var minorAlarm = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(50, 2));
        var battery = body[58];
        var navigation = body[65];
        var error = majorAlarm != 0 || minorAlarm != 0
            ? $"HIKVISION_ALARM_{majorAlarm:X4}_{minorAlarm:X4}"
            : HikvisionDeviceStates.DescribeFault(deviceState);
        snapshot = new(new(x, y, theta, _currentMap), deviceState, taskId, subTaskId,
            battery <= 100 ? battery : null, speed, poseTrusted, mapCode, navigation, error);
        return true;
    }

    private ushort NextTaskId()
    {
        lock (_stateLock) { _taskId++; if (_taskId == 0) _taskId = 1; return _taskId; }
    }

    private static int ToInt32(double value) => checked((int)Math.Round(value));
    private static ushort ToUInt16(double value) => checked((ushort)Math.Clamp(Math.Round(value), 0, ushort.MaxValue));
    private static int DegreesToMilliDegrees(double value) => checked((int)Math.Round(value * 1000d));
    private static double AngleDifference(double a, double b) { var d = Math.Abs((a - b) % 360d); return d > 180d ? 360d - d : d; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.MessageReceived -= OnMessageReceived;
        _client.ConnectionFaulted -= OnConnectionFaulted;
        _motionLock.Dispose();
    }
}

internal sealed record HikvisionStateSnapshot(RobotPose Pose, uint DeviceState, ushort TaskId,
    byte SubTaskId, int? Battery, int SpeedMmPerSecond, bool PoseTrusted,
    string MapCode, byte NavigationType, string? Error);

/// <summary>
/// 应用组合根创建海康协议客户端所需的参数。
/// 此类型属于Devices层，避免设备项目反向依赖Core中的JSON配置类型；Program只负责完成值映射。
/// </summary>
public sealed record HikvisionRobotRegistrationOptions(
    string Host,
    int Port,
    string LocalHost,
    int LocalPort,
    string Transport,
    uint DeviceId,
    string Model,
    string? Map,
    int RequestTimeoutMs,
    int AckRetryIntervalMs,
    int TaskRetryIntervalMs,
    int HeartbeatTimeoutMs,
    int ReconnectDelayMs,
    double DefaultSpeed,
    int MaxSpeedMmPerSecond);

/// <summary>海康底盘业务参数。连接IP、端口和传输协议配置属于HikvisionMobileRobotOptions。</summary>
public sealed class HikvisionChassisOptions
{
    public uint DeviceId { get; init; } = 6001;
    public string Model { get; init; } = "HIKROBOT_UNDERRIDE";
    public string? Map { get; init; }
    public int MoveType { get; init; } = 2;
    public ushort MajorTaskType { get; init; } = 0x0101;
    public byte TargetType { get; init; }
    public byte AccuracyLevel { get; init; }
    public double DefaultSpeed { get; init; } = 0.5;
    public int SpeedScaleMmPerSecond { get; init; } = 1000;
    public int MaxSpeedMmPerSecond { get; init; } = 1200;
    public Action<string>? Log { get; init; }

    internal void Validate()
    {
        if (DeviceId == 0) throw new ArgumentOutOfRangeException(nameof(DeviceId));
        if (MoveType is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(MoveType));
        if (DefaultSpeed <= 0 || SpeedScaleMmPerSecond <= 0 || MaxSpeedMmPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(DefaultSpeed));
    }

    internal void WriteLog(string action, string message) => Log?.Invoke($"[DEVICE][CHASSIS:{Model}] {action} {message}");
}

/// <summary>JSON状态上报最小映射；未使用字段由Json反序列化器自动忽略。</summary>
internal sealed record HikvisionStateJson(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("theta")] double Theta,
    [property: JsonPropertyName("deviceState")] uint DeviceState,
    [property: JsonPropertyName("battery")] int? Battery,
    [property: JsonPropertyName("map")] string? Map,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("taskId")] ushort? TaskId,
    [property: JsonPropertyName("subTaskId")] byte? SubTaskId);

/// <summary>海康设备状态分类。避免把ready/完成误判为运动，并在故障状态到达时立即形成可见故障。</summary>
internal static class HikvisionDeviceStates
{
    internal const uint Ready = 0x01;
    internal const uint TaskCompleted = 0x02;
    internal const uint Paused = 0x81;
    internal const uint Fault = 0x82;

    internal static bool IsCompletion(uint state) => state is Ready or TaskCompleted or 0x382 or 0x383;

    internal static bool IsRunning(uint state) => state is >= 0x03 and <= 0x0D
        || state is >= 0x41 and <= 0x7F;

    internal static bool IsFaultOrBlocked(uint state) => state is Paused or Fault or 0x83 or 0x384
        || state is >= 0x41 and <= 0x80 || state >= 0xC1;

    internal static string? DescribeFault(uint state) => state switch
    {
        Fault => "HIKVISION_STATE_0x82: 设备异常",
        Paused => "HIKVISION_STATE_0x81: 任务暂停",
        0x83 => "HIKVISION_STATE_0x83: 异常偏航",
        0x384 => "HIKVISION_STATE_0x384: 异常休眠模式",
        >= 0x41 and <= 0x80 => $"HIKVISION_STATE_0x{state:X2}: 设备受阻或等待干预",
        >= 0xC1 => $"HIKVISION_STATE_0x{state:X}: 平台指令或设备业务异常",
        _ => null
    };
}
