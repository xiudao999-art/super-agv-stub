namespace Kunling.RobotClient.Core.Models;

/// <summary>设备错误的业务分类，供调度、告警和运维界面采用一致的处理方式。</summary>
public enum DeviceErrorCategory
{
    Unknown,
    Communication,
    Hardware,
    Safety,
    Motion,
    State,
    Configuration,
    Peripheral
}

/// <summary>错误恢复策略。策略表示下一步应做什么，不等同于立即自动执行该操作。</summary>
public enum DeviceRecoveryStrategy
{
    None,
    WaitAndRetry,
    ResetRequired,
    PowerCycle,
    ManualRecovery,
    CorrectConfiguration,
    Abort
}

/// <summary>
/// 平台统一异常等级。该等级描述业务影响范围，不直接等同于厂商控制器的告警等级。
/// </summary>
public enum RobotErrorSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// 异常首要责任归属，用于告警路由和运维派单；它不替代最终的事故责任认定。
/// </summary>
public enum RobotErrorOwner
{
    Unknown,
    UpstreamSystem,
    Scheduler,
    RobotClient,
    DeviceAdapter,
    SiteOperator,
    DeviceVendor
}

/// <summary>
/// 实际物理设备返回的原始异常。平台不得用厂商错误码覆盖自己的业务错误码，
/// 因此这里作为独立对象随平台错误一起保存和上报。
/// </summary>
public sealed record PhysicalDeviceError(
    string DeviceType,
    string? Vendor = null,
    string? Model = null,
    string? DeviceId = null,
    string? Code = null,
    string? Message = null);

/// <summary>所有设备调用使用的统一错误，不包含 Socket/JSON 协议字段。</summary>
public sealed record DeviceError(
    int Code,
    string Message,
    string? DeviceCode = null,
    bool PhysicalResultKnown = true,
    bool Retryable = false,
    DeviceErrorCategory Category = DeviceErrorCategory.Unknown,
    DeviceRecoveryStrategy RecoveryStrategy = DeviceRecoveryStrategy.None,
    string? HandlingAdvice = null,
    RobotErrorSeverity Severity = RobotErrorSeverity.Error,
    RobotErrorOwner Owner = RobotErrorOwner.RobotClient,
    bool? Recoverable = null,
    PhysicalDeviceError? PhysicalDevice = null);

/// <summary>厂商适配器返回的强类型结果。</summary>
public sealed record DeviceResult<T>(bool Success, T? Value = default, DeviceError? Error = null,
    IReadOnlyList<OperationStep>? Steps = null)
{
    public static DeviceResult<T> Ok(T? value = default, IReadOnlyList<OperationStep>? steps = null) => new(true, value, Steps: steps);
    public static DeviceResult<T> Fail(DeviceError error, IReadOnlyList<OperationStep>? steps = null) => new(false, default, error, steps);
}

public sealed record OperationStep(int Sequence, string PhaseId, string SubAction, string State,
    object? Evidence = null, string? SlotId = null, string? CacheSlot = null, string? PoseRef = null);
/// <summary>
/// 主动作执行策略。CompletedPhaseIds 由服务器持久化后在恢复命令中回传，
/// 客户端据此跳过已经成功且具有物理证据的 phase，实现从确定断点继续执行。
/// </summary>
public sealed record ActionExecutionPolicy(
    int MaxRetries = 0,
    string RetryMode = "VERIFY_BEFORE_RETRY",
    string OnExhaust = "HOLD",
    int RetryDelayMs = 500,
    IReadOnlyList<string>? CompletedPhaseIds = null);

public sealed record RobotPose(double X, double Y, double Yaw, string? Map = null);
public sealed record ArmPose(double X, double Y, double Z, double Rx, double Ry, double Rz);

/// <summary>服务器下发的完整 MOVE 参数；客户端不再保存站点坐标表。</summary>
public sealed record MoveRequest(
    string PointName,
    double Speed = 0.5,
    RobotPose? Pose = null,
    MoveArrivalRequest? Arrival = null,
    string? Port = null);
public sealed record MoveArrivalRequest(
    double PositionToleranceMm = 5,
    double AngleToleranceDeg = 5,
    int TimeoutMs = 30_000);
public sealed record MoveResult(string PointName, RobotPose? ActualPose = null);
public sealed record ArmPickRequest(string Station, string? Point = null, ActionExecutionPolicy? Policy = null,
    string GraspProfile = "DEFAULT_PICK");
public sealed record ArmPlaceRequest(string Station, string? Point = null, ActionExecutionPolicy? Policy = null,
    string ReleaseProfile = "DEFAULT_PLACE");
public sealed record ArmActionResult(ArmPose? ActualPose = null, bool? Gripped = null);
public sealed record ArmMoveRequest(
    string Station,
    string PoseRole,
    string? Point = null,
    ArmPose? Pose = null,
    double PositionToleranceMm = 2,
    double AngleToleranceDeg = 1,
    int SettleMs = 200,
    int TimeoutMs = 10_000,
    int PollMs = 50,
    string Frame = "BASE",
    string SpeedProfile = "NORMAL",
    string CollisionProfile = "NORMAL");
public sealed record BatchSlot(string SlotId, string? Point = null, int? Rank = null,
    string? Row = null, int? Depth = null, double? Distance = null, int? ExplicitOrder = null);
public sealed record ArmPickBatchRequest(string Station, IReadOnlyList<BatchSlot> Slots,
    string OrderPolicy = "RANK_ASC", ActionExecutionPolicy? Policy = null,
    IReadOnlyDictionary<string, string>? CacheAssign = null,
    string CacheAssignMode = "EXPLICIT_MAP", IReadOnlyList<string>? AvailableCacheSlots = null);
public sealed record ArmPlaceBatchRequest(string Station, IReadOnlyList<BatchSlot> Slots,
    string OrderPolicy = "RANK_ASC", ActionExecutionPolicy? Policy = null,
    IReadOnlyDictionary<string, string>? CacheAssign = null,
    string ReleaseProfile = "DEFAULT_PLACE", string CacheAssignMode = "EXPLICIT_MAP",
    IReadOnlyList<string>? AvailableCacheSlots = null);
public sealed record BatchActionResult(int CompletedCount, int TotalCount, IReadOnlyList<string> CompletedSlots);
public sealed record VisionRequest(
    string? Station = null,
    string? Recipe = null,
    string? CameraId = null,
    double? ExposureMs = null,
    double? Gain = null,
    int? TimeoutMs = null,
    string? OutputFormat = null,
    bool? SimulatedPass = null,
    string? ExpectedMaterial = null);
public sealed record VisionResult(bool Passed, string? ImageUri = null, ArmPose? Correction = null);
public sealed record GripRequest(
    double? Force = null,
    double? Width = null,
    string? Profile = null,
    int? HoldMs = null,
    double? MinDetectedWidth = null,
    double? MaxDetectedWidth = null,
    int? StableForMs = null,
    int? PollMs = null,
    bool RequireForceFeedback = false,
    double? MinForce = null,
    bool? ExpectedDetected = null);
public sealed record GripResult(bool Detected, double? Width = null, double? Force = null);
public sealed record RfidReadRequest(string? ExpectedTag = null, TimeSpan? StableFor = null);
public sealed record RfidReadResult(string Tag, DateTimeOffset ReadAt);
public sealed record DoorRequest(string DoorId);

public sealed record ChassisStatus(bool Connected, bool Moving, int? Battery, RobotPose? Pose, string? FaultCode = null);
public sealed record ArmStatus(bool Connected, bool Moving, bool Homed, ArmPose? Pose, string? FaultCode = null);
public sealed record DoorStatus(bool Connected, bool Opened, string? FaultCode = null);

public sealed record RobotEvent(
    string EventType,
    string RobotId,
    string? ActionInstanceId,
    object? Data,
    DateTimeOffset Timestamp);
