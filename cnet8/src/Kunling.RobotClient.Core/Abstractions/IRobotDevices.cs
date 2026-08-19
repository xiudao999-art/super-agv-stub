using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Abstractions;

/// <summary>移动底盘接口；实现类负责把强类型请求转换成海康等厂商协议。</summary>
public interface IChassis
{
    string Vendor { get; }
    string Model { get; }
    Task<DeviceResult<MoveResult>> MoveAsync(MoveRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<MoveResult>> ReturnToStandbyAsync(CancellationToken cancellationToken);
    Task<DeviceResult<ChassisStatus>> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>机械臂接口；Core 不包含华沿端口、报文或 V8 类型。</summary>
public interface IArm
{
    string Vendor { get; }
    string Model { get; }
    Task<DeviceResult<ArmActionResult>> HomeAsync(CancellationToken cancellationToken);
    Task<DeviceResult<ArmActionResult>> MoveToPoseAsync(ArmMoveRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<ArmActionResult>> PickAsync(ArmPickRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<ArmActionResult>> PlaceAsync(ArmPlaceRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<ArmStatus>> GetStatusAsync(CancellationToken cancellationToken);
}

public interface IVision
{
    string Vendor { get; }
    string Model { get; }
    Task<DeviceResult<VisionResult>> CaptureAsync(VisionRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<VisionResult>> VerifyAsync(VisionRequest request, CancellationToken cancellationToken);
}

public interface IGripper
{
    string Vendor { get; }
    string Model { get; }
    Task<DeviceResult<GripResult>> GripAsync(GripRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<GripResult>> ReleaseAsync(GripRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<GripResult>> GetStatusAsync(CancellationToken cancellationToken);
    Task<DeviceResult<GripResult>> VerifyLoadAsync(GripRequest request, CancellationToken cancellationToken);
}

/// <summary>RFID 仅提供设备读取能力；等待业务许可由服务器工作流负责。</summary>
public interface IRfidReader
{
    Task<DeviceResult<RfidReadResult>> ReadAsync(RfidReadRequest request, CancellationToken cancellationToken);
}

public interface IDoor
{
    Task<DeviceResult<DoorStatus>> OpenAsync(DoorRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<DoorStatus>> CloseAsync(DoorRequest request, CancellationToken cancellationToken);
    Task<DeviceResult<DoorStatus>> GetStatusAsync(DoorRequest request, CancellationToken cancellationToken);
}
