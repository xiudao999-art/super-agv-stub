namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>
/// 服务器下发 L2 模板的安全边界。只有符合认证动作结构的 phase 序列才能进入设备层，
/// 防止通过修改 JSON 绕过视觉、夹持确认、底盘互锁或最终到位闸门。
/// </summary>
public static class MainActionTemplateValidator
{
    public static IReadOnlyList<string> Validate(MainActionTemplate? action)
    {
        var errors = new List<string>();
        if (action is null) return ["MainAction 不能为空。"];
        if (action.Phases.Count == 0) return [$"{action.ActionType.ToActionType()} phases 不能为空。"];

        foreach (var duplicate in action.Phases.GroupBy(x => x.PhaseId, StringComparer.OrdinalIgnoreCase)
                     .Where(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() > 1))
            errors.Add(string.IsNullOrWhiteSpace(duplicate.Key) ? "phaseId 不能为空。" : $"phaseId 重复：{duplicate.Key}。");

        foreach (var phase in action.Phases)
        {
            if (!Enum.IsDefined(phase.SubAction)) errors.Add($"phase {phase.PhaseId} 的 subAction 无效。");
            if (phase.Parameters is null) errors.Add($"phase {phase.PhaseId} 的 params 不能为空。");
            if (phase.Gate && phase.OnFail == PhaseFailAction.SKIP)
                errors.Add($"闸门 phase {phase.PhaseId} 不允许 SKIP。");
            var retryFrom = ReadString(phase, "retryFromPhaseId");
            if (!string.IsNullOrWhiteSpace(retryFrom))
            {
                var targetIndex = action.Phases.FindIndex(x =>
                    x.PhaseId.Equals(retryFrom, StringComparison.OrdinalIgnoreCase));
                var phaseIndex = action.Phases.IndexOf(phase);
                if (targetIndex < 0 || targetIndex >= phaseIndex)
                    errors.Add($"phase {phase.PhaseId} 的 retryFromPhaseId={retryFrom} 必须指向此前 phase。");
            }
        }

        switch (action.ActionType)
        {
            case MainAction.Move:
                ValidateExact(action, [SubAction.MOVE_TO_MAP_POINT], errors);
                RequireGate(action, SubAction.MOVE_TO_MAP_POINT, errors);
                break;
            case MainAction.ArmPick:
                ValidateExact(action, PickSequence(), errors);
                RequireGate(action, SubAction.VISION_VERIFY_MATERIAL, errors);
                RequireGate(action, SubAction.GRIP_VERIFY_LOAD, errors);
                break;
            case MainAction.ArmPlace:
                ValidateExact(action, PlaceSequence(), errors);
                RequireGate(action, SubAction.VISION_VERIFY_PLACEMENT, errors, expectedCount: 2);
                RequireGate(action, SubAction.GRIP_VERIFY_LOAD, errors);
                RequireSafeEmptyVerification(action, errors);
                break;
            case MainAction.ArmPickBatch:
                ValidateBatch(action, PickCoreSequence(), errors);
                break;
            case MainAction.ArmPlaceBatch:
                ValidateBatch(action, PlaceCoreSequence(), errors);
                RequireSafeEmptyVerification(action, errors);
                break;
            case MainAction.ArmHome:
                ValidateExact(action,
                    [SubAction.CHASSIS_VERIFY_STOPPED, SubAction.MOVE_TO_POSE, SubAction.ARM_VERIFY_HOME], errors);
                RequireGate(action, SubAction.CHASSIS_VERIFY_STOPPED, errors);
                RequireGate(action, SubAction.ARM_VERIFY_HOME, errors);
                break;
            case MainAction.VisionCapture:
                ValidateExact(action, [SubAction.VISION_CAPTURE], errors);
                RequireGate(action, SubAction.VISION_CAPTURE, errors);
                break;
            default:
                errors.Add($"不支持的 MainAction：{action.ActionType}。");
                break;
        }
        return errors;
    }

    private static void RequireSafeEmptyVerification(MainActionTemplate action, ICollection<string> errors)
    {
        foreach (var phase in action.Phases.Where(x => x.SubAction == SubAction.GRIP_VERIFY_LOAD))
        {
            if (phase.OnFail != PhaseFailAction.VERIFY_BEFORE_RETRY)
                errors.Add($"PLACE 的 {phase.PhaseId} 必须使用 VERIFY_BEFORE_RETRY，禁止直接重复释放。");
            if (string.IsNullOrWhiteSpace(ReadString(phase, "retryFromPhaseId")))
                errors.Add($"PLACE 的 {phase.PhaseId} 必须配置 retryFromPhaseId。");
        }
    }

    public static void EnsureValid(MainActionTemplate? action)
    {
        var errors = Validate(action);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
    }

    private static void ValidateExact(MainActionTemplate action, IReadOnlyList<SubAction> expected,
        ICollection<string> errors)
    {
        if (action.Phases.Count != expected.Count)
        {
            errors.Add($"{action.ActionType.ToActionType()} phase 数量应为 {expected.Count}，实际为 {action.Phases.Count}。");
            return;
        }
        for (var i = 0; i < expected.Count; i++)
            if (action.Phases[i].SubAction != expected[i])
                errors.Add($"{action.ActionType.ToActionType()} 第 {i + 1} 个 phase 应为 " +
                    $"{expected[i].ToProtocolName()}，实际为 {action.Phases[i].SubAction.ToProtocolName()}。");
    }

    private static void ValidateBatch(MainActionTemplate action, IReadOnlyList<SubAction> core,
        ICollection<string> errors)
    {
        if (action.Phases.Count < core.Count + 3)
        { errors.Add($"{action.ActionType.ToActionType()} 没有完整的靠位、槽位子序列和撤离阶段。"); return; }
        if (action.Phases[0].SubAction != SubAction.MOVE_TO_POSE ||
            action.Phases[1].SubAction != SubAction.MOVE_TO_POSE ||
            action.Phases[^1].SubAction != SubAction.MOVE_TO_POSE)
            errors.Add("批量动作必须以 SAFE/APPROACH 开始并以 RETREAT 结束。");

        var slotGroups = action.Phases.Skip(2).SkipLast(1)
            .GroupBy(x => ReadString(x, "slotId"), StringComparer.OrdinalIgnoreCase).ToArray();
        if (slotGroups.Any(x => string.IsNullOrWhiteSpace(x.Key)))
            errors.Add("批量循环 phase 必须携带 slotId。");
        foreach (var slot in slotGroups.Where(x => !string.IsNullOrWhiteSpace(x.Key)))
        {
            var phases = slot.ToArray();
            if (phases.Length != core.Count || !phases.Select(x => x.SubAction).SequenceEqual(core))
                errors.Add($"slot {slot.Key} 的子序列不符合认证模板。");
            foreach (var gate in phases.Where(x => x.SubAction is SubAction.VISION_VERIFY_MATERIAL or
                         SubAction.VISION_VERIFY_PLACEMENT or SubAction.GRIP_VERIFY_LOAD))
                if (!gate.Gate) errors.Add($"slot {slot.Key} 的 phase {gate.PhaseId} 必须是 gate。");
        }
    }

    private static void RequireGate(MainActionTemplate action, SubAction subAction,
        ICollection<string> errors, int expectedCount = 1)
    {
        var phases = action.Phases.Where(x => x.SubAction == subAction).ToArray();
        if (phases.Length != expectedCount)
            errors.Add($"{action.ActionType.ToActionType()} 必须包含 {expectedCount} 个 {subAction.ToProtocolName()}。");
        foreach (var phase in phases)
            if (!phase.Gate) errors.Add($"phase {phase.PhaseId} 必须是 gate。");
    }

    private static string? ReadString(PhaseActionTemplate phase, string key)
    {
        try { return phase.Parameters?[key]?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
    }

    private static IReadOnlyList<SubAction> PickSequence() =>
        [SubAction.MOVE_TO_POSE, SubAction.MOVE_TO_POSE, .. PickCoreSequence(), SubAction.MOVE_TO_POSE];
    private static IReadOnlyList<SubAction> PickCoreSequence() =>
        [SubAction.VISION_VERIFY_MATERIAL, SubAction.GRIP_OPEN, SubAction.MOVE_TO_POSE,
            SubAction.GRIP_CLOSE, SubAction.GRIP_VERIFY_LOAD];
    private static IReadOnlyList<SubAction> PlaceSequence() =>
        [SubAction.MOVE_TO_POSE, SubAction.MOVE_TO_POSE, .. PlaceCoreSequence(), SubAction.MOVE_TO_POSE];
    private static IReadOnlyList<SubAction> PlaceCoreSequence() =>
        [SubAction.VISION_VERIFY_PLACEMENT, SubAction.MOVE_TO_POSE, SubAction.GRIP_OPEN,
            SubAction.GRIP_VERIFY_LOAD, SubAction.VISION_VERIFY_PLACEMENT];
}
