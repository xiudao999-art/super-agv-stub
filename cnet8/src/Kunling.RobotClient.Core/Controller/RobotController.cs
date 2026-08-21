using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Controller.Actions;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller;

/// <summary>L2 动作入口；动作行为全部来自对应 Templates.json。</summary>
public sealed class RobotController : IRobotOperations, IRobotExecutionProgressSource
{
    private readonly string _robotId;
    private readonly IChassis _chassis;
    private readonly IArm _arm;
    private readonly ActionTemplateCatalog _templates;
    private readonly ActionTemplateExecutor _executor;
    private readonly IRobotEventSink? _events;

    /// <summary>转发模板执行器产生的实时 phase 状态。</summary>
    public event EventHandler<OperationStep>? StepChanged
    {
        add => _executor.StepChanged += value;
        remove => _executor.StepChanged -= value;
    }

    /// <summary>转发 phase 开始、完成和失败等结构化执行事实。</summary>
    public event EventHandler<OperationProgress>? ProgressChanged
    {
        add => _executor.ProgressChanged += value;
        remove => _executor.ProgressChanged -= value;
    }

    public RobotController(string robotId, IChassis chassis, IArm arm, IVision vision, IGripper gripper,
        IRfidReader rfid, IDoor door, ActionTemplateCatalog templates,
        Action<string>? templateLogger = null, IRobotEventSink? events = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(robotId);
        _robotId = robotId;
        _chassis = chassis;
        _arm = arm;
        _templates = templates;
        _executor = new(chassis, arm, vision, gripper, templateLogger);
        _events = events;
    }

    public Task<DeviceResult<MoveResult>> MoveAsync(MoveRequest request, CancellationToken ct) =>
        _chassis.MoveAsync(request, ct);

    /// <summary>
    /// 执行服务器整体下发的 MoveAction。这里不绕过模板直接调用底盘：模板执行器按 phases
    /// 顺序解释 MOVE_TO_MAP_POINT，并把强类型 Request 传给当前组合中的具体 IChassis。
    /// </summary>
    public async Task<DeviceResult<MoveResult>> ExecuteMoveAsync(MoveAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.ActionType != MainAction.Move)
            return DeviceResult<MoveResult>.Fail(new(4000, "MoveAction.actionType 必须是 MOVE。"));
        if (action.Phases.Count == 0)
            return DeviceResult<MoveResult>.Fail(new(4000, "MoveAction.phases 不能为空。"));
        MoveRequest request;
        try { request = action.ResolveRequest(); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException)
        {
            return DeviceResult<MoveResult>.Fail(new(4000, ex.Message));
        }

        var execution = await _executor.ExecuteAsync(action,
            new(request.PointName, MoveRequest: request), ct);
        if (!execution.Success)
            return DeviceResult<MoveResult>.Fail(execution.Error!, execution.Steps);

        var result = execution.Output as MoveResult;
        await EmitAsync("MOVE_DONE", new { request.PointName, request.Pose }, ct);
        return DeviceResult<MoveResult>.Ok(result, execution.Steps);
    }

    public async Task<DeviceResult<ArmActionResult>> HomeAsync(CancellationToken ct)
    {
        var template = ResolveTemplate(MainAction.ArmHome);
        if (template.Error is not null) return DeviceResult<ArmActionResult>.Fail(template.Error);
        var execution = await _executor.ExecuteAsync(template.Value!, new("GLOBAL"), ct);
        if (!execution.Success) return DeviceResult<ArmActionResult>.Fail(execution.Error!, execution.Steps);
        var status = await _arm.GetStatusAsync(ct);
        await EmitAsync("ARM_HOME_DONE", new { Pose = status.Value?.Pose }, ct);
        return DeviceResult<ArmActionResult>.Ok(new(status.Value?.Pose), execution.Steps);
    }

    public async Task<DeviceResult<VisionResult>> CaptureAsync(VisionRequest request, CancellationToken ct)
    {
        var template = ResolveTemplate(MainAction.VisionCapture);
        if (template.Error is not null) return DeviceResult<VisionResult>.Fail(template.Error);
        var execution = await _executor.ExecuteAsync(template.Value!,
            new(request.Station ?? "GLOBAL"), ct);
        if (!execution.Success) return DeviceResult<VisionResult>.Fail(execution.Error!, execution.Steps);
        var result = execution.Output as VisionResult;
        await EmitAsync("VISION_CAPTURED", new { request.Station, ImageUri = result?.ImageUri }, ct);
        return DeviceResult<VisionResult>.Ok(result, execution.Steps);
    }

    public async Task<DeviceResult<ArmActionResult>> PickAsync(ArmPickRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Station))
            return DeviceResult<ArmActionResult>.Fail(new(4000, "station 不能为空。"));
        var template = ResolveTemplate(MainAction.ArmPick);
        if (template.Error is not null) return DeviceResult<ArmActionResult>.Fail(template.Error);
        var execution = await _executor.ExecuteAsync(template.Value!,
            new(request.Station, request.Point, request.GraspProfile, Policy: request.Policy), ct);
        if (!execution.Success) return DeviceResult<ArmActionResult>.Fail(execution.Error!, execution.Steps);
        var status = await _arm.GetStatusAsync(ct);
        await EmitAsync("PICK_DONE", new { request.Station, request.Point }, ct);
        return DeviceResult<ArmActionResult>.Ok(new(status.Value?.Pose, true), execution.Steps);
    }

    /// <summary>执行服务器整体下发并已参数具体化的 ARM.PICK 主动作。</summary>
    public async Task<DeviceResult<ArmActionResult>> ExecutePickAsync(MainActionTemplate action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.ActionType != MainAction.ArmPick)
            return DeviceResult<ArmActionResult>.Fail(new(4000, "MainAction.actionType 必须是 ARM.PICK。"));
        if (action.Phases.Count == 0)
            return DeviceResult<ArmActionResult>.Fail(new(4000, "ARM.PICK phases 不能为空。"));

        var station = ReadPhaseString(action, "station") ?? "PICK_01";
        var point = ReadPhaseString(action, "point");
        var graspProfile = ReadPhaseString(action, "graspProfile") ?? "DEFAULT_PICK";
        var execution = await _executor.ExecuteAsync(action,
            new(station, point, graspProfile), ct);
        if (!execution.Success)
            return DeviceResult<ArmActionResult>.Fail(execution.Error!, execution.Steps);

        var status = await _arm.GetStatusAsync(ct);
        await EmitAsync("PICK_DONE", new { Station = station, Point = point }, ct);
        return DeviceResult<ArmActionResult>.Ok(new(status.Value?.Pose, true), execution.Steps);
    }

    /// <summary>执行服务器整体下发并已参数具体化的 ARM.PLACE 主动作。</summary>
    public async Task<DeviceResult<ArmActionResult>> ExecutePlaceAsync(MainActionTemplate action, CancellationToken ct)
    {
        var validation = ValidateEmbeddedAction(action, MainAction.ArmPlace);
        if (validation is not null) return DeviceResult<ArmActionResult>.Fail(validation);
        var station = ReadPhaseString(action, "station") ?? "PLACE_01";
        var point = ReadPhaseString(action, "point");
        var releaseProfile = ReadPhaseString(action, "releaseProfile") ?? "DEFAULT_PLACE";
        var execution = await _executor.ExecuteAsync(action,
            new(station, point, ReleaseProfile: releaseProfile), ct);
        if (!execution.Success) return DeviceResult<ArmActionResult>.Fail(execution.Error!, execution.Steps);
        var status = await _arm.GetStatusAsync(ct);
        await EmitAsync("PLACE_DONE", new { Station = station, Point = point }, ct);
        return DeviceResult<ArmActionResult>.Ok(new(status.Value?.Pose, false), execution.Steps);
    }

    /// <summary>执行服务器整体下发的 ARM.HOME 主动作。</summary>
    public async Task<DeviceResult<ArmActionResult>> ExecuteHomeAsync(MainActionTemplate action, CancellationToken ct)
    {
        var validation = ValidateEmbeddedAction(action, MainAction.ArmHome);
        if (validation is not null) return DeviceResult<ArmActionResult>.Fail(validation);
        var execution = await _executor.ExecuteAsync(action, new("GLOBAL"), ct);
        if (!execution.Success) return DeviceResult<ArmActionResult>.Fail(execution.Error!, execution.Steps);
        var status = await _arm.GetStatusAsync(ct);
        await EmitAsync("ARM_HOME_DONE", new { Pose = status.Value?.Pose }, ct);
        return DeviceResult<ArmActionResult>.Ok(new(status.Value?.Pose), execution.Steps);
    }

    /// <summary>执行服务器整体下发的 VISION.CAPTURE 主动作。</summary>
    public async Task<DeviceResult<VisionResult>> ExecuteCaptureAsync(MainActionTemplate action, CancellationToken ct)
    {
        var validation = ValidateEmbeddedAction(action, MainAction.VisionCapture);
        if (validation is not null) return DeviceResult<VisionResult>.Fail(validation);
        var station = ReadPhaseString(action, "station") ?? "GLOBAL";
        var execution = await _executor.ExecuteAsync(action, new(station), ct);
        if (!execution.Success) return DeviceResult<VisionResult>.Fail(execution.Error!, execution.Steps);
        var result = execution.Output as VisionResult;
        await EmitAsync("VISION_CAPTURED", new { Station = station, ImageUri = result?.ImageUri }, ct);
        return DeviceResult<VisionResult>.Ok(result, execution.Steps);
    }

    public async Task<DeviceResult<ArmActionResult>> PlaceAsync(ArmPlaceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Station))
            return DeviceResult<ArmActionResult>.Fail(new(4000, "station 不能为空。"));
        var template = ResolveTemplate(MainAction.ArmPlace);
        if (template.Error is not null) return DeviceResult<ArmActionResult>.Fail(template.Error);
        var execution = await _executor.ExecuteAsync(template.Value!,
            new(request.Station, request.Point, ReleaseProfile: request.ReleaseProfile, Policy: request.Policy), ct);
        if (!execution.Success) return DeviceResult<ArmActionResult>.Fail(execution.Error!, execution.Steps);
        var status = await _arm.GetStatusAsync(ct);
        await EmitAsync("PLACE_DONE", new { request.Station, request.Point }, ct);
        return DeviceResult<ArmActionResult>.Ok(new(status.Value?.Pose, false), execution.Steps);
    }

    public Task<DeviceResult<BatchActionResult>> PickBatchAsync(ArmPickBatchRequest request, CancellationToken ct) =>
        ExecutePickBatchAsync(request, ct);

    public Task<DeviceResult<BatchActionResult>> PlaceBatchAsync(ArmPlaceBatchRequest request, CancellationToken ct) =>
        ExecutePlaceBatchAsync(request, ct);

    /// <summary>执行服务器已展开为完整 phases 的 ARM.PICK_BATCH。</summary>
    public async Task<DeviceResult<BatchActionResult>> ExecutePickBatchAsync(
        MainActionTemplate action, CancellationToken ct)
    {
        var validation = ValidateEmbeddedAction(action, MainAction.ArmPickBatch);
        if (validation is not null) return DeviceResult<BatchActionResult>.Fail(validation);
        return await ExecuteEmbeddedBatchAsync(action, pick: true, ct);
    }

    /// <summary>执行服务器已展开为完整 phases 的 ARM.PLACE_BATCH。</summary>
    public async Task<DeviceResult<BatchActionResult>> ExecutePlaceBatchAsync(
        MainActionTemplate action, CancellationToken ct)
    {
        var validation = ValidateEmbeddedAction(action, MainAction.ArmPlaceBatch);
        if (validation is not null) return DeviceResult<BatchActionResult>.Fail(validation);
        return await ExecuteEmbeddedBatchAsync(action, pick: false, ct);
    }

    /// <summary>
    /// 按服务器完整下发的 phases 执行任意已注册 MainAction。
    /// actionType 只表示 L2 能力名称；实际行为完全由有序 phases 决定，
    /// 各 SubAction 由通用执行器路由到组合设备。
    /// </summary>
    public async Task<DeviceResult<MainActionExecutionResult>> ExecuteMainActionPhasesAsync(
        MainActionTemplate action, CancellationToken ct)
    {
        if (action is null || action.Phases.Count == 0)
            return DeviceResult<MainActionExecutionResult>.Fail(
                new(PlatformErrorCodes.InvalidActionInput, "MainAction.phases 不能为空。"));

        var station = ReadPhaseString(action, "station") ?? "GLOBAL";
        var point = ReadPhaseString(action, "point");
        var execution = await _executor.ExecuteAsync(action, new(station, point), ct);
        if (!execution.Success)
            return DeviceResult<MainActionExecutionResult>.Fail(execution.Error!, execution.Steps);

        var completed = execution.Steps.Count(x =>
            x.State.Equals("SUCCEEDED", StringComparison.OrdinalIgnoreCase) ||
            x.State.Equals("RESUMED_SUCCEEDED", StringComparison.OrdinalIgnoreCase) ||
            x.State.Equals("DISABLED", StringComparison.OrdinalIgnoreCase));
        await EmitAsync("MAIN_ACTION_DONE", new { action.TemplateId, action.ActionType, Completed = completed }, ct);
        return DeviceResult<MainActionExecutionResult>.Ok(
            new(completed, action.Phases.Count, execution.Output), execution.Steps);
    }

    private async Task<DeviceResult<BatchActionResult>> ExecuteEmbeddedBatchAsync(
        MainActionTemplate action, bool pick, CancellationToken ct)
    {
        var station = ReadPhaseString(action, "station") ?? (pick ? "PICK_01" : "PLACE_01");
        var releaseProfile = ReadPhaseString(action, "releaseProfile");
        var execution = await _executor.ExecuteAsync(action,
            new(station, ReleaseProfile: releaseProfile), ct);
        if (!execution.Success)
            return DeviceResult<BatchActionResult>.Fail(execution.Error!, execution.Steps);

        var completedSlots = action.Phases
            .Select(x => x.Parameters?["slotId"])
            .Where(x => x is not null)
            .Select(x => x!.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return DeviceResult<BatchActionResult>.Ok(
            new(completedSlots.Length, completedSlots.Length, completedSlots), execution.Steps);
    }

    private async Task<DeviceResult<BatchActionResult>> ExecutePickBatchAsync(
        ArmPickBatchRequest request, CancellationToken ct)
    {
        var validation = ValidateBatch(request.Slots);
        if (validation is not null) return DeviceResult<BatchActionResult>.Fail(validation);
        var singleTemplate = ResolveTemplate(MainAction.ArmPick);
        if (singleTemplate.Error is not null) return DeviceResult<BatchActionResult>.Fail(singleTemplate.Error);
        MainActionTemplate action;
        try { action = new ArmPickBatchAction(singleTemplate.Value!, request); }
        catch (ArgumentException ex) { return DeviceResult<BatchActionResult>.Fail(new(4000, ex.Message)); }
        var execution = await _executor.ExecuteAsync(action,
            new(request.Station, Policy: request.Policy), ct);
        if (!execution.Success) return DeviceResult<BatchActionResult>.Fail(execution.Error!, execution.Steps);
        var completed = request.Slots.Select(x => x.SlotId).ToArray();
        return DeviceResult<BatchActionResult>.Ok(new(completed.Length, completed.Length, completed), execution.Steps);
    }

    private async Task<DeviceResult<BatchActionResult>> ExecutePlaceBatchAsync(
        ArmPlaceBatchRequest request, CancellationToken ct)
    {
        var validation = ValidateBatch(request.Slots);
        if (validation is not null) return DeviceResult<BatchActionResult>.Fail(validation);
        var singleTemplate = ResolveTemplate(MainAction.ArmPlace);
        if (singleTemplate.Error is not null) return DeviceResult<BatchActionResult>.Fail(singleTemplate.Error);
        MainActionTemplate action;
        try { action = new ArmPlaceBatchAction(singleTemplate.Value!, request); }
        catch (ArgumentException ex) { return DeviceResult<BatchActionResult>.Fail(new(4000, ex.Message)); }
        var execution = await _executor.ExecuteAsync(action,
            new(request.Station, ReleaseProfile: request.ReleaseProfile, Policy: request.Policy), ct);
        if (!execution.Success) return DeviceResult<BatchActionResult>.Fail(execution.Error!, execution.Steps);
        var completed = request.Slots.Select(x => x.SlotId).ToArray();
        return DeviceResult<BatchActionResult>.Ok(new(completed.Length, completed.Length, completed), execution.Steps);
    }

    private static DeviceError? ValidateBatch(IReadOnlyList<BatchSlot> slots)
    {
        if (slots.Count == 0) return new(4000, "slots 不能为空。");
        var duplicate = slots.GroupBy(x => x.SlotId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        return duplicate is null ? null : new DeviceError(PlatformErrorCodes.InvalidActionInput, $"slotId 重复：{duplicate.Key}");
    }

    /// <summary>
    /// 从启动时加载的 ActionTemplateCatalog 获取 L2 模板。
    /// RobotController 不再重新 new PICK/PLACE/HOME/CAPTURE 的硬编码 phase 序列。
    /// </summary>
    private DeviceResult<MainActionTemplate> ResolveTemplate(MainAction action)
    {
        try
        {
            var template = _templates.Resolve(action.ToActionType());
            return DeviceResult<MainActionTemplate>.Ok(template);
        }
        catch (KeyNotFoundException ex)
        {
            return DeviceResult<MainActionTemplate>.Fail(new DeviceError(
                4404, ex.Message, Category: DeviceErrorCategory.Configuration,
                RecoveryStrategy: DeviceRecoveryStrategy.CorrectConfiguration,
                HandlingAdvice: $"请检查 {action.ToActionType()}.Templates.json 是否已加载。"));
        }
        catch (ArgumentException ex)
        {
            return DeviceResult<MainActionTemplate>.Fail(new DeviceError(
                4000, ex.Message, Category: DeviceErrorCategory.Configuration,
                RecoveryStrategy: DeviceRecoveryStrategy.CorrectConfiguration));
        }
    }

    private async Task<DeviceResult<BatchActionResult>> RunBatch(string station, IReadOnlyList<BatchSlot> slots,
        bool pick, string orderPolicy, IReadOnlyDictionary<string, string>? cacheAssign,
        string? releaseProfile, ActionExecutionPolicy? policy, CancellationToken ct)
    {
        if (slots.Count == 0) return DeviceResult<BatchActionResult>.Fail(new(4000, "slots 不能为空。"));
        var duplicate = slots.GroupBy(x => x.SlotId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) return DeviceResult<BatchActionResult>.Fail(new(4000, $"slotId 重复：{duplicate.Key}"));
        IReadOnlyList<BatchSlot> ordered = orderPolicy.ToUpperInvariant() switch
        {
            "INPUT" => slots.ToArray(),
            "RANK_ASC" => slots.OrderBy(x => NaturalRank(x.SlotId)).ToArray(),
            "RANK_DESC" => slots.OrderByDescending(x => NaturalRank(x.SlotId)).ToArray(),
            _ => []
        };
        if (ordered.Count == 0) return DeviceResult<BatchActionResult>.Fail(new(4000, $"不支持 orderPolicy：{orderPolicy}"));
        var allSteps = new List<OperationStep>();
        var completed = new List<string>();
        foreach (var slot in ordered)
        {
            var point = cacheAssign is not null && cacheAssign.TryGetValue(slot.SlotId, out var assigned)
                ? assigned : slot.Point ?? slot.SlotId;
            var result = pick
                ? await PickAsync(new(station, point, policy), ct)
                : await PlaceAsync(new(station, point, policy, releaseProfile ?? "DEFAULT_PLACE"), ct);
            foreach (var step in result.Steps ?? [])
                allSteps.Add(step with { Sequence = allSteps.Count + 1, PhaseId = $"{slot.SlotId}.{step.PhaseId}" });
            if (!result.Success) return DeviceResult<BatchActionResult>.Fail(result.Error!, allSteps);
            completed.Add(slot.SlotId);
        }
        return DeviceResult<BatchActionResult>.Ok(new(completed.Count, slots.Count, completed), allSteps);
    }

    private static int NaturalRank(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var rank) ? rank : int.MaxValue;
    }

    private static string? ReadPhaseString(MainActionTemplate action, string key)
    {
        foreach (var phase in action.Phases)
        {
            var node = phase.Parameters?[key];
            if (node is null) continue;
            try { return node.GetValue<string>(); }
            catch (InvalidOperationException) { }
        }
        return null;
    }

    private static DeviceError? ValidateEmbeddedAction(MainActionTemplate? action, MainAction expected)
    {
        if (action is null) return new(4000, "MainAction 不能为空。");
        if (action.ActionType != expected)
            return new(4000, $"MainAction.actionType 必须是 {expected.ToActionType()}。");
        return action.Phases.Count == 0 ? new DeviceError(PlatformErrorCodes.InvalidActionInput, "MainAction.phases 不能为空。") : null;
    }

    private async ValueTask EmitAsync(string eventType, object data, CancellationToken ct)
    {
        if (_events is not null)
            await _events.EmitAsync(new(eventType, _robotId, null, data, DateTimeOffset.UtcNow), ct);
    }
}
