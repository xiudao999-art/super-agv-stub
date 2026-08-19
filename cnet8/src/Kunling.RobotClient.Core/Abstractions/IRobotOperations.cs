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
    Task<DeviceResult<ArmActionResult>> HomeAsync(CancellationToken cancellationToken);
    Task<DeviceResult<VisionResult>> CaptureAsync(VisionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 机器人主动作执行进度源。每完成一次 phase 尝试（包括失败、复核、跳过）即发布快照，
/// 供通信层缓存当前 Action/SubAction 状态并实时上报服务器。
/// </summary>
public interface IRobotExecutionProgressSource
{
    event EventHandler<OperationStep>? StepChanged;
}
