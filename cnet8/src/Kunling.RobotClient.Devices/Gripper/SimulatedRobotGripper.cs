using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Models;
using Kunling.RobotClient.Devices.Simulation;

namespace Kunling.RobotClient.Devices.Gripper;

[DeviceModel("SimulatedRobotGripper")]
public sealed class SimulatedRobotGripper(SimulationState state, SimulationOptions options) : IGripper
{
    public string Vendor => "KUNLING";
    public string Model => "SIM_GRIPPER";

    public async Task<DeviceResult<GripResult>> GripAsync(GripRequest request, CancellationToken ct)
    {
        if (request.Width is null || request.Force is null)
            return DeviceResult<GripResult>.Fail(new(4000, "GRIP.CLOSE 模板必须配置 targetWidthMm 和 gripForce。"));

        var force = request.Force.Value;
        var width = request.Width.Value;
        var holdMs = request.HoldMs ?? 100;
        var minWidth = request.MinDetectedWidth ?? 0;
        var maxWidth = request.MaxDetectedWidth ?? 70;
        options.WriteLog($"GRIPPER:{Model}", "GRIP.CLOSE",
            $"targetWidthMm={width} gripForce={force} holdMs={holdMs} detectedRange=[{minWidth},{maxWidth}]");
        await Task.Delay(options.ActionDelayMs + holdMs, ct);
        var detected = width >= minWidth && width <= maxWidth && force > 0;
        lock (state.Sync)
        {
            state.Gripped = detected;
            state.GripWidth = width;
            state.GripForce = force;
        }
        return detected
            ? DeviceResult<GripResult>.Ok(new(true, width, force))
            : DeviceResult<GripResult>.Fail(new(5201, "夹爪闭合结果不满足模板夹持条件。", Model, true, true));
    }

    public async Task<DeviceResult<GripResult>> ReleaseAsync(GripRequest request, CancellationToken ct)
    {
        if (request.Width is null)
            return DeviceResult<GripResult>.Fail(new(4000, "GRIP.OPEN 模板必须配置 targetWidthMm。"));
        var width = request.Width.Value;
        var holdMs = request.HoldMs ?? 100;
        var emptyMin = request.MinDetectedWidth ?? 70;
        options.WriteLog($"GRIPPER:{Model}", "GRIP.OPEN",
            $"targetWidthMm={width} holdMs={holdMs} emptyDetectedMinWidth={emptyMin}");
        await Task.Delay(options.ActionDelayMs + holdMs, ct);
        lock (state.Sync)
        {
            state.Gripped = false;
            state.GripWidth = width;
            state.GripForce = 0;
        }
        return width >= emptyMin
            ? DeviceResult<GripResult>.Ok(new(false, width, 0))
            : DeviceResult<GripResult>.Fail(new(5202, "夹爪开口未达到模板空载阈值。", Model, true, true));
    }

    public Task<DeviceResult<GripResult>> GetStatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (state.Sync)
            return Task.FromResult(DeviceResult<GripResult>.Ok(
                new(state.Gripped, state.GripWidth, state.GripForce)));
    }

    public async Task<DeviceResult<GripResult>> VerifyLoadAsync(GripRequest request, CancellationToken ct)
    {
        if (request.MinDetectedWidth is null || request.MaxDetectedWidth is null || request.StableForMs is null)
            return DeviceResult<GripResult>.Fail(new(4000,
                "GRIP.VERIFY_LOAD 模板必须配置宽度范围和 holdCheckMs。"));
        var minWidth = request.MinDetectedWidth.Value;
        var maxWidth = request.MaxDetectedWidth.Value;
        var stableForMs = request.StableForMs.Value;
        var pollMs = request.PollMs ?? 50;
        var minForce = request.MinForce ?? 0;
        var expectedDetected = request.ExpectedDetected ?? true;
        var stableSince = DateTimeOffset.UtcNow;

        while ((DateTimeOffset.UtcNow - stableSince).TotalMilliseconds < stableForMs)
        {
            ct.ThrowIfCancellationRequested();
            bool detected;
            double width;
            double force;
            lock (state.Sync)
            {
                width = state.GripWidth;
                force = state.GripForce;
                var widthInRange = width >= minWidth && width <= maxWidth;
                var loadDetected = state.Gripped && widthInRange &&
                                   (!request.RequireForceFeedback || force >= minForce);
                detected = expectedDetected ? loadDetected : !state.Gripped && widthInRange;
            }
            if (!detected)
                return DeviceResult<GripResult>.Fail(new(5203,
                    $"夹持校验失败：width={width}, force={force}。", Model, true, true));
            await Task.Delay(Math.Max(10, pollMs), ct);
        }

        lock (state.Sync)
        {
            options.WriteLog($"GRIPPER:{Model}", "GRIP.VERIFY_LOAD",
                $"detected=true width={state.GripWidth} force={state.GripForce} stableForMs={stableForMs}");
            return DeviceResult<GripResult>.Ok(new(expectedDetected, state.GripWidth, state.GripForce));
        }
    }
}
