using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Models;
using Kunling.RobotClient.Devices.Simulation;

namespace Kunling.RobotClient.Devices.Vision;

[DeviceModel("SimulatedRobotVision")]
public sealed class SimulatedRobotVision(SimulationOptions options) : IVision
{
    public string Vendor => "KUNLING";
    public string Model => "SIM_VISION";
    public Task<DeviceResult<VisionResult>> CaptureAsync(VisionRequest request, CancellationToken ct) => RunAsync("CAPTURE", request, ct);
    public Task<DeviceResult<VisionResult>> VerifyAsync(VisionRequest request, CancellationToken ct) => RunAsync("VERIFY", request, ct);

    private async Task<DeviceResult<VisionResult>> RunAsync(string action, VisionRequest request, CancellationToken ct)
    {
        var cameraId = request.CameraId ?? "CAM01";
        var exposureMs = request.ExposureMs ?? 10;
        var gain = request.Gain ?? 1;
        var timeoutMs = request.TimeoutMs ?? 5000;
        var format = request.OutputFormat ?? "png";
        var passed = request.SimulatedPass ?? true;
        options.WriteLog($"VISION:{Model}", action,
            $"camera={cameraId} recipe={request.Recipe} expectedMaterial={request.ExpectedMaterial ?? "<none>"} " +
            $"exposure={exposureMs}ms gain={gain} format={format}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try { await Task.Delay(options.ActionDelayMs + (int)Math.Ceiling(exposureMs), timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return DeviceResult<VisionResult>.Fail(new(5301, "相机采集超时。", cameraId, false, true)); }
        var uri = $"sim://camera/{cameraId}/{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{format}";
        return DeviceResult<VisionResult>.Ok(new(passed, uri));
    }
}
