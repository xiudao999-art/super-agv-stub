using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Actions.ServerActions;
using Kunling.RobotClient.Core.Controller.Templates;

namespace WinFormsApp1.Net;

/// <summary>调度侧机器人 Action TCP 服务端，一行一个 UTF-8 JSON 消息。</summary>
public sealed class TcpServer : IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<string, RobotSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SentCommandInfo> _sentCommands = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public bool IsRunning => _listener is not null;
    public IReadOnlyCollection<RobotSessionInfo> Robots => _sessions.Values.Select(x => x.ToInfo()).ToArray();
    public event EventHandler<string>? Log;
    public event EventHandler? RobotsChanged;
    public event EventHandler? ServerStopped;
    public event EventHandler<ActionAttentionEventArgs>? ActionAttentionRequired;

    public void Start(int port)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _acceptTask = AcceptLoopAsync(_cts.Token);
        WriteLog($"服务端已启动，监听 0.0.0.0:{port}");
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        if (listener is null) return;
        _listener = null;
        if (_cts is not null) await _cts.CancelAsync();
        listener.Stop();
        foreach (var session in _sessions.Values) await session.DisposeAsync();
        _sessions.Clear();
        if (_acceptTask is not null)
        {
            try { await _acceptTask; } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        RobotsChanged?.Invoke(this, EventArgs.Empty);
        WriteLog("服务端已停止");
        ServerStopped?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string> SendCommandAsync(string robotId, string actionType, string actionVersion,
        ExecutionMode executionMode, string inputJson, int timeoutMs,
        CancellationToken cancellationToken = default, string? reuseActionInstanceId = null)
    {
        if (!_sessions.TryGetValue(robotId, out var session)) throw new InvalidOperationException("机器人未在线。");
        if (!session.Capabilities.Any(x => x.ActionType.Equals(actionType, StringComparison.OrdinalIgnoreCase) && x.ActionVersion == actionVersion))
            throw new InvalidOperationException($"机器人未注册能力 {actionType}@{actionVersion}。");

        using var inputDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
        var mainAction = inputDoc.RootElement.Deserialize<MainActionMessage>(ServerActionJson.Default)?.MainAction
            ?? throw new InvalidDataException("input.MainAction 不能为空。");
        if (string.IsNullOrWhiteSpace(reuseActionInstanceId))
            MainActionTemplateValidator.EnsureValid(mainAction);
        else
        {
            var resumeErrors = MainActionTemplateValidator.ValidateResume(mainAction);
            if (resumeErrors.Count > 0) throw new InvalidDataException(string.Join(" ", resumeErrors));
        }
        using var configDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(reuseActionInstanceId)
            ? "{}" : "{\"resume\":true}");
        var actionId = string.IsNullOrWhiteSpace(reuseActionInstanceId)
            ? Guid.NewGuid().ToString("N") : reuseActionInstanceId;
        var command = new ActionCommand("1.0", ServerMessageTypes.Command, Guid.NewGuid().ToString("N"),
            session.SessionId, robotId, actionId, Guid.NewGuid().ToString("N"), null, null,
            null, actionVersion, executionMode, configDoc.RootElement.Clone(), inputDoc.RootElement.Clone(),
            timeoutMs, DateTimeOffset.UtcNow);
        await session.SendAsync(command, cancellationToken);
        var sentInfo = new SentCommandInfo(robotId, actionType, actionVersion, executionMode,
            inputJson, timeoutMs);
        // Phase 重试复用同一个 actionInstanceId 时保留首次完整 MainAction，不能被剩余 Phase 覆盖。
        if (string.IsNullOrWhiteSpace(reuseActionInstanceId)) _sentCommands[actionId] = sentInfo;
        else _sentCommands.TryAdd(actionId, sentInfo);
        WriteLog($"[{robotId}] COMMAND JSON: {JsonSerializer.Serialize(command, ServerActionJson.Default)}");
        WriteLog($"[{robotId}] 下发 {actionType}，actionInstanceId={actionId}");
        return actionId;
    }

    /// <summary>
    /// 从失败 phase 开始断点续跑：已经成功的前置 phase 不再下发，失败 phase 成功后
    /// 继续执行其后的剩余 phase，并保持原 actionInstanceId。
    /// </summary>
    public Task<string> RetryCommandAsync(string actionInstanceId, string phaseId,
        CancellationToken cancellationToken = default)
    {
        if (!_sentCommands.TryGetValue(actionInstanceId, out var sent))
            throw new InvalidOperationException($"找不到动作 {actionInstanceId} 的原始下发内容。");
        if (string.IsNullOrWhiteSpace(phaseId))
            throw new InvalidOperationException("重试 phaseId 不能为空。禁止无断点地重跑整个 MainAction。");
        var retryInputJson = CreatePhaseResumeInput(sent.InputJson, phaseId);
        return SendCommandAsync(sent.RobotId, sent.ActionType, sent.ActionVersion, sent.ExecutionMode,
            retryInputJson, sent.TimeoutMs, cancellationToken, actionInstanceId);
    }

    /// <summary>BUSY 拒绝的动作从未开始过，因此允许在机器人空闲后完整重新下发。</summary>
    public Task<string> RetryRejectedCommandAsync(string actionInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (!_sentCommands.TryGetValue(actionInstanceId, out var sent))
            throw new InvalidOperationException($"找不到被拒绝请求 {actionInstanceId} 的原始内容。");
        return SendCommandAsync(sent.RobotId, sent.ActionType, sent.ActionVersion, sent.ExecutionMode,
            sent.InputJson, sent.TimeoutMs, cancellationToken);
    }

    public async Task TerminateActionAsync(string actionInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (!_sentCommands.TryGetValue(actionInstanceId, out var sent))
            throw new InvalidOperationException($"找不到动作 {actionInstanceId} 的原始内容。");
        if (!_sessions.TryGetValue(sent.RobotId, out var session))
            throw new InvalidOperationException("机器人未在线。");
        await session.SendAsync(new TerminateActionRequest("1.0", ServerMessageTypes.TerminateAction,
            Guid.NewGuid().ToString("N"), session.SessionId, sent.RobotId, actionInstanceId,
            DateTimeOffset.UtcNow), cancellationToken);
        WriteLog($"[{sent.RobotId}] 已通知机器人结束保留动作 actionInstanceId={actionInstanceId}");
    }

    private static string CreatePhaseResumeInput(string inputJson, string phaseId)
    {
        var root = JsonNode.Parse(inputJson)?.AsObject()
            ?? throw new InvalidDataException("原始 MainAction JSON 无效。");
        var mainAction = root["MainAction"]?.AsObject() ?? root["mainAction"]?.AsObject()
            ?? throw new InvalidDataException("原始命令缺少 MainAction。");
        var phases = mainAction["phases"]?.AsArray()
            ?? throw new InvalidDataException("原始 MainAction 缺少 phases。");
        // VERIFY_BEFORE_RETRY 可声明安全重入点。例如 verifyLoad 失败不能只重读传感器，
        // 必须按 retryFromPhaseId=toPick 回到取料位重新夹取。
        var requestedPhase = phases.FirstOrDefault(x => string.Equals(
            x?["phaseId"]?.GetValue<string>(), phaseId, StringComparison.OrdinalIgnoreCase));
        var retryFromPhaseId = requestedPhase?["params"]?["retryFromPhaseId"]?.GetValue<string>();
        var resumeFromPhaseId = string.IsNullOrWhiteSpace(retryFromPhaseId) ? phaseId : retryFromPhaseId;
        var start = -1;
        for (var i = 0; i < phases.Count; i++)
        {
            var currentId = phases[i]?["phaseId"]?.GetValue<string>();
            if (string.Equals(currentId, resumeFromPhaseId, StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                break;
            }
        }
        if (start < 0) throw new InvalidDataException($"原始 MainAction 中找不到安全重入 phase：{resumeFromPhaseId}");

        var remaining = new JsonArray();
        for (var i = start; i < phases.Count; i++) remaining.Add(phases[i]?.DeepClone());
        mainAction["phases"] = remaining;
        return root.ToJsonString(ServerActionJson.Default);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _listener is not null)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { WriteLog($"监听异常：{ex.Message}"); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCancellation)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        RobotSession? session = null;
        WriteLog($"TCP 已连接：{remote}，等待 REGISTER");
        try
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 16 * 1024, true);
            using var registerTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
            registerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var firstLine = await reader.ReadLineAsync(registerTimeout.Token) ?? throw new EndOfStreamException();
            var register = JsonSerializer.Deserialize<RegisterRequest>(firstLine, ServerActionJson.Default)
                ?? throw new InvalidDataException("REGISTER 内容为空。");
            if (register.MessageType != ServerMessageTypes.Register || string.IsNullOrWhiteSpace(register.RobotId))
                throw new InvalidDataException("首条消息必须是有效 REGISTER。");

            session = new RobotSession(client, stream, register, remote);
            if (_sessions.TryGetValue(register.RobotId, out var old)) await old.DisposeAsync();
            _sessions[register.RobotId] = session;
            var accepted = register.Capabilities.Select(x => new CapabilityDecision(x.ActionType, x.ActionVersion)).ToArray();
            var ack = new RegisterAck("1.0", ServerMessageTypes.RegisterAck, Guid.NewGuid().ToString("N"),
                register.MessageId, register.RobotId, true, session.SessionId, 30_000, 3_000,
                accepted, [], null, DateTimeOffset.UtcNow);
            await session.SendAsync(ack, serverCancellation);
            WriteLog($"[{register.RobotId}] 注册成功，能力：{string.Join(", ", accepted.Select(x => x.ActionType))}");
            RobotsChanged?.Invoke(this, EventArgs.Empty);

            while (!serverCancellation.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(serverCancellation);
                if (line is null) break;
                using var document = JsonDocument.Parse(line);
                var type = document.RootElement.GetProperty("messageType").GetString();
                if (type == ServerMessageTypes.Ping)
                {
                    var ping = document.RootElement.Deserialize<PingMessage>(ServerActionJson.Default)!;
                    session.LastSeen = DateTimeOffset.UtcNow;
                    session.Snapshot = ping.Snapshot;
                    session.State = ping.Snapshot.State;
                    await session.SendAsync(new PongMessage("1.0", ServerMessageTypes.Pong, Guid.NewGuid().ToString("N"),
                        ping.MessageId, session.SessionId, ping.Sequence, DateTimeOffset.UtcNow), serverCancellation);
                    RobotsChanged?.Invoke(this, EventArgs.Empty);
                }
                else if (type is ServerMessageTypes.ActionEvent or ServerMessageTypes.ActionStatus)
                {
                    var actionEvent = document.RootElement.Deserialize<ActionEvent>(ServerActionJson.Default)!;
                    var report = actionEvent.ReportState;
                    session.LastSeen = DateTimeOffset.UtcNow;
                    session.State = report?.RobotState ?? actionEvent.State.ToString();
                    RobotsChanged?.Invoke(this, EventArgs.Empty);
                    WriteLog(report is null
                        ? $"[{session.RobotId}] {actionEvent.ActionInstanceId} => {actionEvent.State}" +
                          (actionEvent.Error is null ? "" : $"，错误 {actionEvent.Error.Code}: {actionEvent.Error.Message}")
                        : $"[{report.RobotName}] robot={report.RobotState} " +
                          $"action={report.MainAction.Name}/{report.MainAction.State} " +
                          $"actionInstanceId={report.ActionInstanceId} " +
                          $"subAction={report.SubAction?.Name ?? "-"}/{report.SubAction?.State ?? "-"}" +
                          (report.SubAction?.Code is null ? "" :
                              $" code={report.SubAction.Code} msg={report.SubAction.Msg}"));
                    if (actionEvent.State is MainActionState.Busy or MainActionState.Error or MainActionState.Hang)
                        ActionAttentionRequired?.Invoke(this, new ActionAttentionEventArgs(session.RobotId, actionEvent));
                }
                else WriteLog($"[{session.RobotId}] 收到 {type ?? "未知消息"}");
            }
        }
        catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested) { }
        catch (Exception ex) { WriteLog($"[{remote}] 会话异常：{ex.Message}"); }
        finally
        {
            if (session is not null && _sessions.TryGetValue(session.RobotId, out var current) && ReferenceEquals(current, session))
                _sessions.TryRemove(session.RobotId, out _);
            client.Dispose();
            WriteLog($"TCP 已断开：{remote}");
            RobotsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void WriteLog(string message) => Log?.Invoke(this, message);
    public void Dispose() => StopAsync().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class RobotSession(TcpClient client, NetworkStream stream, RegisterRequest register, string remote) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public string RobotId => register.RobotId;
        public IReadOnlyList<ActionCapability> Capabilities => register.Capabilities;
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
        public RobotStateSnapshot Snapshot { get; set; } = register.Snapshot;
        public string State { get; set; } = register.Snapshot.State;
        public RobotSessionInfo ToInfo() => new(RobotId, remote, SessionId, register.RobotType,
            string.Join(", ", Capabilities.Select(x => x.ActionType)), State, LastSeen);
        public async Task SendAsync(object message, CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ServerActionJson.Default);
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            finally { _sendLock.Release(); }
        }
        public ValueTask DisposeAsync() { client.Dispose(); _sendLock.Dispose(); return ValueTask.CompletedTask; }
    }
}

public sealed record SentCommandInfo(string RobotId, string ActionType, string ActionVersion,
    ExecutionMode ExecutionMode, string InputJson, int TimeoutMs);

public sealed record ActionAttentionEventArgs(string RobotId, ActionEvent ActionEvent);

public sealed record RobotSessionInfo(string RobotId, string RemoteEndPoint, string SessionId,
    string RobotType, string Capabilities, string State, DateTimeOffset LastSeen);
