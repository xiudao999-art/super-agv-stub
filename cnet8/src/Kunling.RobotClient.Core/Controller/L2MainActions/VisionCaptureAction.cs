using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.Actions;

/// <summary>VISION.CAPTURE：按相机档案执行现场拍照。</summary>
public sealed class VisionCaptureAction : MainActionTemplate
{
    public VisionCaptureAction() => Initialize(new VisionRequest());
    public VisionCaptureAction(VisionRequest request) => Initialize(request);

    private void Initialize(VisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionType = MainAction.VisionCapture;
        Phases =
        [
            ActionPhaseFactory.Phase("capture", SubAction.VISION_CAPTURE, new()
            {
                ["station"] = request.Station,
                ["recipe"] = request.Recipe ?? "DEFAULT_CAPTURE",
                ["cameraId"] = request.CameraId ?? "CAM01",
                ["exposureMs"] = request.ExposureMs,
                ["gain"] = request.Gain,
                ["timeoutMs"] = request.TimeoutMs ?? 5000,
                ["outputFormat"] = request.OutputFormat ?? "png"
            }, true, PhaseFailAction.ABORT)
        ];
    }
}

