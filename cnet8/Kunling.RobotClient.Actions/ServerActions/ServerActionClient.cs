using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Controller.ReportStateModels;

namespace Kunling.RobotClient.Actions.ServerActions;

public sealed class ServerActionClient : IAsyncDisposable
{
    // 发送锁用于保护同一个 Socket，确保并发任务发送的 JSON 不会互相穿插。
    private readonly ServerActionOptions _options;
    private readonly ServerActionRegistration _registration;
    private readonly IServerActionExecutor _executor;
    private readonly IRobotSnapshotProvider _snapshotProvider;
    private readonly ServerActionJournal _journal;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _clientInstanceId = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private NetworkStream? _stream;
    private string? _sessionId;
    private long _eventSequence;
    private long _heartbeatSequence;
    private DateTimeOffset _lastPongAt;
    private HashSet<string> _acceptedCapabilities = new(StringComparer.OrdinalIgnoreCase);
    private ActionCommand? _activeCommand;
    // 单机器人客户端只保留一个未结束的 MainAction；HANG/可恢复 ERROR 时持续占有该上下文。
    private ActionCommand? _retainedMainAction;

    public ServerActionClient(
        ServerActionOptions options,
        ServerActionRegistration registration,
        IServerActionExecutor executor,
        IRobotSnapshotProvider snapshotProvider,
        ServerActionJournal? journal = null)
    {
        _options = options;
        _registration = registration;
        _executor = executor;
        _snapshotProvider = snapshotProvider;
        _journal = journal ?? new ServerActionJournal();
    }

    public bool IsRunning => _runTask is { IsCompleted: false };
    public bool IsRegistered => !string.IsNullOrWhiteSpace(_sessionId);
    public string? SessionId => _sessionId;
    public event EventHandler<string>? LogReceived;
    public event EventHandler<bool>? RegistrationChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // 只启动后台连接任务，不阻塞 WinForms UI 线程。
        if (IsRunning) return Task.CompletedTask;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunReconnectLoopAsync(_runCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // 取消连接、心跳和监听任务，并清除当前服务器会话。
        if (_runCts is null) return;
        await _runCts.CancelAsync();
        if (_runTask is not null)
        {
            try { await _runTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
        _runCts.Dispose();
        _runCts = null;
        _runTask = null;
        SetSession(null);
    }

    private async Task RunReconnectLoopAsync(CancellationToken cancellationToken)
    {
        // 非主动停止导致的断线会按配置间隔自动重连。
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RunSessionAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException ex)
            {
                Log($"Socket 连接失败 {_options.Host}:{_options.Port}：{ex.Message}；{_options.ReconnectDelayMs}ms 后重试");
            }
            catch (Exception ex) { Log($"服务器会话中断：{ex.Message}；{_options.ReconnectDelayMs}ms 后重试"); }
            finally { _stream = null; SetSession(null); }

            await Task.Delay(_options.ReconnectDelayMs, cancellationToken);
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        // 每次 TCP 连接都是新会话，必须先完成 REGISTER 才能接收动作。
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(_options.ConnectTimeoutMs);
        using var tcpClient = new TcpClient { NoDelay = true };
        Log($"正在连接服务器 {_options.Host}:{_options.Port}");
        await tcpClient.ConnectAsync(_options.Host, _options.Port, connectTimeout.Token);
        await using var stream = tcpClient.GetStream();
        _stream = stream;
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);

        var ack = await RegisterAsync(reader, stream, cancellationToken);
        if (!ack.Accepted || string.IsNullOrWhiteSpace(ack.SessionId))
            throw new InvalidOperationException($"服务器拒绝注册：{ack.Reason ?? "未提供原因"}");

        SetSession(ack.SessionId);
        _acceptedCapabilities = (ack.AcceptedCapabilities ?? [])
            .Select(x => $"{x.ActionType}@{x.ActionVersion}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _lastPongAt = DateTimeOffset.UtcNow;
        Log($"注册成功，sessionId={ack.SessionId}");
        var heartbeatMs = ack.HeartbeatIntervalMs > 0 ? ack.HeartbeatIntervalMs : _options.DefaultHeartbeatMs;

        // 收包和心跳共享生命周期，连接失效后一起退出并由外层重新注册。
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatLoopAsync(stream, heartbeatMs, sessionCts.Token);
        try { await ReceiveLoopAsync(reader, stream, sessionCts.Token); }
        finally
        {
            await sessionCts.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
    }

    private async Task<RegisterAck> RegisterAsync(StreamReader reader, NetworkStream stream, CancellationToken cancellationToken)
    {
        // 将机器人、设备和 Action 能力注册给服务器，供服务器建立调用路由。
        var messageId = NewMessageId();
        var request = new RegisterRequest(
            "1.0", ServerMessageTypes.Register, messageId, _clientInstanceId,
            _options.RobotId, _options.RobotType, _options.ClientVersion, _options.ProtocolVersion,
            _registration.Devices, _registration.ExecutionModes, _registration.Capabilities,
            await _snapshotProvider.GetSnapshotAsync(cancellationToken), DateTimeOffset.UtcNow);

        await SendAsync(stream, request, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RegisterTimeoutMs);
        var line = await ReadLineAsync(reader, timeout.Token);
        using var document = JsonDocument.Parse(line);
        EnsureMessageType(document.RootElement, ServerMessageTypes.RegisterAck);
        var ack = document.RootElement.Deserialize<RegisterAck>(ServerActionJson.Default)
            ?? throw new InvalidDataException("REGISTER_ACK为空");
        if (!string.Equals(ack.ReplyTo, messageId, StringComparison.Ordinal))
            throw new InvalidDataException("REGISTER_ACK replyTo不匹配");
        return ack;
    }

    private async Task ReceiveLoopAsync(StreamReader reader, NetworkStream stream, CancellationToken cancellationToken)
    {
        // 协议采用“一行一个 JSON”，根据 messageType 分发消息。
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await ReadLineAsync(reader, cancellationToken);
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var messageType = root.TryGetProperty("messageType", out var type) ? type.GetString() : null;
            switch (messageType)
            {
                case ServerMessageTypes.Pong:
                    _lastPongAt = DateTimeOffset.UtcNow;
                    break;
                case ServerMessageTypes.Command:
                    Log($"收到 COMMAND JSON: {line}");
                    var command = root.Deserialize<ActionCommand>(ServerActionJson.Default)
                        ?? throw new InvalidDataException("COMMAND为空");
                    // 不阻塞收包；设备动作仍由动作锁保证串行执行。
                    _ = ExecuteCommandAsync(stream, command, cancellationToken);
                    break;
                case ServerMessageTypes.QueryAction:
                    var query = root.Deserialize<QueryActionRequest>(ServerActionJson.Default)
                        ?? throw new InvalidDataException("QUERY_ACTION为空");
                    await ReplyActionStatusAsync(stream, query, cancellationToken);
                    break;
                case ServerMessageTypes.TerminateAction:
                    var terminate = root.Deserialize<TerminateActionRequest>(ServerActionJson.Default)
                        ?? throw new InvalidDataException("TERMINATE_ACTION为空");
                    if (ValidateSession(terminate.SessionId) &&
                        terminate.RobotId.Equals(_options.RobotId, StringComparison.OrdinalIgnoreCase) &&
                        Volatile.Read(ref _retainedMainAction)?.ActionInstanceId.Equals(
                            terminate.ActionInstanceId, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Volatile.Write(ref _retainedMainAction, null);
                        if (_snapshotProvider is IRobotActivitySnapshotProvider activity)
                            activity.SetCurrentAction(null);
                        Log($"服务器结束保留动作：{terminate.ActionInstanceId}");
                    }
                    break;
                default:
                    Log($"忽略未知消息类型：{messageType ?? "<null>"}");
                    break;
            }
        }
    }

    private async Task ExecuteCommandAsync(NetworkStream stream, ActionCommand command, CancellationToken sessionCancellation)
    {
        // 命令必须属于当前会话和当前机器人，防止串台执行。
        if (!ValidateSession(command.SessionId) || !string.Equals(command.RobotId, _options.RobotId, StringComparison.OrdinalIgnoreCase))
        {
            await SendTerminalEventAsync(stream, command, ServerActionExecutionResult.Failed(PlatformErrorCodes.SessionOrRobotMismatch, "sessionId或robotId不匹配"), sessionCancellation);
            return;
        }

        var retained = Volatile.Read(ref _retainedMainAction);
        var isPhaseResume = retained is not null &&
            retained.ActionInstanceId.Equals(command.ActionInstanceId, StringComparison.OrdinalIgnoreCase);
        if (!isPhaseResume && _journal.TryGet(command.ActionInstanceId, out var historical) && historical is not null)
        {
            // actionInstanceId 是幂等键，重复命令只重放历史结果，不再次驱动设备。
            Log($"动作幂等命中：{command.ActionInstanceId}");
            await SendAsync(stream, historical with { MessageId = NewMessageId(), Sequence = NextEventSequence(), Timestamp = DateTimeOffset.UtcNow }, sessionCancellation);
            return;
        }
        if (isPhaseResume) _journal.Remove(command.ActionInstanceId);

        string actionType;
        try { actionType = ReadMainActionType(command.Input); }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            await SendTerminalEventAsync(stream, command,
                ServerActionExecutionResult.Failed(PlatformErrorCodes.InvalidActionInput, ex.Message), sessionCancellation);
            return;
        }

        if (!_acceptedCapabilities.Contains($"{actionType}@{command.ActionVersion}") ||
            !_executor.CanExecute(actionType, command.ActionVersion, command.ExecutionMode))
        {
            await SendTerminalEventAsync(stream, command, ServerActionExecutionResult.Failed(PlatformErrorCodes.SessionOrRobotMismatch, $"客户端未认证动作 {actionType}@{command.ActionVersion}"), sessionCancellation);
            return;
        }

        if (!_inFlight.TryAdd(command.ActionInstanceId, 0)) return;
        // HANG 主动作仍保留在客户端单例上下文中；仅相同 actionInstanceId 可以断点恢复。
        if (retained is not null && !isPhaseResume)
        {
            var retainedType = ReadMainActionType(retained.Input);
            var retainedState = await _executor.QueryAsync(retained.ActionInstanceId,
                retained.DeviceCommandId, sessionCancellation);
            var retainedStep = retainedState.ResolvedSteps?.LastOrDefault();
            var busy = ServerActionExecutionResult.Busy(
                $"机器人保留未结束动作 {retainedType}，actionInstanceId={retained.ActionInstanceId}。",
                new ActionFailureContext(retained.ActionInstanceId, retainedType,
                    PhaseId: retainedStep?.PhaseId, SubAction: retainedStep?.SubAction,
                    UserChoices: ["TERMINATE_REQUEST"], MainActionState: retainedState.State,
                    SubActionState: retainedStep?.State));
            await SendTerminalEventAsync(stream, command, busy, sessionCancellation);
            _inFlight.TryRemove(command.ActionInstanceId, out _);
            return;
        }
        // 不把新动作静默排队：机器人被占用时立即回复 BUSY，并带回当前动作信息。
        if (!await _actionLock.WaitAsync(0, sessionCancellation))
        {
            var active = Volatile.Read(ref _activeCommand);
            var activeType = active is null ? "UNKNOWN" : ReadMainActionType(active.Input);
            ServerActionQueryResult? activeState = null;
            if (active is not null)
                activeState = await _executor.QueryAsync(active.ActionInstanceId,
                    active.DeviceCommandId, sessionCancellation);
            var activeStep = activeState?.ResolvedSteps?.LastOrDefault();
            var busy = ServerActionExecutionResult.Busy(
                active is null ? "机器人正在执行其他动作。" :
                    $"机器人正在执行 {activeType}，actionInstanceId={active.ActionInstanceId}。",
                new ActionFailureContext(active?.ActionInstanceId ?? string.Empty, activeType,
                    PhaseId: activeStep?.PhaseId, SubAction: activeStep?.SubAction,
                    UserChoices: ["RETRY_LATER", "TERMINATE_REQUEST"],
                    MainActionState: activeState?.State ?? MainActionState.Running,
                    SubActionState: activeStep?.State ?? "RUNNING"));
            await SendTerminalEventAsync(stream, command, busy, sessionCancellation);
            _inFlight.TryRemove(command.ActionInstanceId, out _);
            return;
        }
        ServerActionExecutionResult? completedResult = null;
        try
        {
            Volatile.Write(ref _activeCommand, command);
            // 动作进入串行执行区后立即切换为 EXECUTING；后续每个 3 秒心跳都会携带该状态。
            if (_snapshotProvider is IRobotActivitySnapshotProvider activitySnapshot)
                activitySnapshot.SetCurrentAction(command.ActionInstanceId,
                    isPhaseResume ? "RECOVERING" : "EXECUTING");

            // 标准状态顺序：ACCEPTED -> RUNNING -> 终态。
            if (!isPhaseResume)
                await SendEventAsync(stream, command, MainActionState.Accepted, null, null, null, sessionCancellation);
            await SendEventAsync(stream, command, MainActionState.Running, null, null, null, sessionCancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation);
            timeout.CancelAfter(Math.Max(1_000, command.TimeoutMs));
            var context = new ExecutionContext(this, stream, command);
            ServerActionExecutionResult result;
            try { result = await _executor.ExecuteAsync(context, timeout.Token); }
            catch (OperationCanceledException) when (!sessionCancellation.IsCancellationRequested)
            {
                result = ServerActionExecutionResult.Unknown(PlatformErrorCodes.ActionStateUnknown, "动作执行超时，物理结果待查询");
            }
            catch (Exception ex) { result = ServerActionExecutionResult.Unknown(PlatformErrorCodes.InternalExecutionError, ex.Message); }
            completedResult = result;
            await SendTerminalEventAsync(stream, command, result, sessionCancellation);
        }
        finally
        {
            var keepForRecovery = completedResult?.State is MainActionState.Hang ||
                                  completedResult?.State == MainActionState.Error &&
                                  completedResult.Error?.Retryable == true;
            if (keepForRecovery)
            {
                // 首次失败时缓存完整 MainAction；恢复命令只包含剩余 phases，不能覆盖完整快照。
                if (retained is null) Volatile.Write(ref _retainedMainAction, command);
                if (_snapshotProvider is IRobotActivitySnapshotProvider activitySnapshot)
                    activitySnapshot.SetCurrentAction(command.ActionInstanceId,
                        completedResult!.State.ToString().ToUpperInvariant());
            }
            else
            {
                Volatile.Write(ref _retainedMainAction, null);
                if (_snapshotProvider is IRobotActivitySnapshotProvider activitySnapshot)
                    activitySnapshot.SetCurrentAction(null);
            }

            Volatile.Write(ref _activeCommand, null);
            _actionLock.Release();
            _inFlight.TryRemove(command.ActionInstanceId, out _);
        }
    }

    private static string ReadMainActionType(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("MainAction", out var mainAction) ||
            mainAction.ValueKind != JsonValueKind.Object ||
            !mainAction.TryGetProperty("actionType", out var actionType) ||
            actionType.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(actionType.GetString()))
            throw new ArgumentException("COMMAND.input.MainAction.actionType 不能为空。");
        return actionType.GetString()!;
    }

    private async Task ReplyActionStatusAsync(NetworkStream stream, QueryActionRequest query, CancellationToken cancellationToken)
    {
        // 优先返回幂等日志；没有历史终态时再向具体设备执行器查询。
        if (_journal.TryGet(query.ActionInstanceId, out var saved) && saved is not null)
        {
            await SendAsync(stream, saved with { MessageType = ServerMessageTypes.ActionStatus, MessageId = NewMessageId(), Sequence = NextEventSequence(), Timestamp = DateTimeOffset.UtcNow }, cancellationToken);
            return;
        }

        var queried = await _executor.QueryAsync(query.ActionInstanceId, query.DeviceCommandId, cancellationToken);
        var report = CreateReportState(query.ActionInstanceId,
            queried.Error?.Context?.ActionType ?? "UNKNOWN", queried.State,
            queried.ResolvedSteps, queried.Error);
        var status = new ActionEvent("1.0", ServerMessageTypes.ActionStatus, NewMessageId(), _sessionId!, _options.RobotId,
            query.ActionInstanceId, query.DeviceCommandId, NextEventSequence(), queried.State, queried.ResolvedSteps,
            queried.PhysicalResult, queried.Error, DateTimeOffset.UtcNow, report);
        await SendAsync(stream, status, cancellationToken);
    }

    private async Task HeartbeatLoopAsync(NetworkStream stream, int heartbeatMs, CancellationToken cancellationToken)
    {
        // 连续三个周期没有收到 PONG，判定会话失效并触发重新连接。
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1_000, heartbeatMs)));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (DateTimeOffset.UtcNow - _lastPongAt > TimeSpan.FromMilliseconds(heartbeatMs * 3L))
                throw new TimeoutException("连续3个心跳周期未收到PONG");
            var ping = new PingMessage("1.0", ServerMessageTypes.Ping, NewMessageId(), _sessionId!, _options.RobotId,
                Interlocked.Increment(ref _heartbeatSequence), await _snapshotProvider.GetSnapshotAsync(cancellationToken), DateTimeOffset.UtcNow);
            await SendAsync(stream, ping, cancellationToken);
        }
    }

    private async ValueTask SendRunningAsync(NetworkStream stream, ActionCommand command, IReadOnlyList<ResolvedStep>? steps, JsonElement? evidence, CancellationToken cancellationToken) =>
        await SendEventAsync(stream, command, MainActionState.Running, steps, evidence, null, cancellationToken);

    private async Task SendTerminalEventAsync(NetworkStream stream, ActionCommand command, ServerActionExecutionResult result, CancellationToken cancellationToken)
    {
        // 终态先保存再发送，使服务器重发命令时能够安全恢复结果。
        var actionEvent = CreateEvent(command, result.State, result.ResolvedSteps, result.PhysicalResult, result.Error);
        _journal.Save(actionEvent);
        await SendAsync(stream, actionEvent, cancellationToken);
    }

    private Task SendEventAsync(NetworkStream stream, ActionCommand command, MainActionState state,
        IReadOnlyList<ResolvedStep>? steps, JsonElement? physicalResult, ActionError? error, CancellationToken cancellationToken) =>
        SendAsync(stream, CreateEvent(command, state, steps, physicalResult, error), cancellationToken);

    private ActionEvent CreateEvent(ActionCommand command, MainActionState state, IReadOnlyList<ResolvedStep>? steps, JsonElement? physicalResult, ActionError? error) =>
        new("1.0", ServerMessageTypes.ActionEvent, NewMessageId(), _sessionId!, _options.RobotId,
            command.ActionInstanceId, command.DeviceCommandId, NextEventSequence(), state, steps, physicalResult, error,
            DateTimeOffset.UtcNow, CreateReportState(command.ActionInstanceId,
                ReadMainActionType(command.Input), state, steps, error));

    /// <summary>把分散的执行现场统一封装为服务器直接消费的状态模型。</summary>
    private ReportRobotStateModel CreateReportState(string eventActionInstanceId, string actionType,
        MainActionState eventState, IReadOnlyList<ResolvedStep>? steps, ActionError? error)
    {
        var failure = error?.Context;
        var latestStep = steps?.LastOrDefault();
        var actionInstanceId = string.IsNullOrWhiteSpace(failure?.ActionInstanceId)
            ? eventActionInstanceId : failure.ActionInstanceId;
        var mainState = failure?.MainActionState ??
                        (eventState == MainActionState.Busy ? MainActionState.Running : eventState);
        var subActionName = failure?.SubAction ?? latestStep?.SubAction;
        var subAction = string.IsNullOrWhiteSpace(subActionName) ? null : new ReportSubActionStateModel(
            subActionName,
            failure?.SubActionState ?? latestStep?.State ?? "RUNNING",
            failure?.PhaseId ?? latestStep?.PhaseId,
            failure?.OnFail?.ToString(),
            error is null ? null : error.DeviceCode ?? error.Code.ToString(),
            error?.Message,
            error?.Detail);
        var robotState = mainState switch
        {
            MainActionState.Accepted or MainActionState.Running => "EXECUTING",
            MainActionState.Hang => "HANG",
            MainActionState.Error => "ERROR",
            _ => "IDLE"
        };
        return new ReportRobotStateModel(_options.RobotId, robotState, actionInstanceId,
            new ReportMainActionStateModel(failure?.ActionType ?? actionType, mainState),
            subAction, DateTimeOffset.UtcNow);
    }

    private async Task SendAsync(NetworkStream stream, object message, CancellationToken cancellationToken)
    {
        // 所有出站消息统一序列化，并使用换行符作为 TCP 消息边界。
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, ServerActionJson.Default);
        if (payload.Length > _options.MaxMessageBytes) throw new InvalidDataException($"消息超过上限：{payload.Length}");
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(payload, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally { _sendLock.Release(); }
    }

    private async Task<string> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException("服务器关闭连接");
        if (Encoding.UTF8.GetByteCount(line) > _options.MaxMessageBytes) throw new InvalidDataException("接收消息超过上限");
        return line;
    }

    private bool ValidateSession(string sessionId) => !string.IsNullOrWhiteSpace(_sessionId) && string.Equals(_sessionId, sessionId, StringComparison.Ordinal);
    private long NextEventSequence() => Interlocked.Increment(ref _eventSequence);
    private static string NewMessageId() => Guid.NewGuid().ToString("N");
    private static void EnsureMessageType(JsonElement root, string expected)
    {
        var actual = root.TryGetProperty("messageType", out var type) ? type.GetString() : null;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"期望{expected}，实际{actual ?? "<null>"}");
    }
    private void SetSession(string? sessionId) { _sessionId = sessionId; RegistrationChanged?.Invoke(this, sessionId is not null); }
    private void Log(string message) => LogReceived?.Invoke(this, message);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sendLock.Dispose();
        _actionLock.Dispose();
    }

    private sealed class ExecutionContext(ServerActionClient owner, NetworkStream stream, ActionCommand command) : IServerActionExecutionContext
    {
        public ActionCommand Command => command;
        public ValueTask ReportRunningAsync(IReadOnlyList<ResolvedStep>? steps = null, JsonElement? evidence = null, CancellationToken cancellationToken = default) =>
            owner.SendRunningAsync(stream, command, steps, evidence, cancellationToken);
    }
}
