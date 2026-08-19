using Kunling.RobotClient.Core.Controller.Templates;

namespace Kunling.RobotClient.Actions.ServerActions;

public static class ServerActionCatalog
{
    public static ServerActionRegistration HikrobotHuayanV1() => new(
        Devices:
        [
            new("CHASSIS", "HIKROBOT", "CONFIGURED", "hikrobot-chassis", "1.0.0", null, true),
            new("ARM", "HUAYAN", "CONFIGURED", "huayan-v8", "1.0.0", null, true)
        ],
        // 注册动作名称全部来自 Core 的 MainActionCatalog，协议层不再维护第二份动作清单。
        Capabilities: MainActionCatalog.All.Select(x => Capability(x.ActionType, Features(x.Action))).ToArray(),
        ExecutionModes: [ExecutionMode.Package]);

    private static IReadOnlyList<string> Features(MainAction action) => action switch
    {
        MainAction.Move => ["ARRIVAL_EVIDENCE"],
        MainAction.ArmPick => ["VISION_CORRECTED", "GRIP_VERIFY", "RESOLVED_STEPS"],
        MainAction.ArmPlace => ["VISION_VERIFY_PLACEMENT", "GRIP_VERIFY", "RESOLVED_STEPS"],
        MainAction.ArmPickBatch or MainAction.ArmPlaceBatch => ["BATCH", "SLOT_RESUME", "RESOLVED_STEPS"],
        MainAction.ArmHome => ["SAFE_HOME"],
        MainAction.VisionCapture => ["IMAGE_EVIDENCE"],
        _ => []
    };

    private static ActionCapability Capability(string type, IReadOnlyList<string> features) =>
        new(type, "1.0", $"sha256:{type.ToLowerInvariant().Replace('.', '-')}-schema-v1", ExecutionMode.Package, features, 1_000, 300_000);
}
