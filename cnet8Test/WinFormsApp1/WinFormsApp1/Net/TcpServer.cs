using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kunling.RobotClient.Actions.ServerActions;
using Kunling.RobotClient.Core.Controller.Templates;

namespace WinFormsApp1.Net;

/// <summary>调度侧机器人 Action TCP 服务端，一行一个 UTF-8 JSON 消息。</summary>
public sealed class TcpServer : IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<string, RobotSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public bool IsRunning => _listener is not null;
    public IReadOnlyCollection<RobotSessionInfo> Robots => _sessions.Values.Select(x => x.ToInfo()).ToArray();
    public event EventHandler<string>? Log;
    public event EventHandler? RobotsChanged;
    public event EventHandler? ServerStopped;

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
        ExecutionMode executionMode, string inputJson, int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(robotId, out var session)) throw new InvalidOperationException("机器人未在线。");
        if (!session.Capabilities.Any(x => x.ActionType.Equals(actionType, StringComparison.OrdinalIgnoreCase) && x.ActionVersion == actionVersion))
            throw new InvalidOperationException($"机器人未注册能力 {actionType}@{actionVersion}。");

        using var inputDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
        var mainAction = inputDoc.RootElement.Deserialize<MainActionMessage>(ServerActionJson.Default)?.MainAction
            ?? throw new InvalidDataException("input.MainAction 不能为空。");
        MainActionTemplateValidator.EnsureValid(mainAction);
        using var configDoc = JsonDocument.Parse("{}");
        var actionId = Guid.NewGuid().ToString("N");
        var command = new ActionCommand("1.0", ServerMessageTypes.Command, Guid.NewGuid().ToString("N"),
            session.SessionId, robotId, actionId, Guid.NewGuid().ToString("N"), null, null,
            null, actionVersion, executionMode, configDoc.RootElement.Clone(), inputDoc.RootElement.Clone(),
            timeoutMs, DateTimeOffset.UtcNow);
        await session.SendAsync(command, cancellationToken);
        WriteLog($"[{robotId}] COMMAND JSON: {JsonSerializer.Serialize(command, ServerActionJson.Default)}");
        WriteLog($"[{robotId}] 下发 {actionType}，actionInstanceId={actionId}");
        return actionId;
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
                    session.LastSeen = DateTimeOffset.UtcNow;
                    session.State = actionEvent.State.ToString();
                    RobotsChanged?.Invoke(this, EventArgs.Empty);
                    WriteLog($"[{session.RobotId}] {actionEvent.ActionInstanceId} => {actionEvent.State}" +
                             (actionEvent.Error is null ? "" : $"，错误 {actionEvent.Error.Code}: {actionEvent.Error.Message}"));
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

public sealed record RobotSessionInfo(string RobotId, string RemoteEndPoint, string SessionId,
    string RobotType, string Capabilities, string State, DateTimeOffset LastSeen);
