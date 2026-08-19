using System.Text.Json;

namespace Kunling.RobotClient.Devices.HikvisionMobileRobot;

/// <summary>协议传输层。协议默认 UDP，需要可靠有序链路时可配置 TCP。</summary>
public enum HikvisionTransport { Udp, Tcp }

/// <summary>消息体形式，数值与协议附录2一致。</summary>
public enum HikvisionContentType : byte { Binary = 0, Json = 1 }

/// <summary>客户端连接、重试和报文安全限制。</summary>
public sealed class HikvisionMobileRobotOptions
{
    public string RemoteHost { get; init; } = "192.168.0.10";
    public int RemotePort { get; init; } = 5000;
    public string LocalHost { get; init; } = "0.0.0.0";
    /// <summary>RCS 固定监听端口。海康 AGV 按现场配置主动向该端口注册，禁止使用 0 随机端口。</summary>
    public int LocalPort { get; init; } = 5000;
    public HikvisionTransport Transport { get; init; } = HikvisionTransport.Udp;
    public int RequestTimeoutMs { get; init; } = 5_000;
    public int AckRetryIntervalMs { get; init; } = 100;
    public int TaskRetryIntervalMs { get; init; } = 1_000;
    public int HeartbeatTimeoutMs { get; init; } = 2_000;
    public int ReconnectDelayMs { get; init; } = 2_000;
    public int MaxFrameLength { get; init; } = 65_532;
    public byte ProtocolVersion { get; init; } = 0x20;
    public uint ExpectedDeviceId { get; init; }
    public Action<string>? Log { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RemoteHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(LocalHost);
        if (RemotePort is < 1 or > 65535 || LocalPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(RemotePort));
        if (RequestTimeoutMs <= 0 || AckRetryIntervalMs <= 0 || TaskRetryIntervalMs <= 0 || HeartbeatTimeoutMs <= 0
            || ReconnectDelayMs < 0 || MaxFrameLength < HikvisionFrameCodec.HeaderSize || MaxFrameLength > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeoutMs));
    }

    internal void WriteLog(string action, string message) => Log?.Invoke($"[DEVICE][CHASSIS:HIKVISION] {action} {message}");
}

/// <summary>解码后的完整 GBP 报文。Body 包含去除4字节对齐填充后的有效消息体。</summary>
public sealed record HikvisionMessage(ushort Signal, uint Sequence, byte Version,
    byte Encryption, HikvisionContentType ContentType, ReadOnlyMemory<byte> Body)
{
    public T? DeserializeJson<T>(JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(Body.Span, options ?? HikvisionJson.Options);
}

/// <summary>未经协议业务解释的请求结果，可用于尚未建立强类型 DTO 的任何保留或扩展信令。</summary>
public sealed record HikvisionResponse(ushort Signal, uint Sequence, ReadOnlyMemory<byte> Body)
{
    public T? DeserializeJson<T>(JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(Body.Span, options ?? HikvisionJson.Options);
}

internal static class HikvisionJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
