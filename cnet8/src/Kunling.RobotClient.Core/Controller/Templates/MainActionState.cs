using System.Text.Json.Serialization;

namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>
/// L2 主动作生命周期。
/// IDLE 表示没有动作；BUSY 表示新命令因机器人占用而被拒绝，不属于已接收动作的生命周期。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MainActionState
{
    Idle,
    Accepted,
    Running,
    Hang,
    Error,
    Finished,
    Busy
}
