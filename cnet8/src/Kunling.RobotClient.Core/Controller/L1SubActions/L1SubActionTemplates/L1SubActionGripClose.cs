using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.L1SubActions.L1SubActionTemplates;

/// <summary>GRIP_CLOSE：合拢到目标夹持宽度与夹持力。</summary>
public sealed class L1SubActionGripClose : PhaseActionTemplate
{
    public L1SubActionGripClose()
    {
        PhaseId = "close"; SubAction = SubAction.GRIP_CLOSE; Enabled = true;
        Parameters = new JsonObject
        {
            ["graspProfile"] = "DEFAULT_PICK", ["gripForce"] = 35, ["targetWidthMm"] = 25,
            ["holdMs"] = 150, ["minDetectedWidth"] = 5, ["maxDetectedWidth"] = 65
        };
        Gate = false; OnFail = PhaseFailAction.RETRY_PHASE;
    }

    public L1SubActionGripClose(string phaseId, GripRequest request,
        PhaseFailAction onFail = PhaseFailAction.RETRY_PHASE) : this()
    { PhaseId = L1SubActionGripOpen.RequireText(phaseId, nameof(phaseId)); SetRequest(request); OnFail = onFail; }

    public void SetRequest(GripRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Parameters = new JsonObject
        {
            ["graspProfile"] = request.Profile, ["gripForce"] = request.Force,
            ["targetWidthMm"] = request.Width, ["holdMs"] = request.HoldMs,
            ["minDetectedWidth"] = request.MinDetectedWidth, ["maxDetectedWidth"] = request.MaxDetectedWidth
        };
    }

    public static GripRequest ResolveRequest(PhaseActionTemplate phase, string? profileOverride = null)
    {
        if (phase.SubAction != SubAction.GRIP_CLOSE) throw new InvalidDataException($"phase {phase.PhaseId} 不是 GRIP_CLOSE。");
        var p = phase.Parameters ?? throw new InvalidDataException($"phase {phase.PhaseId} 缺少 params。");
        return new GripRequest(L1SubActionGripOpen.Number(p, "gripForce", 35),
            L1SubActionGripOpen.Number(p, "targetWidthMm", 25),
            profileOverride ?? L1SubActionGripOpen.Text(p, "graspProfile"),
            L1SubActionGripOpen.Integer(p, "holdMs", 150), L1SubActionGripOpen.Number(p, "minDetectedWidth", 5),
            L1SubActionGripOpen.Number(p, "maxDetectedWidth", 65));
    }
}
