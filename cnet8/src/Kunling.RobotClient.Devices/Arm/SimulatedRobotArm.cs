using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Models;
using Kunling.RobotClient.Devices.Recipes;
using Kunling.RobotClient.Devices.Simulation;

namespace Kunling.RobotClient.Devices.Arm;

[DeviceModel("SimulatedRobotArm")]
public sealed class SimulatedRobotArm(SimulationState state, SimulationOptions options) : IArm
{
    public string Vendor => "KUNLING";
    public string Model => "SIM_ARM";
    public Task<DeviceResult<ArmActionResult>> HomeAsync(CancellationToken ct) =>
        Task.FromResult(DeviceResult<ArmActionResult>.Fail(new(4004, "HOME 必须通过 ARM.HOME.Templates.json 执行。")));
    public Task<DeviceResult<ArmActionResult>> PickAsync(ArmPickRequest request, CancellationToken ct) =>
        Task.FromResult(DeviceResult<ArmActionResult>.Fail(new(4004, "PICK 必须通过 ARM.PICK.Templates.json 执行。")));
    public Task<DeviceResult<ArmActionResult>> PlaceAsync(ArmPlaceRequest request, CancellationToken ct) =>
        Task.FromResult(DeviceResult<ArmActionResult>.Fail(new(4004, "PLACE 必须通过 ARM.PLACE.Templates.json 执行。")));

    public async Task<DeviceResult<ArmActionResult>> MoveToPoseAsync(ArmMoveRequest request, CancellationToken ct)
    {
        if (request.Pose is null)
            return DeviceResult<ArmActionResult>.Fail(new(4404, $"模板 phase {request.PoseRole} 没有配置 pose。"));
        var arrival = new ArrivalCriteria(request.PositionToleranceMm, request.AngleToleranceDeg,
            request.SettleMs, request.TimeoutMs, request.PollMs);
        options.WriteLog($"ARM:{Model}", "TEMPLATE_POSE",
            $"{request.Station}:{request.PoseRole} pose={request.Pose} frame={request.Frame} " +
            $"speed={request.SpeedProfile} collision={request.CollisionProfile}");
        await state.MotionLock.WaitAsync(ct);
        try
        {
            lock (state.Sync) state.ArmMoving = true;
            var delay = request.SpeedProfile.ToUpperInvariant() switch
            {
                "SLOW" => options.ActionDelayMs * 2,
                "FAST" => Math.Max(1, options.ActionDelayMs / 2),
                _ => options.ActionDelayMs
            };
            await Task.Delay(delay, ct);
            lock (state.Sync)
            {
                state.ArmPose = request.Pose;
                state.Homed = request.PoseRole.Equals("HOME", StringComparison.OrdinalIgnoreCase);
            }
            ArmPose Feedback() { lock (state.Sync) return state.ArmPose; }
            if (!await ArrivalVerifier.WaitArmAsync(Feedback, request.Pose, arrival, ct))
                return DeviceResult<ArmActionResult>.Fail(new(5102, "机械臂到位判定超时。", Model, false, true));
            return DeviceResult<ArmActionResult>.Ok(new(Feedback(), state.Gripped));
        }
        finally { lock (state.Sync) state.ArmMoving = false; state.MotionLock.Release(); }
    }

    public Task<DeviceResult<ArmStatus>> GetStatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (state.Sync) return Task.FromResult(DeviceResult<ArmStatus>.Ok(
            new(true, state.ArmMoving, state.Homed, state.ArmPose)));
    }
}
