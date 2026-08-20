namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>
/// 坤灵机器人客户端/Action 协议使用的平台级错误码。
/// </summary>
/// <remarks>
/// 这里的错误码描述的是协议校验、动作编排、运行状态和恢复流程，
/// 不代表海康底盘、华沿机械臂、相机、夹爪等物理设备的原始错误码。
/// 物理设备错误码必须通过 DeviceCode/UnifiedRobotErrorModel.PhysicalDevice 原样上报。
///
/// 分段约定：
/// 2xxx：会话、认证与能力协商；
/// 4xxx：请求、模板、状态及业务前置条件；
/// 5xxx：客户端内部执行、结果未知与 Phase 恢复。
/// </remarks>
public static class PlatformErrorCodes
{
    // 2xxx：连接、会话与能力协商。
    public const int SessionOrRobotMismatch = 2005;

    // 4xxx：请求、模板与运行前置条件。
    public const int InvalidActionInput = 4000;
    public const int UnsupportedAction = 4004;

    /// <summary>
    /// 新动作因机器人已有唯一运行中 MainAction 而被拒绝。
    /// 该错误只作用于本次新请求，不表示当前运行中的动作失败。
    /// </summary>
    public const int RobotBusy = 4090;

    // 5xxx：客户端执行及状态确认。
    public const int InternalExecutionError = 5001;
    public const int PhaseExecutionFailed = 5002;
    public const int ActionStateUnknown = 5004;

    // 52xx：复核冲突和 Phase 重试耗尽。
    public const int MaterialStateUnknown = 5201;
    public const int PlacementStateConflict = 5202;
    public const int RetryExhaustedHold = 5210;
    public const int RetryExhaustedCancel = 5211;
    public const int RetryExhaustedManual = 5212;
}
