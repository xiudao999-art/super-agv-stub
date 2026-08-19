using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.Actions;

/// <summary>ARM.PICK_BATCH：一次靠位连续取 N 个；槽位顺序由 orderPolicy 决定，可用 cacheAssign 覆盖点位。</summary>
public sealed class ArmPickBatchAction : MainActionTemplate
{
    public ArmPickBatchAction() => Initialize(new ArmPickBatchRequest(string.Empty, []));
    public ArmPickBatchAction(ArmPickBatchRequest request) => Initialize(request);
    public ArmPickBatchAction(MainActionTemplate singlePickTemplate, ArmPickBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionType = MainAction.ArmPickBatch;
        Phases = ActionPhaseFactory.CreateBatchPhases(singlePickTemplate, request.Station,
            request.Slots, request.OrderPolicy, request.CacheAssign,
            request.CacheAssignMode, request.AvailableCacheSlots);
    }

    private void Initialize(ArmPickBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionType = MainAction.ArmPickBatch;
        var ordered = ActionPhaseFactory.OrderSlots(request.Slots, request.OrderPolicy);
        Phases = [];
        foreach (var slot in ordered)
        {
            var point = slot.Point ?? slot.SlotId;
            Phases.AddRange(ActionPhaseFactory.CreatePickPhases($"{slot.SlotId}.", request.Station, point,
                "DEFAULT_PICK", request.OrderPolicy, request.CacheAssign));
        }
    }
}
