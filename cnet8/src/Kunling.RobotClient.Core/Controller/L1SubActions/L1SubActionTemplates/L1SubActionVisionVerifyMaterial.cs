using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.L1SubActions.L1SubActionTemplates;

/// <summary>VISION_VERIFY_MATERIAL：取料前确认目标位存在正确物料并完成识别。</summary>
public sealed class L1SubActionVisionVerifyMaterial : PhaseActionTemplate
{
    public L1SubActionVisionVerifyMaterial() : this(SubAction.VISION_VERIFY_MATERIAL, "verifyMaterial", "MATERIAL") { }
    internal L1SubActionVisionVerifyMaterial(SubAction action, string phaseId, string recipe)
    {
        PhaseId = phaseId; SubAction = action; Enabled = true;
        Parameters = new JsonObject
        {
            ["station"] = null, ["recipe"] = recipe, ["cameraId"] = "CAM01",
            ["exposureMs"] = 10, ["gain"] = 1, ["timeoutMs"] = 5000,
            ["outputFormat"] = "png", ["simulatedPass"] = true
        };
        Gate = true; OnFail = PhaseFailAction.ABORT;
    }

    public L1SubActionVisionVerifyMaterial(string phaseId, VisionRequest request,
        PhaseFailAction onFail = PhaseFailAction.ABORT) : this()
    { PhaseId = L1SubActionGripOpen.RequireText(phaseId, nameof(phaseId)); SetRequest(request); OnFail = onFail; }

    public void SetRequest(VisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Parameters = ToParameters(request);
    }

    internal static JsonObject ToParameters(VisionRequest r) => new()
    {
        ["station"] = r.Station, ["recipe"] = r.Recipe, ["cameraId"] = r.CameraId,
        ["exposureMs"] = r.ExposureMs, ["gain"] = r.Gain, ["timeoutMs"] = r.TimeoutMs,
        ["outputFormat"] = r.OutputFormat, ["simulatedPass"] = r.SimulatedPass,
        ["expectedMaterial"] = r.ExpectedMaterial
    };

    public static VisionRequest ResolveRequest(PhaseActionTemplate phase, string? fallbackStation = null)
    {
        if (phase.SubAction is not (SubAction.VISION_VERIFY_MATERIAL or SubAction.VISION_VERIFY_PLACEMENT))
            throw new InvalidDataException($"phase {phase.PhaseId} 不是视觉复核子动作。");
        var p = phase.Parameters ?? throw new InvalidDataException($"phase {phase.PhaseId} 缺少 params。");
        return new VisionRequest(L1SubActionGripOpen.Text(p, "station") ?? fallbackStation,
            L1SubActionGripOpen.Text(p, "recipe") ?? phase.SubAction.ToProtocolName(), L1SubActionGripOpen.Text(p, "cameraId") ?? "CAM01",
            L1SubActionGripOpen.Number(p, "exposureMs", 10), L1SubActionGripOpen.Number(p, "gain", 1),
            L1SubActionGripOpen.Integer(p, "timeoutMs", 5000), L1SubActionGripOpen.Text(p, "outputFormat") ?? "png",
            L1SubActionGripOpen.Boolean(p, "simulatedPass", true),
            L1SubActionGripOpen.Text(p, "expectedMaterial"));
    }
}
