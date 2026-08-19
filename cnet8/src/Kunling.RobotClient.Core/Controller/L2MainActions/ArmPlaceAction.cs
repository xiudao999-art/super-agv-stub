using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.Actions;

/// <summary>ARM.PLACE：单载具放料，按安全位、接近位、放料位、释放、视觉确认和撤离编排。</summary>
public sealed class ArmPlaceAction : MainActionTemplate
{
    public ArmPlaceAction() => Initialize(new ArmPlaceRequest(string.Empty));
    public ArmPlaceAction(ArmPlaceRequest request) => Initialize(request);

    private void Initialize(ArmPlaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionType = MainAction.ArmPlace;
        Phases = ActionPhaseFactory.CreatePlacePhases(string.Empty, request.Station, request.Point,
            request.ReleaseProfile);
    }

    public ArmPlaceRequest ResolveRequest()
    {
        var p = ActionPhaseFactory.RequiredParameters(this, "safe");
        return new(ActionPhaseFactory.RequiredString(p, "station"),
            ActionPhaseFactory.OptionalString(p, "point"),
            ReleaseProfile: ActionPhaseFactory.RequiredString(p, "releaseProfile"));
    }
}

