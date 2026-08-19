using System.Text.Json;
using System.Text.Json.Serialization;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Controller.ReportStateModels;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Actions.ServerActions;

public static class ServerMessageTypes
{
    public const string Register = "REGISTER";
    public const string RegisterAck = "REGISTER_ACK";
    public const string Ping = "PING";
    public const string Pong = "PONG";
    public const string Command = "COMMAND";
    public const string ActionEvent = "ACTION_EVENT";
    public const string QueryAction = "QUERY_ACTION";
    public const string ActionStatus = "ACTION_STATUS";
    public const string StateReport = "STATE_REPORT";
    public const string TerminateAction = "TERMINATE_ACTION";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionMode { Atomic, Package }

public sealed record DeviceDescriptor(
    string Category,
    string Vendor,
    string Model,
    string Adapter,
    string AdapterVersion,
    string? FirmwareVersion,
    bool Connected);

public sealed record ActionCapability(
    string ActionType,
    string ActionVersion,
    string SchemaHash,
    ExecutionMode ExecutionMode,
    IReadOnlyList<string>? Features = null,
    int? MinTimeoutMs = null,
    int? MaxTimeoutMs = null);

public sealed record RobotStateSnapshot(
    string State,
    int? Battery,
    bool Emergency,
    bool ChassisConnected,
    bool ArmConnected,
    string? CurrentActionInstanceId,
    DateTimeOffset Timestamp);

public sealed record RegisterRequest(
    string Version,
    string MessageType,
    string MessageId,
    string ClientInstanceId,
    string RobotId,
    string RobotType,
    string ClientVersion,
    string ProtocolVersion,
    IReadOnlyList<DeviceDescriptor> Devices,
    IReadOnlyList<ExecutionMode> ExecutionModes,
    IReadOnlyList<ActionCapability> Capabilities,
    RobotStateSnapshot Snapshot,
    DateTimeOffset Timestamp);

public sealed record CapabilityDecision(string ActionType, string ActionVersion, string? ReasonCode = null, string? Reason = null);

public sealed record RegisterAck(
    string Version,
    string MessageType,
    string MessageId,
    string ReplyTo,
    string RobotId,
    bool Accepted,
    string? SessionId,
    int LeaseMs,
    int HeartbeatIntervalMs,
    IReadOnlyList<CapabilityDecision>? AcceptedCapabilities,
    IReadOnlyList<CapabilityDecision>? RejectedCapabilities,
    string? Reason,
    DateTimeOffset ServerTime);

public sealed record ActionCommand(
    string Version,
    string MessageType,
    string MessageId,
    string SessionId,
    string RobotId,
    string ActionInstanceId,
    string DeviceCommandId,
    string? WorkflowInstanceId,
    string? NodeInstanceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActionType,
    string ActionVersion,
    ExecutionMode ExecutionMode,
    JsonElement ConfigSnapshot,
    JsonElement Input,
    int TimeoutMs,
    DateTimeOffset Timestamp);

public sealed record ResolvedStep(
    int Sequence,
    string PhaseId,
    string SubAction,
    string State,
    JsonElement? Evidence = null,
    string? SlotId = null,
    string? CacheSlot = null,
    string? PoseRef = null);

public sealed record ActionError(int Code, string Message, string? DeviceCode = null,
    bool PhysicalResultKnown = true, bool Retryable = false,
    DeviceErrorCategory Category = DeviceErrorCategory.Unknown,
    DeviceRecoveryStrategy RecoveryStrategy = DeviceRecoveryStrategy.None,
    string? HandlingAdvice = null,
    ActionFailureContext? Context = null,
    UnifiedRobotErrorModel? Detail = null);

/// <summary>机器人忙碌或 Phase 失败时回传的结构化执行现场。</summary>
public sealed record ActionFailureContext(
    string ActionInstanceId,
    string ActionType,
    string? TemplateId = null,
    string? PhaseId = null,
    string? SubAction = null,
    PhaseFailAction? OnFail = null,
    IReadOnlyList<string>? UserChoices = null,
    MainActionState? MainActionState = null,
    string? SubActionState = null);

public sealed record ActionEvent(
    string Version,
    string MessageType,
    string MessageId,
    string SessionId,
    string RobotId,
    string ActionInstanceId,
    string DeviceCommandId,
    long Sequence,
    MainActionState State,
    IReadOnlyList<ResolvedStep>? ResolvedSteps,
    JsonElement? PhysicalResult,
    ActionError? Error,
    DateTimeOffset Timestamp,
    ReportRobotStateModel? ReportState = null);

public sealed record QueryActionRequest(string Version, string MessageType, string MessageId, string SessionId, string RobotId, string ActionInstanceId, string DeviceCommandId);
public sealed record TerminateActionRequest(string Version, string MessageType, string MessageId,
    string SessionId, string RobotId, string ActionInstanceId, DateTimeOffset Timestamp);

public sealed record PingMessage(string Version, string MessageType, string MessageId, string SessionId, string RobotId, long Sequence, RobotStateSnapshot Snapshot, DateTimeOffset Timestamp);
public sealed record PongMessage(string Version, string MessageType, string MessageId, string ReplyTo, string SessionId, long Sequence, DateTimeOffset ServerTime);

public sealed class ServerActionJson
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // MainAction 使用 MOVE / ARM.PICK / VISION.CAPTURE 等带点协议名，
        // 必须放在通用枚举转换器之前，否则 ARM.PICK 会按普通 C# 枚举名解析并失败。
        Converters =
        {
            new MainActionJsonConverter(),
            new SubActionJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)
        }
    };
}
