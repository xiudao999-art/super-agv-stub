using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.Actions;

/// <summary>ARM.PICK：单载具取料，按安全位、接近位、视觉、夹爪、取料位和撤离位编排。</summary>
public sealed class ArmPickAction : MainActionTemplate
{
    public ArmPickAction() => Initialize(new ArmPickRequest(string.Empty));
    public ArmPickAction(ArmPickRequest request) => Initialize(request);

    private void Initialize(ArmPickRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionType = MainAction.ArmPick;
        Phases = ActionPhaseFactory.CreatePickPhases(string.Empty, request.Station, request.Point,
            request.GraspProfile);
    }

    public ArmPickRequest ResolveRequest()
    {
        var p = ActionPhaseFactory.RequiredParameters(this, "safe");
        return new(ActionPhaseFactory.RequiredString(p, "station"),
            ActionPhaseFactory.OptionalString(p, "point"),
            GraspProfile: ActionPhaseFactory.RequiredString(p, "graspProfile"));
    }
}

