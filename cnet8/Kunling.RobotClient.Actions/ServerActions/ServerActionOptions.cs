namespace Kunling.RobotClient.Actions.ServerActions;

public sealed class ServerActionOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string RobotId { get; init; }
    public string RobotType { get; init; } = "COMPOSITE_ROBOT";
    public string ClientVersion { get; init; } = "1.0.0";
    public string ProtocolVersion { get; init; } = "1.0";
    public int ConnectTimeoutMs { get; init; } = 10_000;
    public int RegisterTimeoutMs { get; init; } = 10_000;
    public int DefaultHeartbeatMs { get; init; } = 10_000;
    public int ReconnectDelayMs { get; init; } = 3_000;
    public int MaxMessageBytes { get; init; } = 1024 * 1024;
}

public sealed record ServerActionRegistration(
    IReadOnlyList<DeviceDescriptor> Devices,
    IReadOnlyList<ActionCapability> Capabilities,
    IReadOnlyList<ExecutionMode> ExecutionModes);
