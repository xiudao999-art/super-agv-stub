using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.Actions;

/// <summary>ARM.HOME：移动到 HOME poseSet，并用 ARM_VERIFY_HOME 验证归零状态。</summary>
public sealed class ArmHomeAction : MainActionTemplate
{
    public ArmHomeAction()
    {
        ActionType = MainAction.ArmHome;
        Phases =
        [
            ActionPhaseFactory.Phase("home", SubAction.MOVE_TO_POSE,
                new() { ["station"] = "GLOBAL", ["poseRole"] = "HOME", ["poseSet"] = "HOME" },
                false, PhaseFailAction.VERIFY_BEFORE_RETRY),
            ActionPhaseFactory.Phase("verifyHome", SubAction.ARM_VERIFY_HOME,
                new() { ["expectedHomed"] = true }, true, PhaseFailAction.ABORT)
        ];
    }
}

