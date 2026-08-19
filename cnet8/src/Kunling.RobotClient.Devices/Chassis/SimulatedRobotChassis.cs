using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Config;
using Kunling.RobotClient.Core.Models;
using Kunling.RobotClient.Devices.Recipes;
using Kunling.RobotClient.Devices.Simulation;

namespace Kunling.RobotClient.Devices.Chassis;

[DeviceModel("SimulatedRobotChassis")]
public sealed class SimulatedRobotChassis(
    SimulationState state,
    SimulationOptions options,
    ChassisArrivalConfig arrivalConfig) : IChassis
{
    public string Vendor => "KUNLING";
    public string Model => "SIM_CHASSIS";

    public async Task<DeviceResult<MoveResult>> MoveAsync(MoveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PointName))
            return DeviceResult<MoveResult>.Fail(new(4000, "pointName 不能为空。"));
        if (request.Speed <= 0 || double.IsNaN(request.Speed) || double.IsInfinity(request.Speed))
            return DeviceResult<MoveResult>.Fail(new(4000, "speed 必须是大于 0 的有效数值。"));

        if (request.Pose is null)
            return DeviceResult<MoveResult>.Fail(new(4000, "pose 不能为空；服务器必须随 MOVE 下发完整位置。"));
        var requestedArrival = request.Arrival ?? new MoveArrivalRequest(
            arrivalConfig.XyToleranceMm, arrivalConfig.YawToleranceDeg);
        var recipe = new ChassisRecipe(request.Pose,
            new ArrivalCriteria(requestedArrival.PositionToleranceMm, requestedArrival.AngleToleranceDeg,
                TimeoutMs: requestedArrival.TimeoutMs), request.Speed);

        // speed=0.5 使用 ActionDelayMs；速度加倍，模拟运动时间减半。
        var motionDelayMs = Math.Clamp(
            (int)Math.Round(options.ActionDelayMs * (0.5 / request.Speed)),
            10,
            120_000);

        options.WriteLog($"CHASSIS:{Model}", "RECIPE",
            $"pointName={request.PointName}, port={request.Port ?? "-"}, pose={recipe.Pose}, " +
            $"speed={request.Speed}, timer={motionDelayMs}ms");

        await state.MotionLock.WaitAsync(ct);
        try
        {
            lock (state.Sync) state.ChassisMoving = true;
            options.WriteLog($"CHASSIS:{Model}", "MOVE", "设备指令已下发，定时器开始");
            await Task.Delay(motionDelayMs, ct);

            lock (state.Sync)
            {
                state.ChassisPose = recipe.Pose;
                state.Battery = Math.Max(0, state.Battery - 1);
            }

            RobotPose Feedback()
            {
                lock (state.Sync) return state.ChassisPose;
            }

            var arrived = await ArrivalVerifier.WaitChassisAsync(Feedback, recipe.Pose, recipe.Arrival, ct);
            var actual = Feedback();
            if (!arrived)
            {
                options.WriteLog($"CHASSIS:{Model}", "ARRIVAL", $"超时 target={recipe.Pose}, actual={actual}");
                return DeviceResult<MoveResult>.Fail(new(5101, "底盘到位判定超时。", Model, false, true));
            }

            options.WriteLog($"CHASSIS:{Model}", "ARRIVAL", $"到位并稳定，actual={actual}");
            return DeviceResult<MoveResult>.Ok(new(request.PointName, actual));
        }
        finally
        {
            lock (state.Sync) state.ChassisMoving = false;
            state.MotionLock.Release();
        }
    }

    public Task<DeviceResult<MoveResult>> ReturnToStandbyAsync(CancellationToken ct) =>
        Task.FromResult(DeviceResult<MoveResult>.Fail(new(4000,
            "客户端不保存 STANDBY 位姿；请由服务器使用 MOVE 下发 STANDBY 的完整 pose。")));

    public Task<DeviceResult<ChassisStatus>> GetStatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (state.Sync)
            return Task.FromResult(DeviceResult<ChassisStatus>.Ok(
                new(true, state.ChassisMoving, state.Battery, state.ChassisPose)));
    }
}
