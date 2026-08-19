using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.Actions;

/// <summary>构造代码化主动作 Phase 的内部工厂，保证单次和批量动作使用同一套编排。</summary>
internal static class ActionPhaseFactory
{
    private static readonly HashSet<string> BatchEntryPhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "safe", "approach"
    };

    internal static List<PhaseActionTemplate> CreatePickPhases(string prefix, string station, string? point,
        string graspProfile, string? orderPolicy = null, IReadOnlyDictionary<string, string>? cacheAssign = null)
    {
        JsonObject Common(string poseRole) => new()
        {
            ["station"] = station, ["point"] = point, ["poseRole"] = poseRole,
            ["poseSet"] = $"{station}:{point}", ["graspProfile"] = graspProfile,
            ["arrival"] = "stationProfile", ["motion"] = "stationProfile",
            ["orderPolicy"] = orderPolicy, ["cacheAssign"] = ToNode(cacheAssign)
        };
        return
        [
            Phase(prefix + "safe", SubAction.MOVE_TO_POSE, Common("SAFE"), false, PhaseFailAction.RETRY_PHASE),
            Phase(prefix + "approach", SubAction.MOVE_TO_POSE, Common("APPROACH"), false, PhaseFailAction.RETRY_PHASE),
            Phase(prefix + "verifyMaterial", SubAction.VISION_VERIFY_MATERIAL,
                new() { ["station"] = station, ["point"] = point, ["recipe"] = "MATERIAL" }, true, PhaseFailAction.ABORT),
            Phase(prefix + "preOpen", SubAction.GRIP_OPEN,
                new() { ["graspProfile"] = graspProfile }, false, PhaseFailAction.RETRY_PHASE),
            Phase(prefix + "toPick", SubAction.MOVE_TO_POSE, Common("PICK"), false, PhaseFailAction.VERIFY_BEFORE_RETRY),
            Phase(prefix + "close", SubAction.GRIP_CLOSE,
                new() { ["graspProfile"] = graspProfile }, false, PhaseFailAction.RETRY_PHASE),
            Phase(prefix + "verifyLoad", SubAction.GRIP_VERIFY_LOAD,
                new() { ["graspProfile"] = graspProfile, ["expectedDetected"] = true }, true, PhaseFailAction.VERIFY_BEFORE_RETRY),
            Phase(prefix + "retreat", SubAction.MOVE_TO_POSE, Common("RETREAT"), false, PhaseFailAction.RETRY_PHASE)
        ];
    }

    internal static List<PhaseActionTemplate> CreatePlacePhases(string prefix, string station, string? point,
        string releaseProfile, string? orderPolicy = null, IReadOnlyDictionary<string, string>? cacheAssign = null)
    {
        JsonObject Common(string poseRole) => new()
        {
            ["station"] = station, ["point"] = point, ["poseRole"] = poseRole,
            ["poseSet"] = $"{station}:{point}", ["releaseProfile"] = releaseProfile,
            ["arrival"] = "stationProfile", ["motion"] = "stationProfile",
            ["orderPolicy"] = orderPolicy, ["cacheAssign"] = ToNode(cacheAssign)
        };
        return
        [
            Phase(prefix + "safe", SubAction.MOVE_TO_POSE, Common("SAFE"), false, PhaseFailAction.RETRY_PHASE),
            Phase(prefix + "approach", SubAction.MOVE_TO_POSE, Common("APPROACH"), false, PhaseFailAction.RETRY_PHASE),
            Phase(prefix + "toPlace", SubAction.MOVE_TO_POSE, Common("PLACE"), false, PhaseFailAction.VERIFY_BEFORE_RETRY),
            Phase(prefix + "release", SubAction.GRIP_OPEN,
                new() { ["releaseProfile"] = releaseProfile }, false, PhaseFailAction.RETRY_PHASE),
            Phase(prefix + "verifyPlacement", SubAction.VISION_VERIFY_PLACEMENT,
                new() { ["station"] = station, ["point"] = point, ["recipe"] = "PLACEMENT" }, true, PhaseFailAction.ABORT),
            Phase(prefix + "retreat", SubAction.MOVE_TO_POSE, Common("RETREAT"), false, PhaseFailAction.RETRY_PHASE)
        ];
    }

    internal static PhaseActionTemplate Phase(string id, SubAction subAction, JsonObject parameters,
        bool gate, PhaseFailAction onFail) => new()
    {
        PhaseId = id, SubAction = subAction, Enabled = true, Parameters = parameters, Gate = gate, OnFail = onFail
    };

    internal static IReadOnlyList<BatchSlot> OrderSlots(IReadOnlyList<BatchSlot> slots, string orderPolicy) =>
        orderPolicy.ToUpperInvariant() switch
        {
            "INPUT" or "EXPLICIT" => slots.OrderBy(x => x.ExplicitOrder ?? int.MaxValue)
                .ThenBy(x => Array.IndexOf(slots.ToArray(), x)).ToArray(),
            "RANK_ASC" => slots.OrderBy(x => x.Rank ?? Rank(x.SlotId)).ToArray(),
            "RANK_DESC" => slots.OrderByDescending(x => x.Rank ?? Rank(x.SlotId)).ToArray(),
            "ROW_FRONT_FIRST" => slots.OrderBy(x => x.Row).ThenBy(x => x.Depth ?? int.MaxValue)
                .ThenBy(x => x.Rank ?? Rank(x.SlotId)).ToArray(),
            "ROW_BACK_FIRST" => slots.OrderBy(x => x.Row).ThenByDescending(x => x.Depth ?? int.MinValue)
                .ThenBy(x => x.Rank ?? Rank(x.SlotId)).ToArray(),
            "NEAREST" => slots.OrderBy(x => x.Distance ?? double.MaxValue).ToArray(),
            _ => throw new ArgumentException($"不支持 orderPolicy：{orderPolicy}", nameof(orderPolicy))
        };

    /// <summary>
    /// 把单次 PICK/PLACE 模板展开为批量模板：公共靠位阶段只执行一次，槽位核心阶段循环 N 次，
    /// 最后只执行一次 retreat。每个槽位 phase 都写入自己的 station/point 与批量分配证据。
    /// </summary>
    internal static List<PhaseActionTemplate> CreateBatchPhases(MainActionTemplate singleTemplate,
        string station, IReadOnlyList<BatchSlot> slots, string orderPolicy,
        IReadOnlyDictionary<string, string>? cacheAssign, string cacheAssignMode = "EXPLICIT_MAP",
        IReadOnlyList<string>? availableCacheSlots = null)
    {
        ArgumentNullException.ThrowIfNull(singleTemplate);
        var ordered = OrderSlots(slots, orderPolicy);
        if (ordered.Count == 0) return [];
        var resolvedCache = ResolveCacheAssignments(ordered, cacheAssignMode, cacheAssign, availableCacheSlots);

        var entry = singleTemplate.Phases.Where(x => BatchEntryPhases.Contains(x.PhaseId)).ToArray();
        var retreat = singleTemplate.Phases.Where(x => x.PhaseId.Equals("retreat", StringComparison.OrdinalIgnoreCase)).ToArray();
        var loop = singleTemplate.Phases.Where(x => !BatchEntryPhases.Contains(x.PhaseId) &&
            !x.PhaseId.Equals("retreat", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entry.Length == 0 || retreat.Length == 0 || loop.Length == 0)
            throw new ArgumentException($"{singleTemplate.ActionType.ToActionType()} 模板缺少批量展开所需的 entry/loop/retreat 阶段。");

        var result = new List<PhaseActionTemplate>();
        var firstPoint = ResolvePoint(ordered[0]);
        result.AddRange(entry.Select(x => CloneForBatch(x, x.PhaseId, station, firstPoint,
            orderPolicy, cacheAssign, null)));

        foreach (var slot in ordered)
        {
            var point = ResolvePoint(slot);
            result.AddRange(loop.Select(x => CloneForBatch(x, $"{slot.SlotId}.{x.PhaseId}", station,
                point, orderPolicy, resolvedCache, slot.SlotId,
                resolvedCache.TryGetValue(slot.SlotId, out var cacheSlot) ? cacheSlot : null)));
        }

        var lastPoint = ResolvePoint(ordered[^1]);
        result.AddRange(retreat.Select(x => CloneForBatch(x, x.PhaseId, station, lastPoint,
            orderPolicy, cacheAssign, null)));
        return result;

        string ResolvePoint(BatchSlot slot) => slot.Point ?? slot.SlotId;
    }

    private static PhaseActionTemplate CloneForBatch(PhaseActionTemplate source, string phaseId,
        string station, string point, string orderPolicy,
        IReadOnlyDictionary<string, string>? cacheAssign, string? slotId, string? cacheSlot = null)
    {
        var parameters = source.Parameters?.DeepClone().AsObject() ?? new JsonObject();
        parameters["station"] = station;
        parameters["point"] = point;
        parameters["orderPolicy"] = orderPolicy;
        parameters["cacheAssign"] = ToNode(cacheAssign);
        parameters["slotId"] = slotId;
        parameters["cacheSlot"] = cacheSlot;
        parameters["poseRef"] = $"{station}:{point}:{parameters["poseRole"]?.GetValue<string>() ?? source.PhaseId}";
        if (slotId is not null && parameters["retryFromPhaseId"]?.GetValue<string>() is { Length: > 0 } retryFrom)
            parameters["retryFromPhaseId"] = $"{slotId}.{retryFrom}";
        return new PhaseActionTemplate
        {
            PhaseId = phaseId,
            SubAction = source.SubAction,
            Enabled = source.Enabled,
            Parameters = parameters,
            Gate = source.Gate,
            OnFail = source.OnFail
        };
    }

    private static IReadOnlyDictionary<string, string> ResolveCacheAssignments(
        IReadOnlyList<BatchSlot> ordered, string mode, IReadOnlyDictionary<string, string>? explicitMap,
        IReadOnlyList<string>? available)
    {
        mode = mode.ToUpperInvariant();
        if (mode == "EXPLICIT_MAP")
        {
            var map = explicitMap ?? new Dictionary<string, string>();
            var missing = ordered.FirstOrDefault(x => !map.ContainsKey(x.SlotId));
            if (missing is not null && map.Count > 0)
                throw new ArgumentException($"cacheAssign 缺少槽位 {missing.SlotId} 的映射。");
            return map;
        }
        if (mode is not ("SEQUENTIAL" or "AUTO"))
            throw new ArgumentException($"不支持 cacheAssignMode：{mode}");

        var candidates = available?.ToArray() ??
            Enumerable.Range(1, ordered.Count).Select(x => $"CACHE_{x:00}").ToArray();
        if (candidates.Length < ordered.Count)
            throw new ArgumentException("可用 cache slot 数量小于批量槽位数量。");
        return ordered.Select((slot, index) => new { slot.SlotId, Cache = candidates[index] })
            .ToDictionary(x => x.SlotId, x => x.Cache, StringComparer.OrdinalIgnoreCase);
    }

    internal static JsonObject RequiredParameters(MainActionTemplate action, string phaseId) =>
        action.Phases.FirstOrDefault(x => x.PhaseId == phaseId)?.Parameters
        ?? throw new InvalidDataException($"{action.ActionType.ToActionType()} 缺少 phase {phaseId} 或 params。");

    internal static string RequiredString(JsonObject parameters, string key) =>
        OptionalString(parameters, key) is { Length: > 0 } value
            ? value : throw new InvalidDataException($"参数 {key} 不能为空。");

    internal static string? OptionalString(JsonObject parameters, string key) =>
        parameters[key]?.GetValue<string>();

    private static int Rank(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var rank) ? rank : int.MaxValue;
    }

    private static JsonNode? ToNode<T>(T value) => value is null ? null : JsonSerializer.SerializeToNode(value);
}
