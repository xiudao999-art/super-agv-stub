using Kunling.RobotClient.Core.Controller.Actions;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Abstractions;

/// <summary>
/// 设备之上的机器人业务操作接口。Actions 层负责把服务器输入转换为这些强类型调用，
/// Core 不依赖 REGISTER/COMMAND/JSON 等网络协议。
/// </summary>
public interface IRobotOperations
{
    Task<DeviceResult<MoveResult>> MoveAsync(MoveRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<MoveResult>> ExecuteMoveAsync(MoveAction action, CancellationToken cancellationToken);
    Task<DeviceResult<ArmActionResult>> PickAsync(ArmPickRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<ArmActionResult>> ExecutePickAsync(MainActionTemplate action, CancellationToken cancellationToken);
    Task<DeviceResult<ArmActionResult>> ExecutePlaceAsync(MainActionTemplate action, CancellationToken cancellationToken);
    Task<DeviceResult<ArmActionResult>> ExecuteHomeAsync(MainActionTemplate action, CancellationToken cancellationToken);
    Task<DeviceResult<VisionResult>> ExecuteCaptureAsync(MainActionTemplate action, CancellationToken cancellationToken);
    Task<DeviceResult<ArmActionResult>> PlaceAsync(ArmPlaceRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<BatchActionResult>> PickBatchAsync(ArmPickBatchRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<BatchActionResult>> PlaceBatchAsync(ArmPlaceBatchRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<BatchActionResult>> ExecutePickBatchAsync(MainActionTemplate action, CancellationToken cancellationToken);
    Task<DeviceResult<BatchActionResult>> ExecutePlaceBatchAsync(MainActionTemplate action, CancellationToken cancellationToken);
    /// <summary>不按固定 L2 实现分流，按服务器给出的 phases 逐项解释执行。</summary>
    Task<DeviceResult<MainActionExecutionResult>> ExecuteMainActionPhasesAsync(MainActionTemplate action, CancellationToken cancellationToken);
    Task<DeviceResult<ArmActionResult>> HomeAsync(CancellationToken cancellationToken);
    Task<DeviceResult<VisionResult>> CaptureAsync(VisionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 机器人主动作执行进度源。StepChanged 提供累计结果快照，ProgressChanged 提供开始、结束、
/// 重试和异常策略等结构化执行事实，供通信层实时上报服务器。
/// </summary>
public interface IRobotExecutionProgressSource
{
    event EventHandler<OperationStep>? StepChanged;
    event EventHandler<OperationProgress>? ProgressChanged;
}
