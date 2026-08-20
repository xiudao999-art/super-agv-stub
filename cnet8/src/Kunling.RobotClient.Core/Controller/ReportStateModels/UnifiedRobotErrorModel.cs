using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.ReportStateModels;

/// <summary>
/// 机器人客户端对服务器上报的统一异常结构。
/// <para>Code/Message 是平台语义，PhysicalDevice 保存厂家原始语义，两者不能互相覆盖。</para>
/// <para>Recoverable 表示现场处置后能否恢复；Retryable 仅表示当前条件下是否允许自动重试。</para>
/// </summary>
public sealed record UnifiedRobotErrorModel(
    string SubAction,
    int Code,
    string Message,
    RobotErrorSeverity Severity,
    DeviceErrorCategory Category,
    PhysicalDeviceError PhysicalDevice,
    bool Recoverable,
    bool Retryable,
    RobotErrorOwner Owner,
    DeviceRecoveryStrategy RecoveryStrategy,
    string FailureStrategy,
    bool PhysicalResultKnown,
    string? HandlingAdvice,
    DateTimeOffset OccurredAt)
{
    /// <summary>由设备层错误和当前 Phase 现场生成稳定的对外结构。</summary>
    public static UnifiedRobotErrorModel Create(DeviceError error, string subAction,
        string? failureStrategy = null, DateTimeOffset? occurredAt = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var physical = error.PhysicalDevice ?? new PhysicalDeviceError(
            InferDeviceType(subAction), Code: error.DeviceCode, Message: error.Message);
        var recoverable = error.Recoverable ?? IsRecoverable(error);
        var severity = ResolveSeverity(error);
        var owner = ResolveOwner(error, physical);
        return new UnifiedRobotErrorModel(
            subAction,
            error.Code,
            error.Message,
            severity,
            error.Category,
            physical,
            recoverable,
            error.Retryable,
            owner,
            error.RecoveryStrategy,
            string.IsNullOrWhiteSpace(failureStrategy) ? "ABORT" : failureStrategy,
            error.PhysicalResultKnown,
            error.HandlingAdvice,
            occurredAt ?? DateTimeOffset.UtcNow);
    }

    private static bool IsRecoverable(DeviceError error) => error.Retryable ||
        error.RecoveryStrategy is DeviceRecoveryStrategy.WaitAndRetry or
            DeviceRecoveryStrategy.ResetRequired or DeviceRecoveryStrategy.PowerCycle or
            DeviceRecoveryStrategy.ManualRecovery or DeviceRecoveryStrategy.CorrectConfiguration;

    private static RobotErrorSeverity ResolveSeverity(DeviceError error)
    {
        if (error.Severity != RobotErrorSeverity.Error) return error.Severity;
        if (error.Category == DeviceErrorCategory.Safety && !error.PhysicalResultKnown)
            return RobotErrorSeverity.Critical;
        if (error.RecoveryStrategy == DeviceRecoveryStrategy.WaitAndRetry)
            return RobotErrorSeverity.Warning;
        return RobotErrorSeverity.Error;
    }

    private static RobotErrorOwner ResolveOwner(DeviceError error, PhysicalDeviceError physical)
    {
        if (error.Owner != RobotErrorOwner.RobotClient) return error.Owner;
        if (physical.DeviceType == "UNKNOWN") return RobotErrorOwner.RobotClient;
        return error.Category is DeviceErrorCategory.Hardware or DeviceErrorCategory.Safety
            ? RobotErrorOwner.DeviceVendor
            : RobotErrorOwner.DeviceAdapter;
    }

    private static string InferDeviceType(string? subAction)
    {
        if (string.IsNullOrWhiteSpace(subAction)) return "UNKNOWN";
        if (subAction.StartsWith("MOVE_TO_MAP", StringComparison.OrdinalIgnoreCase) ||
            subAction.StartsWith("CHASSIS", StringComparison.OrdinalIgnoreCase)) return "CHASSIS";
        if (subAction.StartsWith("MOVE_TO_POSE", StringComparison.OrdinalIgnoreCase) ||
            subAction.StartsWith("ARM", StringComparison.OrdinalIgnoreCase)) return "ARM";
        if (subAction.StartsWith("GRIP", StringComparison.OrdinalIgnoreCase)) return "GRIPPER";
        if (subAction.StartsWith("VISION", StringComparison.OrdinalIgnoreCase)) return "VISION";
        if (subAction.StartsWith("RFID", StringComparison.OrdinalIgnoreCase)) return "RFID";
        if (subAction.StartsWith("DOOR", StringComparison.OrdinalIgnoreCase)) return "DOOR";
        return "UNKNOWN";
    }
}
