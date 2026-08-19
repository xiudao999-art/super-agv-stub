namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>
/// 服务器下发 L2 模板的通用结构校验器。MainAction 的 Phase 数量、顺序和业务组合
/// 完全由服务器配置决定；客户端只验证每个 Phase 自身结构及引用关系是否合法。
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
            if (!Enum.IsDefined(phase.OnFail)) errors.Add($"phase {phase.PhaseId} 的 onFail 无效。");
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

        return errors;
    }

    public static void EnsureValid(MainActionTemplate? action)
    {
        var errors = Validate(action);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
    }

    /// <summary>
    /// 校验断点恢复包。恢复包只包含安全重入点及其后续 phases，因此不再要求完整标准模板数量；
    /// 但每个 phase 的基本结构、枚举、Gate/SKIP 互锁和 retryFrom 指向仍必须合法。
    /// </summary>
    public static IReadOnlyList<string> ValidateResume(MainActionTemplate? action)
    {
        if (action is null) return ["恢复 MainAction 不能为空。"];
        if (action.Phases.Count == 0) return ["恢复 MainAction phases 不能为空。"];
        var errors = new List<string>();
        if (!MainActionCatalog.All.Any(x => x.Action == action.ActionType))
            errors.Add($"不支持的 MainAction：{action.ActionType}。");
        foreach (var duplicate in action.Phases.GroupBy(x => x.PhaseId, StringComparer.OrdinalIgnoreCase)
                     .Where(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() > 1))
            errors.Add(string.IsNullOrWhiteSpace(duplicate.Key) ? "phaseId 不能为空。" : $"phaseId 重复：{duplicate.Key}。");
        foreach (var phase in action.Phases)
        {
            if (!Enum.IsDefined(phase.SubAction)) errors.Add($"phase {phase.PhaseId} 的 subAction 无效。");
            if (phase.Parameters is null) errors.Add($"phase {phase.PhaseId} 的 params 不能为空。");
            if (!Enum.IsDefined(phase.OnFail)) errors.Add($"phase {phase.PhaseId} 的 onFail 无效。");
            if (phase.Gate && phase.OnFail == PhaseFailAction.SKIP)
                errors.Add($"闸门 phase {phase.PhaseId} 不允许 SKIP。");
            var retryFrom = ReadString(phase, "retryFromPhaseId");
            if (string.IsNullOrWhiteSpace(retryFrom)) continue;
            var targetIndex = action.Phases.FindIndex(x => x.PhaseId.Equals(retryFrom,
                StringComparison.OrdinalIgnoreCase));
            if (targetIndex < 0 || targetIndex >= action.Phases.IndexOf(phase))
                errors.Add($"恢复包 phase {phase.PhaseId} 的 retryFromPhaseId={retryFrom} 不在此前 phases 中。");
        }
        return errors;
    }


    private static string? ReadString(PhaseActionTemplate phase, string key)
    {
        try { return phase.Parameters?[key]?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
    }

}
