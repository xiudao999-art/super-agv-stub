using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.Actions;

/// <summary>ARM.PLACE_BATCH：一次靠位连续放 N 个；槽位顺序由 orderPolicy 决定，可用 cacheAssign 覆盖点位。</summary>
public sealed class ArmPlaceBatchAction : MainActionTemplate
{
    public ArmPlaceBatchAction() => Initialize(new ArmPlaceBatchRequest(string.Empty, []));
    public ArmPlaceBatchAction(ArmPlaceBatchRequest request) => Initialize(request);
    public ArmPlaceBatchAction(MainActionTemplate singlePlaceTemplate, ArmPlaceBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionType = MainAction.ArmPlaceBatch;
        Phases = ActionPhaseFactory.CreateBatchPhases(singlePlaceTemplate, request.Station,
            request.Slots, request.OrderPolicy, request.CacheAssign,
            request.CacheAssignMode, request.AvailableCacheSlots);
    }

    private void Initialize(ArmPlaceBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionType = MainAction.ArmPlaceBatch;
        var ordered = ActionPhaseFactory.OrderSlots(request.Slots, request.OrderPolicy);
        Phases = [];
        foreach (var slot in ordered)
        {
            var point = slot.Point ?? slot.SlotId;
            Phases.AddRange(ActionPhaseFactory.CreatePlacePhases($"{slot.SlotId}.", request.Station, point,
                request.ReleaseProfile, request.OrderPolicy, request.CacheAssign));
        }
    }
}
