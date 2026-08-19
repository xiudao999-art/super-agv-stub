using Kunling.RobotClient.Core.Controller.L1SubActions.L1SubActionTemplates;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Controller.Templates.MoveActionTemplates;
using Kunling.RobotClient.Core.Models;
using System.Text.Json.Serialization;

namespace Kunling.RobotClient.Core.Controller.Actions;

/// <summary>
/// L2 主动作 MOVE。
/// 上游调用 MOVE 并传入 pointName、speed、pose 和 arrival。
/// speed 使用数值表示，不再使用 NORMAL/SLOW 等速度档位字符串。
/// </summary>
public sealed class MoveAction : MainActionTemplate
{
    public MoveAction()
    {
        ActionType = MainAction.Move;
        Phases =
        [
            new L1SubActionMoveToPose()
        ];
    }

    public MoveAction(MoveRequest request) : this()
    {
        Phases[0] = new L1SubActionMoveToMapPoint(request);
    }

    /// <summary>从 MOVE_TO_MAP_POINT phase 的具体参数还原设备层 MoveRequest。</summary>
    public MoveRequest ResolveRequest()
    {
        var phase = Phases.FirstOrDefault(x => x.SubAction == SubAction.MOVE_TO_MAP_POINT)
            ?? throw new InvalidDataException("MoveAction 缺少 MOVE_TO_MAP_POINT phase。");
        return L1SubActionMoveToMapPoint.ResolveRequest(phase);
    }
}

/// <summary>服务器 COMMAND.input 使用的固定包装结构。</summary>
public sealed class MoveActionMessage
{
    [JsonPropertyName("MainAction")]
    public MoveAction MainAction { get; set; } = new();

    public MoveActionMessage() { }

    public MoveActionMessage(MoveAction mainAction) =>
        MainAction = mainAction ?? throw new ArgumentNullException(nameof(mainAction));
}
