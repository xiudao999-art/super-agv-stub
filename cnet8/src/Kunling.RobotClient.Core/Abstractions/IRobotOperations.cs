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
