using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Kunling.RobotClient.Devices.HikvisionMobileRobot;

/// <summary>
/// 海康基础卷与潜伏设备卷的统一通信实例。
/// <para>一个实例只维护一个Socket、一个发送序号、一个接收循环和一个请求等待表，避免重复注册和回复串包。</para>
/// <para>
/// 所有协议信令均可通过 <see cref="SendRawAsync"/> 或 <see cref="SendJsonAsync{TRequest,TResponse}"/> 调用；
/// 常用业务接口提供具名方法，保留及后续扩展信令无需修改连接层。
/// </para>
/// </summary>
public sealed class HikvisionMobileRobot : IAsyncDisposable
{
    private readonly HikvisionMobileRobotOptions _options;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, PendingRequest> _pending = new();
    private Socket? _socket;
    private Socket? _listener;
    private Task? _acceptTask;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private IPEndPoint? _remoteEndPoint;
    private int _sequence;
    private IPAddress? _configuredRemoteAddress;
    private DateTimeOffset _lastStateAt;
    private bool _disposed;
    private volatile bool _registered;
    private TaskCompletionSource<bool> _registration = NewRegistrationSource();

    public HikvisionMobileRobot(HikvisionMobileRobotOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    /// <summary>收到设备主动上报、申请或未匹配回复时触发。事件处理程序不应阻塞接收循环。</summary>
    public event EventHandler<HikvisionMessage>? MessageReceived;

    /// <summary>连接或接收循环异常通知；调用方可记录告警，连接会在下次发送前重新建立。</summary>
    public event EventHandler<Exception>? ConnectionFaulted;

    public bool IsConnected => _socket is not null && (_options.Transport == HikvisionTransport.Udp || _socket.Connected);
    public bool IsRegistered
    {
        get
        {
            if (!_registered) return false;
            if (_lastStateAt != default && DateTimeOffset.UtcNow - _lastStateAt > TimeSpan.FromMilliseconds(_options.HeartbeatTimeoutMs))
            {
                InvalidateRegistration("状态心跳超时");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 启动 RCS 监听端。UDP 绑定固定端口接收 AGV 数据，TCP 则监听并接受 AGV 主动连接。
    /// 海康设备是注册发起方，因此本方法不会主动连接机器人。
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if ((IsConnected && _receiveTask is { IsCompleted: false }) || _acceptTask is { IsCompleted: false }) return;
            await CloseSocketCoreAsync().ConfigureAwait(false);
            _remoteEndPoint = await ResolveEndPointAsync(_options.RemoteHost, _options.RemotePort, cancellationToken).ConfigureAwait(false);
            _configuredRemoteAddress = _remoteEndPoint.Address;
            var localAddress = await ResolveLocalAddressAsync(_options.LocalHost, cancellationToken).ConfigureAwait(false);
            var listener = new Socket(_remoteEndPoint.AddressFamily,
                _options.Transport == HikvisionTransport.Udp ? SocketType.Dgram : SocketType.Stream,
                _options.Transport == HikvisionTransport.Udp ? ProtocolType.Udp : ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(localAddress, _options.LocalPort));
            _receiveCts = new CancellationTokenSource();
            if (_options.Transport == HikvisionTransport.Tcp)
            {
                listener.Listen(1);
                _options.WriteLog("LISTENING", $"transport=TCP, local={listener.LocalEndPoint}；等待 AGV 主动连接");
                _listener = listener;
                _acceptTask = AcceptTcpAsync(listener, _receiveCts.Token);
            }
            else
            {
                _socket = listener;
                _receiveTask = ReceiveLoopAsync(_socket, _receiveCts.Token);
                _options.WriteLog("LISTENING", $"transport=UDP, local={_socket.LocalEndPoint}, configuredAgv={_remoteEndPoint}");
            }
        }
        finally { _lifecycleLock.Release(); }
    }

    /// <summary>发送任意二进制协议体并等待相同序号、请求信令+1的回复。</summary>
    public async Task<HikvisionResponse> SendRawAsync(ushort signal, ReadOnlyMemory<byte> body,
        HikvisionContentType contentType = HikvisionContentType.Binary,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var sequence = NextSequence();
        var waiter = new TaskCompletionSource<HikvisionMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = HikvisionSignals.ResponseOf(signal);
        if (!_pending.TryAdd(sequence, new(expected, waiter))) throw new InvalidOperationException($"海康消息序号重复：{sequence}。");
        try
        {
            var frame = HikvisionFrameCodec.Encode(signal, sequence, _options.ProtocolVersion, 0, contentType, body.Span);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeoutMs);
            var retryInterval = IsTaskControlSignal(signal) ? _options.TaskRetryIntervalMs : _options.AckRetryIntervalMs;
            var attempt = 0;
            while (!waiter.Task.IsCompleted)
            {
                timeout.Token.ThrowIfCancellationRequested();
                attempt++;
                await SendFrameAsync(frame, timeout.Token).ConfigureAwait(false);
                _options.WriteLog(attempt == 1 ? "SEND" : "RESEND",
                    $"signal=0x{signal:X4}, seq={sequence}, attempt={attempt}, bytes={frame.Length}, content={contentType}");
                var delay = Task.Delay(retryInterval, timeout.Token);
                await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
            }
            var response = await waiter.Task.ConfigureAwait(false);
            if (response.Signal != expected)
                throw new InvalidDataException($"海康回复信令不匹配：request=0x{signal:X4}, expected=0x{expected:X4}, actual=0x{response.Signal:X4}。");
            return new(response.Signal, response.Sequence, response.Body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"等待海康回复超时：signal=0x{signal:X4}, seq={sequence}。");
        }
        finally { _pending.TryRemove(sequence, out _); }
    }

    /// <summary>以协议支持的JSON字节流发送任意请求，并反序列化对应回复。</summary>
    public async Task<TResponse?> SendJsonAsync<TRequest, TResponse>(ushort signal, TRequest request,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(request, HikvisionJson.Options);
        var response = await SendRawAsync(signal, body, HikvisionContentType.Json, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TResponse>(response.Body.Span, HikvisionJson.Options);
    }

    /// <summary>回复设备主动发起的注册、资源申请或状态上报，序号必须沿用原请求序号。</summary>
    public async Task ReplyJsonAsync<T>(HikvisionMessage request, T response, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(response, HikvisionJson.Options);
        var frame = HikvisionFrameCodec.Encode(HikvisionSignals.ResponseOf(request.Signal), request.Sequence,
            _options.ProtocolVersion, 0, HikvisionContentType.Json, body);
        await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 以二进制结构体回复设备主动上报。状态上报ACK等报文必须沿用请求序号，不能进入新的请求等待表。
    /// </summary>
    public async Task ReplyRawAsync(HikvisionMessage request, ReadOnlyMemory<byte> body,
        HikvisionContentType contentType = HikvisionContentType.Binary,
        CancellationToken cancellationToken = default)
    {
        var frame = HikvisionFrameCodec.Encode(HikvisionSignals.ResponseOf(request.Signal), request.Sequence,
            _options.ProtocolVersion, 0, contentType, body.Span);
        await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    // 以下具名方法覆盖协议功能分类。DTO由项目按现场协议版本定义，连接层不会篡改字段名称或单位。
    public Task<TRes?> RegisterAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.Register, body, ct);
    public Task<TRes?> ConfigureStatusReportAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.ConfigureStatusReport, body, ct);
    public Task<TRes?> ConfigureAccuracyAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.ConfigureGlobalAccuracy, body, ct);
    public Task<TRes?> ConfigureAlarmAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.ConfigureAlarm, body, ct);
    public Task<TRes?> ConfigureNtpAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.ConfigureNtp, body, ct);
    public Task<TRes?> ConfigureMotionAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.ConfigureMotion, body, ct);
    public Task<TRes?> QueryCapabilityAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.QueryBasicCapability, body, ct);
    public Task<TRes?> MoveStraightAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.MoveStraight, body, ct);
    public Task<TRes?> MoveArcAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.MoveArc, body, ct);
    public Task<TRes?> MoveComplexPathAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.MoveComplexPath, body, ct);
    public Task<TRes?> DetectRackAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.DetectRack, body, ct);
    public Task<TRes?> LiftAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.LiftRack, body, ct);
    public Task<TRes?> LowerAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.LowerRack, body, ct);
    public Task<TRes?> RollerAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.ControlRoller, body, ct);
    public Task<TRes?> BatteryAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.ControlBattery, body, ct);
    public Task<TRes?> SwitchNavigationAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.SwitchNavigation, body, ct);
    public Task<TRes?> SwitchMapAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.SwitchMap, body, ct);
    public Task<TRes?> PauseAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.PauseTask, body, ct);
    public Task<TRes?> ContinueAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.ContinueTask, body, ct);
    public Task<TRes?> CancelAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.CancelTask, body, ct);
    public Task<TRes?> StopAsync<TReq, TRes>(TReq body, CancellationToken ct = default) => SendJsonAsync<TReq, TRes>(HikvisionSignals.Stop, body, ct);

    /// <summary>等待 AGV 主动注册成功。MOVE 等控制命令必须在注册完成后发送。</summary>
    public async Task WaitForRegistrationAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (IsRegistered) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeoutMs);
        try { await _registration.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"等待海康 AGV 注册超时，本地监听端口 {_options.LocalPort}。"); }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected || _acceptTask is { IsCompleted: false }) return;
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    private async Task AcceptTcpAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var accepted = await listener.AcceptAsync(ct).ConfigureAwait(false);
                var endpoint = (IPEndPoint?)accepted.RemoteEndPoint;
                if (endpoint is null || !IsConfiguredAddress(endpoint.Address))
                {
                    _options.WriteLog("REJECT", $"拒绝未配置TCP来源：{endpoint}");
                    accepted.Dispose();
                    continue;
                }
                _socket = accepted;
                _remoteEndPoint = endpoint;
                _options.WriteLog("CONNECTED", $"transport=TCP, local={accepted.LocalEndPoint}, remote={endpoint}");
                _receiveTask = ReceiveLoopAsync(accepted, ct);
                await _receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                FailAllPending(ex);
                InvalidateRegistration("TCP连接断开");
                ConnectionFaulted?.Invoke(this, ex);
                _options.WriteLog("FAULT", ex.Message);
            }
            finally
            {
                try { _socket?.Shutdown(SocketShutdown.Both); } catch { }
                _socket?.Dispose();
                _socket = null;
            }
            if (!ct.IsCancellationRequested) await Task.Delay(_options.ReconnectDelayMs, ct).ConfigureAwait(false);
        }
    }

    private async Task SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var socket = _socket ?? throw new IOException("海康Socket尚未监听或连接。");
            var sent = 0;
            do
            {
                sent += _options.Transport == HikvisionTransport.Udp
                    ? await socket.SendToAsync(frame[sent..], SocketFlags.None,
                        _remoteEndPoint ?? throw new IOException("尚未获得海康 AGV 远端地址。"), ct).ConfigureAwait(false)
                    : await socket.SendAsync(frame[sent..], SocketFlags.None, ct).ConfigureAwait(false);
            }
            while (sent < frame.Length && _options.Transport == HikvisionTransport.Tcp);
            if (sent != frame.Length) throw new IOException($"海康报文发送不完整：{sent}/{frame.Length}。");
        }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            if (_options.Transport == HikvisionTransport.Tcp) await ReceiveTcpAsync(socket, ct).ConfigureAwait(false);
            else await ReceiveUdpAsync(socket, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            FailAllPending(ex);
            ConnectionFaulted?.Invoke(this, ex);
            _options.WriteLog("FAULT", ex.Message);
        }
    }

    private async Task ReceiveTcpAsync(Socket socket, CancellationToken ct)
    {
        var header = new byte[HikvisionFrameCodec.HeaderSize];
        while (!ct.IsCancellationRequested)
        {
            await ReceiveExactlyAsync(socket, header, ct).ConfigureAwait(false);
            var length = HikvisionFrameCodec.ReadDeclaredLength(header);
            ValidateLength(length);
            var frame = new byte[length];
            header.CopyTo(frame, 0);
            await ReceiveExactlyAsync(socket, frame.AsMemory(header.Length), ct).ConfigureAwait(false);
            Dispatch(HikvisionFrameCodec.Decode(frame, _options.MaxFrameLength));
        }
    }

    private async Task ReceiveUdpAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[_options.MaxFrameLength];
        while (!ct.IsCancellationRequested)
        {
            EndPoint sender = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, ct).ConfigureAwait(false);
            var count = received.ReceivedBytes;
            if (count == 0) continue;
            var remote = (IPEndPoint)received.RemoteEndPoint;
            if (!IsConfiguredAddress(remote.Address))
            {
                _options.WriteLog("REJECT", $"忽略未配置UDP来源：{remote}");
                continue;
            }
            var message = HikvisionFrameCodec.Decode(buffer.AsSpan(0, count), _options.MaxFrameLength);
            // 仅在合法来源完成报文解码后更新端口；注册还会进一步验证设备编号。
            if (_registered || message.Signal == HikvisionSignals.Register) _remoteEndPoint = remote;
            Dispatch(message);
        }
    }

    private void Dispatch(HikvisionMessage message)
    {
        _options.WriteLog("RECEIVE", $"signal=0x{message.Signal:X4}, seq={message.Sequence}, bytes={message.Body.Length}");
        if (message.Signal == HikvisionSignals.ReportDeviceState) _lastStateAt = DateTimeOffset.UtcNow;
        if (_pending.TryGetValue(message.Sequence, out var pending) && message.Signal == pending.ExpectedSignal)
        {
            pending.Waiter.TrySetResult(message);
            return;
        }
        if (message.Signal == HikvisionSignals.Register)
        {
            _ = AcceptRegistrationAsync(message);
            return;
        }
        MessageReceived?.Invoke(this, message);
    }

    private async Task AcceptRegistrationAsync(HikvisionMessage request)
    {
        try
        {
            if (request.ContentType != HikvisionContentType.Binary || request.Body.Length < 68)
                throw new InvalidDataException($"海康注册请求格式无效：content={request.ContentType}, bytes={request.Body.Length}。");
            var deviceId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(request.Body.Span[..4]);
            if (_options.ExpectedDeviceId != 0 && deviceId != _options.ExpectedDeviceId)
                throw new InvalidDataException($"海康注册设备编号不匹配：expected={_options.ExpectedDeviceId}, actual={deviceId}。");
            var selfCheckState = request.Body.Span[57];
            var success = selfCheckState == 0;
            var reply = new byte[12];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(0, 4), deviceId);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4, 4), success ? 200u : 201u);
            reply[8] = success ? (byte)0 : (byte)1;
            reply[9] = (byte)'X';
            reply[10] = (byte)'Y';
            await ReplyRawAsync(request, reply, HikvisionContentType.Binary).ConfigureAwait(false);
            if (!success) throw new InvalidDataException($"海康AGV自检未通过，注册状态={selfCheckState}。");
            _registered = true;
            _lastStateAt = DateTimeOffset.UtcNow;
            // 注册完成后必须查询基础能力集。失败时撤销注册，禁止后续MOVE直接进入设备。
            var query = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(query, deviceId);
            var capability = await SendRawAsync(HikvisionSignals.QueryBasicCapability, query).ConfigureAwait(false);
            if (capability.Body.Length < 8)
                throw new InvalidDataException($"海康基础能力集回复过短：{capability.Body.Length}字节。");
            _registration.TrySetResult(true);
            _options.WriteLog("REGISTERED", $"remote={_remoteEndPoint}, seq={request.Sequence}, capabilityBytes={capability.Body.Length}");
            MessageReceived?.Invoke(this, request);
        }
        catch (Exception ex)
        {
            InvalidateRegistration("注册或能力集查询失败");
            _options.WriteLog("REGISTER_ERROR", ex.Message);
            ConnectionFaulted?.Invoke(this, ex);
        }
    }

    private static async Task ReceiveExactlyAsync(Socket socket, Memory<byte> target, CancellationToken ct)
    {
        var received = 0;
        while (received < target.Length)
        {
            var count = await socket.ReceiveAsync(target[received..], SocketFlags.None, ct).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("海康TCP连接已被对端关闭。");
            received += count;
        }
    }

    private void ValidateLength(int length)
    {
        if (length < HikvisionFrameCodec.HeaderSize || length > _options.MaxFrameLength || length % 4 != 0)
            throw new InvalidDataException($"海康消息长度超出限制：{length}。");
    }

    private uint NextSequence() => unchecked((uint)Interlocked.Increment(ref _sequence));

    private bool IsConfiguredAddress(IPAddress address) => _configuredRemoteAddress is not null
        && (address.Equals(_configuredRemoteAddress) || address.MapToIPv6().Equals(_configuredRemoteAddress.MapToIPv6()));

    private static bool IsTaskControlSignal(ushort signal) => signal is >= 0x0302 and <= 0x0332
        || signal is >= 0x0900 and <= 0x0908;

    private void InvalidateRegistration(string reason)
    {
        if (!_registered) return;
        _registered = false;
        _registration = NewRegistrationSource();
        _options.WriteLog("UNREGISTERED", reason);
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var pending in _pending.Values) pending.Waiter.TrySetException(exception);
        _pending.Clear();
    }

    private async Task CloseSocketCoreAsync()
    {
        if (_receiveCts is not null) await _receiveCts.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        _listener = null;
        try { _socket?.Shutdown(SocketShutdown.Both); } catch { }
        _socket?.Dispose();
        _socket = null;
        _registered = false;
        _registration.TrySetCanceled();
        _registration = NewRegistrationSource();
        if (_receiveTask is not null) try { await _receiveTask.ConfigureAwait(false); } catch { }
        if (_acceptTask is not null) try { await _acceptTask.ConfigureAwait(false); } catch { }
        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;
        _acceptTask = null;
    }

    private static async Task<IPEndPoint> ResolveEndPointAsync(string host, int port, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var parsed)) return new(parsed, port);
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return new(addresses.First(x => x.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6), port);
    }

    private static async Task<IPAddress> ResolveLocalAddressAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var parsed)) return parsed;
        return (await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false))
            .First(x => x.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try { await CloseSocketCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleLock.Release(); }
        FailAllPending(new ObjectDisposedException(nameof(HikvisionMobileRobot)));
        _sendLock.Dispose();
        _lifecycleLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TaskCompletionSource<bool> NewRegistrationSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record PendingRequest(ushort ExpectedSignal, TaskCompletionSource<HikvisionMessage> Waiter);
}
