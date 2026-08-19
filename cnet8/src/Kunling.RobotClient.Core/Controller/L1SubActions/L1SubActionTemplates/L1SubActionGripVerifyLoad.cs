using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.L1SubActions.L1SubActionTemplates;

/// <summary>GRIP_VERIFY_LOAD：根据宽度、夹持力和稳定时间验证是否抓到载具。</summary>
public sealed class L1SubActionGripVerifyLoad : PhaseActionTemplate
{
    public L1SubActionGripVerifyLoad()
    {
        PhaseId = "verifyLoad"; SubAction = SubAction.GRIP_VERIFY_LOAD; Enabled = true;
        Parameters = new JsonObject
        {
            ["graspProfile"] = "DEFAULT_PICK", ["minDetectedWidth"] = 5, ["maxDetectedWidth"] = 65,
            ["holdCheckMs"] = 500, ["pollMs"] = 50, ["requireForceFeedback"] = true,
            ["minForce"] = 1, ["expectedDetected"] = true
        };
        Gate = true; OnFail = PhaseFailAction.VERIFY_BEFORE_RETRY;
    }

    public L1SubActionGripVerifyLoad(string phaseId, GripRequest request,
        PhaseFailAction onFail = PhaseFailAction.VERIFY_BEFORE_RETRY) : this()
    { PhaseId = L1SubActionGripOpen.RequireText(phaseId, nameof(phaseId)); SetRequest(request); OnFail = onFail; }

    public void SetRequest(GripRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Parameters = new JsonObject
        {
            ["graspProfile"] = request.Profile, ["minDetectedWidth"] = request.MinDetectedWidth,
            ["maxDetectedWidth"] = request.MaxDetectedWidth, ["holdCheckMs"] = request.StableForMs,
            ["pollMs"] = request.PollMs, ["requireForceFeedback"] = request.RequireForceFeedback,
            ["minForce"] = request.MinForce, ["expectedDetected"] = request.ExpectedDetected
        };
    }

    public static GripRequest ResolveRequest(PhaseActionTemplate phase, string? profileOverride = null)
    {
        if (phase.SubAction != SubAction.GRIP_VERIFY_LOAD) throw new InvalidDataException($"phase {phase.PhaseId} 不是 GRIP_VERIFY_LOAD。");
        var p = phase.Parameters ?? throw new InvalidDataException($"phase {phase.PhaseId} 缺少 params。");
        return new GripRequest(Profile: profileOverride ?? L1SubActionGripOpen.Text(p, "graspProfile"),
            MinDetectedWidth: L1SubActionGripOpen.Number(p, "minDetectedWidth", 5),
            MaxDetectedWidth: L1SubActionGripOpen.Number(p, "maxDetectedWidth", 65),
            StableForMs: L1SubActionGripOpen.Integer(p, "holdCheckMs", 500),
            PollMs: L1SubActionGripOpen.Integer(p, "pollMs", 50),
            RequireForceFeedback: L1SubActionGripOpen.Boolean(p, "requireForceFeedback", true),
            MinForce: L1SubActionGripOpen.Number(p, "minForce", 1),
            ExpectedDetected: L1SubActionGripOpen.Boolean(p, "expectedDetected", true));
    }
}
