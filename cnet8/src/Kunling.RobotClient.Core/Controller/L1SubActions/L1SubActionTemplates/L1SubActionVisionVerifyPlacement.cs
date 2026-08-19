using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.L1SubActions.L1SubActionTemplates;

/// <summary>VISION_VERIFY_PLACEMENT：放料前后确认目标位可放及物料已经放置到位。</summary>
public sealed class L1SubActionVisionVerifyPlacement : PhaseActionTemplate
{
    public L1SubActionVisionVerifyPlacement()
    {
        var defaults = new L1SubActionVisionVerifyMaterial(SubAction.VISION_VERIFY_PLACEMENT,
            "verifyPlacement", "PLACEMENT");
        PhaseId = defaults.PhaseId; SubAction = defaults.SubAction; Enabled = defaults.Enabled;
        Parameters = defaults.Parameters; Gate = defaults.Gate; OnFail = defaults.OnFail;
    }

    public L1SubActionVisionVerifyPlacement(string phaseId, VisionRequest request,
        PhaseFailAction onFail = PhaseFailAction.ABORT) : this()
    {
        PhaseId = L1SubActionGripOpen.RequireText(phaseId, nameof(phaseId));
        Parameters = L1SubActionVisionVerifyMaterial.ToParameters(request);
        OnFail = onFail;
    }

    public static VisionRequest ResolveRequest(PhaseActionTemplate phase, string? fallbackStation = null) =>
        L1SubActionVisionVerifyMaterial.ResolveRequest(phase, fallbackStation);
}
