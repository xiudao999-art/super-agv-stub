using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;
using RobotLibrarys;

namespace Kunling.RobotClient.Devices.HuayanRobot;

/// <summary>
/// 华沿 RobotLibrarys V1.0.17.0 适配器。继承 RobotAPI，厂家 DLL 的全部 HRIF_* 原始接口
/// 可直接调用；同时实现调度系统 IArm 强类型接口。
/// </summary>
[DeviceModel("HuayanRobot")]
public sealed class HuayanRobot : RobotAPI, IArm, IDisposable, IAsyncDisposable
{
    // RobotAPI 内部维护连接与收发状态，不应由多个 Action 并发调用。
    // 使用同一把锁串行化连接、运动及状态读取，避免响应数据相互串扰。
    private readonly SemaphoreSlim _sdkLock = new(1, 1);

    // 当前设备配置。仅允许在未连接状态通过 Configure 替换，避免会话中途切换控制器。
    private HuayanRobotOptions _options;

    // 防止重复释放 SemaphoreSlim 或重复调用厂家断开接口。
    private bool _disposed;

    // 网络连接不等于机械臂已经完成电箱连接、上电、复位和使能。
    // 只有配置要求的初始化步骤全部成功且状态复核通过后，该标志才允许运动入口继续执行。
    private bool _initialized;

    /// <summary>
    /// 供 ComponentFactory 反射创建。默认配置不会自动上电或使能；真实项目应在首次连接前调用 Configure。
    /// </summary>
    public HuayanRobot() : this(new HuayanRobotOptions()) { }

    /// <summary>使用明确配置创建华沿机械臂适配器。</summary>
    public HuayanRobot(HuayanRobotOptions options) { _options = options; _options.Validate(); }

    /// <summary>设备厂商名称，供能力注册、日志和状态快照使用。</summary>
    public string Vendor => "华沿";

    /// <summary>设备具体型号，取自当前配置。</summary>
    public string Model => _options.Model;

    /// <summary>当前生效的只读配置引用；连接状态下不能替换。</summary>
    public HuayanRobotOptions Options => _options;

    /// <summary>同时检查对象未释放和厂家 SDK 会话仍处于连接状态。</summary>
    public bool Connected => !_disposed && HRIF_IsConnected(_options.BoxId);

    /// <summary>
    /// 在未连接状态替换设备配置。连接建立后禁止热切换配置，防止 BoxId、RobotId 或坐标系与现有会话不一致。
    /// </summary>
    public void Configure(HuayanRobotOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Connected) throw new InvalidOperationException("机器人已连接，不能修改连接配置。");
        options.Validate();
        _options = options;
    }

    /// <summary>
    /// 建立 SDK 会话，并按配置依次执行：连接电箱 → 上电 → 复位 → 使能。
    /// 任一步失败立即停止后续步骤并转换为统一 DeviceError；安全相关步骤全部由配置显式开启。
    /// </summary>
    public async Task<DeviceResult<bool>> ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (HRIF_IsConnected(_options.BoxId) && _initialized)
            {
                var stateCode = ReadRobotState(out var currentState);
                if (stateCode == 0 && InitializationSatisfied(currentState)) return DeviceResult<bool>.Ok(true);
                // 会话期间可能发生掉电、失能、电箱断开或控制器报错；清除就绪标志并按配置重新初始化。
                _initialized = false;
            }

            var code = 0;
            if (!HRIF_IsConnected(_options.BoxId))
            {
                // HRIF_Connect 只建立到控制器服务的网络连接，不等同于电箱已连接、机器人已上电或已使能。
                code = HRIF_Connect(_options.BoxId, _options.Host, _options.Port);
                if (code != 0) return Failure<bool>(code, "连接控制器失败", false);
            }

            // 任一步失败都保持 _initialized=false。即使网络仍连接，下次调用也会重新执行未完成的初始化，
            // 不会因为 HRIF_IsConnected=true 而错误跳过电箱连接、上电或使能。
            _initialized = false;
            if (_options.ConnectToBox && (code = HRIF_Connect2Box(_options.BoxId)) != 0)
                return Failure<bool>(code, "连接电箱失败", true);
            if (_options.Electrify && (code = HRIF_Electrify(_options.BoxId)) != 0)
                return Failure<bool>(code, "机器人上电失败", true);
            if (_options.ResetOnConnect && (code = HRIF_GrpReset(_options.BoxId, _options.RobotId)) != 0)
                return Failure<bool>(code, "机器人复位失败", true);
            if (_options.EnableOnConnect && (code = HRIF_GrpEnable(_options.BoxId, _options.RobotId)) != 0)
                return Failure<bool>(code, "机器人使能失败", true);

            // 使用控制器真实状态确认初始化结果。只验证配置明确要求的条件，默认安全配置不会擅自要求上电或使能。
            code = ReadRobotState(out var state);
            if (code != 0) return Failure<bool>(code, "读取初始化状态失败", true);
            if (_options.ConnectToBox && !state.ConnectedToBox)
                return StateFailure<bool>(5201, "初始化失败：控制器报告电箱尚未连接。", state, false);
            if (_options.Electrify && !state.Electrified)
                return StateFailure<bool>(5202, "初始化失败：控制器报告机器人尚未上电。", state, false);
            if (_options.EnableOnConnect && !state.Enabled)
                return StateFailure<bool>(5203, "初始化失败：控制器报告轴组尚未使能。", state, false);
            if (state.HasError)
                return Failure<bool>(state.ErrorCode, "初始化后机器人仍处于错误状态", true);

            _initialized = true;
            _options.WriteLog("CONNECT", $"{_options.Host}:{_options.Port}, box={_options.BoxId}, robot={_options.RobotId}");
            return DeviceResult<bool>.Ok(true);
        }
        catch (Exception ex) { return ExceptionFailure<bool>("连接控制器异常", ex); }
        finally { _sdkLock.Release(); }
    }

    /// <summary>断开 SDK 网络会话。本方法不会主动断电，避免退出客户端时改变现场电气状态。</summary>
    public async Task<DeviceResult<bool>> DisconnectAsync(CancellationToken ct = default)
    {
        if (_disposed) return DeviceResult<bool>.Ok(true);
        await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!HRIF_IsConnected(_options.BoxId)) return DeviceResult<bool>.Ok(true);
            var code = HRIF_DisConnect(_options.BoxId);
            if (code == 0) _initialized = false;
            return code == 0 ? DeviceResult<bool>.Ok(true) : Failure<bool>(code, "断开控制器失败", true);
        }
        catch (Exception ex) { return ExceptionFailure<bool>("断开控制器异常", ex); }
        finally { _sdkLock.Release(); }
    }

    /// <summary>
    /// 执行 HOME 位姿运动。优先设计是由 ARM.HOME 模板展开并调用 MoveToPoseAsync；
    /// 此入口只在 Options.HomePose 已明确配置时提供兼容执行。
    /// </summary>
    public Task<DeviceResult<ArmActionResult>> HomeAsync(CancellationToken ct)
    {
        if (_options.HomePose is null)
            return Task.FromResult(DeviceResult<ArmActionResult>.Fail(new(4004,
                "未配置 HomePose；ARM.HOME 应由 ARM.HOME.Templates.json 展开后调用 MoveToPoseAsync。", Model)));
        return MoveToPoseAsync(new("HOME", "HOME", Pose: _options.HomePose,
            PositionToleranceMm: _options.PositionToleranceMm, AngleToleranceDeg: _options.AngleToleranceDeg,
            SettleMs: _options.SettleMs, TimeoutMs: _options.MotionTimeoutMs, PollMs: _options.PollMs,
            Frame: _options.DefaultUcs, SpeedProfile: "HOME"), ct);
    }

    /// <summary>
    /// 将 L1 MOVE_TO_POSE 子动作转换为 HRIF_MoveL，并等待厂家完成标志与实际到位条件同时成立。
    /// “命令发送成功”不代表机械臂真实到位，因此成功结果必须经过实际位姿复核。
    /// </summary>
    public async Task<DeviceResult<ArmActionResult>> MoveToPoseAsync(ArmMoveRequest request, CancellationToken ct)
    {
        if (request.Pose is null)
            return DeviceResult<ArmActionResult>.Fail(new(4000, $"phase {request.PoseRole} 未配置 pose。", Model));
        var connection = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        if (!connection.Success) return DeviceResult<ArmActionResult>.Fail(connection.Error!);

        await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var target = request.Pose;
            // 运动命令下发前先读取完整状态。默认安全配置没有上电/使能时，应在这里明确拒绝，
            // 不能先调用 HRIF_MoveL 再在后续轮询中发现设备不具备运动条件。
            var code = ReadRobotState(out var preflightState);
            if (code != 0) return Failure<ArmActionResult>(code, "运动前状态检查失败", true);
            if (TryGetMotionStateError(preflightState, out var preflightError))
                return DeviceResult<ArmActionResult>.Fail(preflightError);

            // MoveL 需要六轴参考关节位置用于逆解选解。读取当前位置作为参考，可减少姿态相同但关节构型突变的风险。
            code = ReadActual(out _, out var joints);
            if (code != 0) return Failure<ArmActionResult>(code, "读取当前关节位置失败", true);
            var motion = _options.ResolveMotion(request.SpeedProfile);
            var commandId = $"{request.Station}:{request.Point ?? "-"}:{request.PoseRole}:{Guid.NewGuid():N}";
            _options.WriteLog("MOVE_L", $"cmd={commandId}, pose={target}, velocity={motion.Velocity}, acceleration={motion.Acceleration}");
            // nIsSeek=0：本层不使用 DI 触发停止；若项目需要寻位，应在专用模板/子动作中显式实现，不能隐式开启。
            code = HRIF_MoveL(_options.BoxId, _options.RobotId,
                target.X, target.Y, target.Z, target.Rx, target.Ry, target.Rz,
                joints[0], joints[1], joints[2], joints[3], joints[4], joints[5],
                _options.DefaultTcp, ResolveUcs(request.Frame), motion.Velocity, motion.Acceleration, motion.Radius,
                0, 0, 0, commandId);
            if (code != 0) return Failure<ArmActionResult>(code, "下发直线运动失败", true);

            var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : _options.MotionTimeoutMs;
            var started = DateTime.UtcNow;
            ArmPose? actual = null;
            while ((DateTime.UtcNow - started).TotalMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();

                // 协议要求运动过程中持续读取完整机器人状态。异常、急停、光幕、暂停、失能、断电或电箱断开
                // 都必须立即结束当前 Action，不能继续等待并最终伪装成普通“到位超时”。
                code = ReadRobotState(out var state);
                if (code != 0)
                {
                    await StopAndConfirmAsync(commandId).ConfigureAwait(false);
                    return Failure<ArmActionResult>(code, "读取运动状态失败", false);
                }
                if (TryGetMotionStateError(state, out var stateError))
                {
                    await StopAndConfirmAsync(commandId).ConfigureAwait(false);
                    return DeviceResult<ArmActionResult>.Fail(stateError);
                }

                bool done = false;
                code = HRIF_IsMotionDone(_options.BoxId, _options.RobotId, ref done);
                if (code != 0)
                {
                    await StopAndConfirmAsync(commandId).ConfigureAwait(false);
                    return Failure<ArmActionResult>(code, "读取运动完成标志失败", false);
                }
                code = ReadActual(out actual, out _);
                if (code != 0)
                {
                    await StopAndConfirmAsync(commandId).ConfigureAwait(false);
                    return Failure<ArmActionResult>(code, "读取实际位姿失败", false);
                }
                // 厂家完成标志和本地实际位姿判定必须同时满足，防止仅凭通讯层“完成”提前结束 Action。
                if (done && !state.Moving && state.InPosition
                    && IsArrived(actual, target, request.PositionToleranceMm, request.AngleToleranceDeg))
                {
                    if (request.SettleMs > 0) await Task.Delay(request.SettleMs, ct).ConfigureAwait(false);
                    _options.WriteLog("ARRIVAL", $"cmd={commandId}, actual={actual}");
                    return DeviceResult<ArmActionResult>.Ok(new(actual));
                }
                await Task.Delay(Math.Max(10, request.PollMs), ct).ConfigureAwait(false);
            }
            // 超时后物理结果未知，先发送轴组停止，再向上游返回可恢复错误。
            var stopped = await StopAndConfirmAsync(commandId).ConfigureAwait(false);
            return DeviceResult<ArmActionResult>.Fail(new(5102,
                $"机械臂到位超时，目标={target}，实际={actual}，停车确认={stopped}。", Model, false, true));
        }
        // 上游取消 Action 时不能只停止等待任务，必须尽力停止机械臂实际运动。
        catch (OperationCanceledException)
        {
            await StopAndConfirmAsync("cancelled-action").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) { return ExceptionFailure<ArmActionResult>("机械臂运动异常", ex); }
        finally { _sdkLock.Release(); }
    }

    /// <summary>
    /// PICK 是由多个 L1 子动作组成的 L2 主动作，禁止在设备层硬编码；由通用模板执行器逐 phase 调用本适配器。
    /// </summary>
    public Task<DeviceResult<ArmActionResult>> PickAsync(ArmPickRequest request, CancellationToken ct) =>
        Task.FromResult(DeviceResult<ArmActionResult>.Fail(new DeviceError(PlatformErrorCodes.UnsupportedAction,
            "ARM.PICK 必须由 ARM.PICK.Templates.json 展开后执行。", Model)));

    /// <summary>PLACE 与 PICK 相同，必须由对应动作模板展开，设备适配器只执行单设备原子子动作。</summary>
    public Task<DeviceResult<ArmActionResult>> PlaceAsync(ArmPlaceRequest request, CancellationToken ct) =>
        Task.FromResult(DeviceResult<ArmActionResult>.Fail(new DeviceError(PlatformErrorCodes.UnsupportedAction,
            "ARM.PLACE 必须由 ARM.PLACE.Templates.json 展开后执行。", Model)));

    /// <summary>
    /// 读取连接、运动、使能、故障及实际末端位姿。ArmStatus.Homed 当前表示轴组已使能，
    /// 因为厂家状态接口没有独立的“已执行 HOME”持久标志；业务 HOME 完成证据仍由动作模板保存。
    /// </summary>
    public async Task<DeviceResult<ArmStatus>> GetStatusAsync(CancellationToken ct)
    {
        if (_disposed || !HRIF_IsConnected(_options.BoxId)) return DeviceResult<ArmStatus>.Ok(new(false, false, false, null));
        await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var code = ReadRobotState(out var state);
            if (code != 0) return Failure<ArmStatus>(code, "读取机器人状态失败", true);
            code = ReadActual(out var pose, out _);
            if (code != 0) return Failure<ArmStatus>(code, "读取机器人位姿失败", true);
            return DeviceResult<ArmStatus>.Ok(new(true, state.Moving, state.Enabled, pose,
                state.HasError ? $"{state.ErrorCode}@AXIS{state.ErrorAxis}" : null));
        }
        catch (Exception ex) { return ExceptionFailure<ArmStatus>("读取机器人状态异常", ex); }
        finally { _sdkLock.Release(); }
    }

    /// <summary>
    /// 动作执行前统一检查连接。AutoConnect 关闭时只返回未连接错误，不擅自改变现场连接状态。
    /// </summary>
    private async Task<DeviceResult<bool>> EnsureConnectedAsync(CancellationToken ct)
    {
        // AutoConnect 同时承担“连接和按显式配置恢复初始化状态”的职责；即使网络仍连接，也要复核设备就绪条件。
        if (_options.AutoConnect) return await ConnectAsync(ct).ConfigureAwait(false);
        return Connected
            ? DeviceResult<bool>.Ok(true)
            : DeviceResult<bool>.Fail(new(5001, "机器人控制器未连接。", Model, true, true));
    }

    /// <summary>
    /// 一次 HRIF_ReadActPos 同时读取末端位姿、六轴关节、TCP 和 UCS 数据。
    /// 当前上层只消费末端位姿和关节参考值，但仍必须为 SDK 的全部 ref 参数提供存储位置。
    /// </summary>
    private int ReadActual(out ArmPose pose, out double[] joints)
    {
        double x = 0, y = 0, z = 0, rx = 0, ry = 0, rz = 0, j1 = 0, j2 = 0, j3 = 0, j4 = 0, j5 = 0, j6 = 0;
        double tx = 0, ty = 0, tz = 0, trx = 0, trY = 0, trz = 0, ux = 0, uy = 0, uz = 0, urx = 0, ury = 0, urz = 0;
        var code = HRIF_ReadActPos(_options.BoxId, _options.RobotId,
            ref x, ref y, ref z, ref rx, ref ry, ref rz, ref j1, ref j2, ref j3, ref j4, ref j5, ref j6,
            ref tx, ref ty, ref tz, ref trx, ref trY, ref trz, ref ux, ref uy, ref uz, ref urx, ref ury, ref urz);
        pose = new(x, y, z, rx, ry, rz);
        joints = [j1, j2, j3, j4, j5, j6];
        return code;
    }

    /// <summary>读取协议定义的完整机器人状态，统一供连接复核、运动监控、停车确认和状态查询使用。</summary>
    private int ReadRobotState(out RobotStateSnapshot state)
    {
        int moving = 0, enabled = 0, error = 0, errorCode = 0, errorAxis = 0, brake = 0,
            pause = 0, emergency = 0, safeguard = 0, electrify = 0, box = 0, blending = 0, inPos = 0;
        var code = HRIF_ReadRobotState(_options.BoxId, _options.RobotId, ref moving, ref enabled,
            ref error, ref errorCode, ref errorAxis, ref brake, ref pause, ref emergency,
            ref safeguard, ref electrify, ref box, ref blending, ref inPos);
        state = new(moving != 0, enabled != 0, error != 0, errorCode, errorAxis, brake != 0,
            pause != 0, emergency != 0, safeguard != 0, electrify != 0, box != 0, blending != 0, inPos != 0);
        return code;
    }

    /// <summary>把运动期间不允许继续等待的控制器状态转换为可审计的设备错误。</summary>
    private bool TryGetMotionStateError(RobotStateSnapshot state, out DeviceError error)
    {
        if (state.HasError)
            error = new(state.ErrorCode == 0 ? 5210 : state.ErrorCode,
                $"机器人运动错误：code={state.ErrorCode}, axis={state.ErrorAxis}。", Model, false, false,
                DeviceErrorCategory.Motion, DeviceRecoveryStrategy.ResetRequired,
                "停止动作并检查厂家错误码；确认现场安全后复位，禁止直接重试。" );
        else if (state.EmergencyStop)
            error = new(5211, "机器人急停已触发。", Model, false, false,
                DeviceErrorCategory.Safety, DeviceRecoveryStrategy.ManualRecovery,
                "检查电箱急停和外部急停，由人工解除并确认安全后复位。" );
        else if (state.SafeGuard)
            error = new(5212, "机器人安全光幕已触发。", Model, false, false,
                DeviceErrorCategory.Safety, DeviceRecoveryStrategy.ManualRecovery,
                "检查安全光幕区域内人员和障碍物，由人工确认后恢复。" );
        else if (state.Paused)
            error = new(5213, "机器人运动处于暂停状态。", Model, false, false,
                DeviceErrorCategory.State, DeviceRecoveryStrategy.ManualRecovery,
                "由授权流程决定继续或终止任务，禁止自动重发运动。" );
        else if (!state.ConnectedToBox)
            error = new(5214, "机器人运动期间电箱连接断开。", Model, false, false,
                DeviceErrorCategory.Communication, DeviceRecoveryStrategy.PowerCycle,
                "检查电箱连接和控制网络，确认机械臂状态后重新初始化。" );
        else if (!state.Electrified)
            error = new(5215, "机器人运动期间掉电。", Model, false, false,
                DeviceErrorCategory.Hardware, DeviceRecoveryStrategy.ManualRecovery,
                "检查机器人供电，由授权流程重新上电并复核状态。" );
        else if (!state.Enabled)
            error = new(5216, "机器人运动期间轴组失能。", Model, false, false,
                DeviceErrorCategory.Safety, DeviceRecoveryStrategy.ResetRequired,
                "检查失能原因和控制器错误，确认安全后复位并重新使能。" );
        else
        {
            error = null!;
            return false;
        }
        _options.WriteLog("STATE_ERROR", error.Message);
        return true;
    }

    /// <summary>判断控制器当前状态是否仍满足配置明确要求的初始化条件。</summary>
    private bool InitializationSatisfied(RobotStateSnapshot state) =>
        !state.HasError
        && (!_options.ConnectToBox || state.ConnectedToBox)
        && (!_options.Electrify || state.Electrified)
        && (!_options.EnableOnConnect || state.Enabled);

    /// <summary>将厂家正整数错误码翻译为系统统一错误，并保留原始设备码供审计与现场排障。</summary>
    private DeviceResult<T> Failure<T>(int code, string action, bool physicalKnown)
    {
        var message = string.Empty;
        try { HRIF_GetErrorCodeStr(_options.BoxId, code, ref message); } catch { }
        if (string.IsNullOrWhiteSpace(message)) message = "厂家 SDK 未返回错误描述";
        var policy = HuayanErrorCatalog.Resolve(code);
        _options.WriteLog("ERROR",
            $"{action}: code={code}, message={message}, category={policy.Category}, recovery={policy.RecoveryStrategy}, retryable={policy.Retryable}, advice={policy.HandlingAdvice}");
        return DeviceResult<T>.Fail(new(code, $"{action}：{message}", code.ToString(), physicalKnown,
            policy.Retryable, policy.Category, policy.RecoveryStrategy, policy.HandlingAdvice));
    }

    /// <summary>
    /// 将网络中断、SDK 内部异常等非厂家返回码异常转换为统一设备错误。
    /// PhysicalResultKnown=false 表示上游恢复前必须先查询现场实际状态。
    /// </summary>
    private DeviceResult<T> ExceptionFailure<T>(string action, Exception ex) =>
        DeviceResult<T>.Fail(new(5900, $"{action}：{ex.Message}", Model, false, true));

    private DeviceResult<T> StateFailure<T>(int code, string message, RobotStateSnapshot state, bool physicalKnown)
    {
        _options.WriteLog("STATE_ERROR", $"{message} state={state}");
        return DeviceResult<T>.Fail(new(code, message, Model, physicalKnown, false,
            DeviceErrorCategory.State, DeviceRecoveryStrategy.ManualRecovery,
            "检查完整机器人状态并完成现场确认后再执行。"));
    }

    /// <summary>
    /// 取消、超时或状态异常后的安全停车：检查 GrpStop 返回码，并在独立的 3 秒安全窗口内持续读取状态，
    /// 直到控制器确认 nMovingState=0。停车失败不能覆盖原始 Action 错误，但必须留下高优先级日志。
    /// </summary>
    private async Task<bool> StopAndConfirmAsync(string commandId)
    {
        try
        {
            var code = HRIF_GrpStop(_options.BoxId, _options.RobotId);
            if (code != 0)
            {
                var message = string.Empty;
                try { HRIF_GetErrorCodeStr(_options.BoxId, code, ref message); } catch { }
                _options.WriteLog("STOP_FAILED", $"cmd={commandId}, code={code}, message={message}");
                return false;
            }

            _options.WriteLog("STOP_SENT", $"cmd={commandId}；等待控制器确认停止");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                code = ReadRobotState(out var state);
                if (code == 0 && !state.Moving)
                {
                    _options.WriteLog("STOP_CONFIRMED", $"cmd={commandId}, state={state}");
                    return true;
                }
                if (code != 0) _options.WriteLog("STOP_STATE_ERROR", $"cmd={commandId}, code={code}");
                await Task.Delay(Math.Max(10, _options.PollMs)).ConfigureAwait(false);
            }
            _options.WriteLog("STOP_UNCONFIRMED", $"cmd={commandId}；3秒内未确认 nMovingState=0，物理结果未知");
            return false;
        }
        catch (Exception ex)
        {
            _options.WriteLog("STOP_EXCEPTION", $"cmd={commandId}, error={ex.Message}");
            return false;
        }
    }
    /// <summary>
    /// 把厂商无关模板中的 BASE 映射为示教器实际 UCS 名称；其他名称认为是项目显式配置并原样传递。
    /// </summary>
    private string ResolveUcs(string frame) => string.IsNullOrWhiteSpace(frame) || frame.Equals("BASE", StringComparison.OrdinalIgnoreCase) ? _options.DefaultUcs : frame;

    /// <summary>XYZ 使用三维欧氏距离，Rx/Ry/Rz 分别按最短圆周角计算到位误差。</summary>
    private static bool IsArrived(ArmPose a, ArmPose b, double pos, double angle)
    {
        var distance = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
        return distance <= pos && Diff(a.Rx, b.Rx) <= angle && Diff(a.Ry, b.Ry) <= angle && Diff(a.Rz, b.Rz) <= angle;
    }

    /// <summary>计算最短角差，例如 359° 与 1° 的差为 2°，而不是 358°。</summary>
    private static double Diff(double a, double b) { var d = Math.Abs((a - b) % 360); return d > 180 ? 360 - d : d; }

    /// <summary>释放同步资源并断开 SDK；不会执行断电或关机等破坏现场状态的操作。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        try { if (HRIF_IsConnected(_options.BoxId)) HRIF_DisConnect(_options.BoxId); } catch { }
        _disposed = true;
        _sdkLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>等待断开完成后释放锁等托管资源。</summary>
    public async ValueTask DisposeAsync() { if (!_disposed) await DisconnectAsync().ConfigureAwait(false); Dispose(); }

    /// <summary>HRIF_ReadRobotState 的不可变快照，避免不同调用点重复使用容易错位的 13 个 ref 参数。</summary>
    private sealed record RobotStateSnapshot(bool Moving, bool Enabled, bool HasError, int ErrorCode,
        int ErrorAxis, bool BrakeReleased, bool Paused, bool EmergencyStop, bool SafeGuard,
        bool Electrified, bool ConnectedToBox, bool BlendingDone, bool InPosition);
}
