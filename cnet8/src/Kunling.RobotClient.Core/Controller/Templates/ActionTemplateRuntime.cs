using System.Text.Json;
using System.Text.Json.Serialization;
using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Controller.L1SubActions.L1SubActionTemplates;
using Kunling.RobotClient.Core.Controller.Templates.MoveActionTemplates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>多个 L2 主动作模板的运行时目录。</summary>
public sealed class ActionTemplateCatalog
{
    public List<MainActionTemplate> Templates { get; } = [];

    public MainActionTemplate Resolve(string actionType, string? station = null)
    {
        var action = MainActionCatalog.Parse(actionType);
        return Templates.FirstOrDefault(x => x.ActionType == action)
            ?? throw new KeyNotFoundException($"未找到动作模板：{actionType}");
    }
}

/// <summary>加载 ARM/HOME/VISION 的 *.Templates.json。</summary>
public static class ActionTemplateLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ActionTemplateCatalog LoadMany(params string[] paths)
    {
        var result = new ActionTemplateCatalog();
        foreach (var path in paths)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到动作模板配置。", path);
            var file = JsonSerializer.Deserialize<ActionTemplateFile>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException($"动作模板为空：{path}");
            result.Templates.AddRange(file.ActionTemplates);
        }
        var duplicate = result.Templates.GroupBy(x => x.ActionType).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"动作模板重复：{duplicate.Key.ToActionType()}");
        foreach (var template in result.Templates)
        {
            if (template.Phases.Count == 0) throw new InvalidDataException($"模板 {template.ActionType.ToActionType()} 没有 phase。");
            foreach (var phase in template.Phases)
            {
                if (string.IsNullOrWhiteSpace(phase.PhaseId))
                    throw new InvalidDataException($"模板 {template.ActionType.ToActionType()} 的 phaseId 不能为空。");
                if (!Enum.IsDefined(phase.SubAction))
                    throw new InvalidDataException($"phase {phase.PhaseId} 的 subAction 无效。");
                if (phase.Parameters is null)
                    throw new InvalidDataException($"phase {phase.PhaseId} 的 params 必须是 object，不能省略。");
                if (!Enum.IsDefined(phase.OnFail))
                    throw new InvalidDataException($"phase {phase.PhaseId} 的 onFail 无效。");
            }
            var duplicatePhase = template.Phases.GroupBy(x => x.PhaseId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => x.Count() > 1);
            if (duplicatePhase is not null) throw new InvalidDataException($"phaseId 重复：{duplicatePhase.Key}");
        }
        return result;
    }

    private sealed class ActionTemplateFile
    {
        [JsonPropertyName("actionTemplates")]
        public List<MainActionTemplate> ActionTemplates { get; init; } = [];
    }
}

public sealed record ActionTemplateContext(string Station, string? Point = null, string? GraspProfile = null,
    string? ReleaseProfile = null, ActionExecutionPolicy? Policy = null, MoveRequest? MoveRequest = null);
public sealed record ActionTemplateExecutionResult(bool Success, IReadOnlyList<OperationStep> Steps,
    DeviceError? Error = null, object? Output = null);

/// <summary>解释当前 MainActionTemplate/PhaseActionTemplate 并调用组合设备接口。</summary>
public sealed class ActionTemplateExecutor(IChassis chassis, IArm arm, IVision vision, IGripper gripper,
    Action<string>? logger = null)
{
    /// <summary>phase 状态发生变化时发布；订阅方不得在事件中阻塞设备执行线程。</summary>
    public event EventHandler<OperationStep>? StepChanged;

    private void AddStep(List<OperationStep> steps, OperationStep step)
    {
        steps.Add(step);
        StepChanged?.Invoke(this, step);
    }

    public async Task<ActionTemplateExecutionResult> ExecuteAsync(MainActionTemplate template,
        ActionTemplateContext context, CancellationToken ct)
    {
        var steps = new List<OperationStep>();
        var completedPhaseIds = new HashSet<string>(
            context.Policy?.CompletedPhaseIds ?? [], StringComparer.OrdinalIgnoreCase);
        object? output = null;
        foreach (var phase in template.Phases)
        {
            if (!phase.Enabled)
            {
                AddStep(steps, CreateStep(steps.Count + 1, phase, "DISABLED"));
                continue;
            }

            // 只允许跳过服务器明确回传为已成功的 phase；失败、运行中和未知状态均不得跳过。
            if (completedPhaseIds.Contains(phase.PhaseId))
            {
                logger?.Invoke($"[TEMPLATE][{template.ActionType.ToActionType()}] phase={phase.PhaseId} RESUME-SKIP");
                AddStep(steps, CreateStep(steps.Count + 1, phase, "RESUMED_SUCCEEDED"));
                continue;
            }

            var maxRetries = ResolveMaxRetries(phase, context.Policy);
            PhaseResult result = new(false, null, new DeviceError(PlatformErrorCodes.PhaseExecutionFailed, $"phase {phase.PhaseId} 尚未执行。"));
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                logger?.Invoke($"[TEMPLATE][{template.ActionType.ToActionType()}] phase={phase.PhaseId} " +
                    $"subAction={phase.SubAction.ToProtocolName()} attempt={attempt + 1}/{maxRetries + 1} START");
                result = await ExecutePhaseAsync(phase, context, ct);
                AddStep(steps, CreateStep(steps.Count + 1, phase,
                    result.Success ? "SUCCEEDED" : attempt < maxRetries ? "RETRY_PENDING" : phase.Gate ? "GATE_FAILED" : "FAILED",
                    new PhaseAttemptEvidence(attempt + 1, result.Evidence, result.Error)));
                if (result.Success) break;
                if (phase.OnFail is PhaseFailAction.ABORT or PhaseFailAction.SKIP) break;

                if (phase.OnFail == PhaseFailAction.VERIFY_BEFORE_RETRY ||
                    string.Equals(context.Policy?.RetryMode, "VERIFY_BEFORE_RETRY", StringComparison.OrdinalIgnoreCase))
                {
                    var verification = await VerifyBeforeRetryAsync(template, phase, context, ct);
                    AddStep(steps, CreateStep(steps.Count + 1, phase,
                        verification.Decision.ToString().ToUpperInvariant(), verification.Evidence ?? verification.Error,
                        "VERIFY_BEFORE_RETRY"));
                    logger?.Invoke($"[TEMPLATE][{template.ActionType.ToActionType()}] phase={phase.PhaseId} " +
                        $"VERIFY_BEFORE_RETRY={verification.Decision}");

                    // 传感器复核表明目标状态其实已经达成时，不再重复下发设备动作。
                    if (verification.Decision == RetryVerificationDecision.Satisfied)
                    {
                        result = new(true, verification.Evidence);
                        break;
                    }
                    // 物料位置不明或多传感器结果冲突时禁止盲目重试。
                    if (verification.Decision == RetryVerificationDecision.Abort)
                    {
                        result = new(false, verification.Evidence, verification.Error ??
                            new DeviceError(PlatformErrorCodes.PhaseExecutionFailed, $"phase {phase.PhaseId} 复核结果不允许自动重试。",
                                PhysicalResultKnown: false, Retryable: false));
                        break;
                    }

                    // 最后一次尝试失败后仍执行一次真实复核，但复核未确认成功时不得再回放动作。
                    if (attempt >= maxRetries)
                    {
                        result = new(false, verification.Evidence,
                            CreateExhaustionError(phase, context.Policy, result.Error));
                        AddStep(steps, CreateStep(steps.Count + 1, phase,
                            ResolveOnExhaust(phase, context.Policy),
                            new { attempt = attempt + 1, maxRetries, verification = verification.Evidence },
                            "ON_EXHAUST"));
                        break;
                    }

                    // retryFromPhaseId 指向本次重试必须重新执行的起点。例如取料夹持校验失败，
                    // 必须从 toPick 重新进入，而不是只重复读取夹爪传感器。
                    var retryFromPhaseId = GetOptionalString(phase, "retryFromPhaseId");
                    if (!string.IsNullOrWhiteSpace(retryFromPhaseId))
                    {
                        var replay = await ReplayBeforeRetryAsync(template, retryFromPhaseId, phase.PhaseId,
                            context, steps, ct);
                        if (!replay.Success)
                        {
                            result = replay;
                            break;
                        }
                    }
                }

                else if (attempt >= maxRetries)
                {
                    result = new(false, result.Evidence, CreateExhaustionError(phase, context.Policy, result.Error));
                    AddStep(steps, CreateStep(steps.Count + 1, phase,
                        ResolveOnExhaust(phase, context.Policy), result.Error, "ON_EXHAUST"));
                    break;
                }

                var retryDelayMs = Math.Max(0, context.Policy?.RetryDelayMs ?? 500);
                if (retryDelayMs > 0) await Task.Delay(retryDelayMs, ct);
            }

            object? stepEvidence = result.Success ? result.Evidence : result.Error;
            if (!result.Success && IsFinalPlacementVerification(phase))
            {
                // 放置到位复核失败时立即拍照取证。拍照失败不能覆盖原始业务错误，
                // 两份结果一并写入 resolvedSteps，供上游和运维判断现场真实状态。
                var captureRequest = CreateVisionRequest(phase, context) with
                {
                    Recipe = "PLACEMENT_FAILURE_EVIDENCE"
                };
                var capture = await vision.CaptureAsync(captureRequest, ct);
                stepEvidence = new PhaseFailureEvidence(result.Error, result.Evidence,
                    capture.Value, capture.Error);
                logger?.Invoke($"[TEMPLATE][{template.ActionType.ToActionType()}] " +
                    $"phase={phase.PhaseId} failure evidence capture={(capture.Success ? "SUCCEEDED" : "FAILED")}");
            }
            if (!result.Success && stepEvidence is PhaseFailureEvidence)
                AddStep(steps, CreateStep(steps.Count + 1, phase, "CAPTURED", stepEvidence, "FAILURE_EVIDENCE"));
            if (result.Success) { output = result.Evidence; completedPhaseIds.Add(phase.PhaseId); continue; }
            if (phase.OnFail == PhaseFailAction.SKIP && !phase.Gate) continue;
            return new(false, steps, result.Error ?? new DeviceError(PlatformErrorCodes.PhaseExecutionFailed, $"phase {phase.PhaseId} 失败。"));
        }
        return new(true, steps, Output: output);
    }

    private async Task<RetryVerification> VerifyBeforeRetryAsync(MainActionTemplate template,
        PhaseActionTemplate failedPhase, ActionTemplateContext context, CancellationToken ct)
    {
        // 位姿运动失败必须读取机械臂反馈并与本 phase 的目标位姿比较，不能用“原位有料”
        // 代替“机械臂已到位”。只有实际位姿进入容差才允许把运动 phase 补判成功。
        if (failedPhase.SubAction == SubAction.MOVE_TO_POSE)
        {
            var request = L1SubActionMoveToPose.ResolveRequest(failedPhase, context.Station, context.Point);
            var status = await arm.GetStatusAsync(ct);
            if (!status.Success || status.Value is not { Connected: true, Moving: false, Pose: not null } actual ||
                request.Pose is null)
                return new(RetryVerificationDecision.RetryFrom, status.Value, status.Error);
            return IsArmArrived(actual.Pose, request.Pose, request.PositionToleranceMm, request.AngleToleranceDeg)
                ? new(RetryVerificationDecision.Satisfied, new { actual = actual.Pose, target = request.Pose })
                : new(RetryVerificationDecision.RetryFrom, new { actual = actual.Pose, target = request.Pose });
        }

        // 先用与原 phase 相同的期望值重新读取夹爪传感器。若目标状态已满足，
        // 本 phase 可直接判定成功，避免二次夹取/二次释放。
        if (failedPhase.SubAction == SubAction.GRIP_VERIFY_LOAD)
        {
            var grip = Convert(await gripper.VerifyLoadAsync(
                L1SubActionGripVerifyLoad.ResolveRequest(failedPhase,
                    context.GraspProfile ?? context.ReleaseProfile), ct),
                "GRIPPER", gripper.Vendor, gripper.Model);
            if (grip.Success)
                return new(RetryVerificationDecision.Satisfied, grip.Evidence);

            if (template.ActionType is MainAction.ArmPick or MainAction.ArmPickBatch)
            {
                var source = Convert(await vision.VerifyAsync(
                    CreateVisionRequest(failedPhase, context) with { Recipe = "MATERIAL" }, ct),
                    "VISION", vision.Vendor, vision.Model, x => x?.Passed == true);
                // 夹爪未抓到且原位仍有料，才允许从 retryFromPhaseId 重新取料。
                return source.Success
                    ? new(RetryVerificationDecision.RetryFrom, source.Evidence)
                    : new(RetryVerificationDecision.Abort, source.Evidence,
                        new DeviceError(PlatformErrorCodes.MaterialStateUnknown, "夹爪未检测到物料，且视觉无法确认物料仍在原位；物理结果未知，禁止盲重试。",
                            PhysicalResultKnown: false, Retryable: false));
            }

            if (template.ActionType is MainAction.ArmPlace or MainAction.ArmPlaceBatch)
            {
                var placed = Convert(await vision.VerifyAsync(
                    CreateVisionRequest(failedPhase, context) with { Recipe = "PLACEMENT" }, ct),
                    "VISION", vision.Vendor, vision.Model, x => x?.Passed == true);
                // 视觉已确认放置，但夹爪仍报有料属于传感器冲突；不允许再次释放。
                return placed.Success
                    ? new(RetryVerificationDecision.Abort, placed.Evidence,
                        new DeviceError(PlatformErrorCodes.PlacementStateConflict, "视觉确认已放置，但夹爪仍检测到负载；传感器结果冲突，需人工复核。",
                            PhysicalResultKnown: false, Retryable: false))
                    : new(RetryVerificationDecision.RetryFrom, placed.Evidence);
            }
        }

        if (template.ActionType is MainAction.ArmPick or MainAction.ArmPickBatch)
        {
            var request = CreateVisionRequest(failedPhase, context) with { Recipe = "MATERIAL" };
            var result = Convert(await vision.VerifyAsync(request, ct),
                "VISION", vision.Vendor, vision.Model, x => x?.Passed == true);
            return result.Success
                ? new(RetryVerificationDecision.Satisfied, result.Evidence)
                : new(RetryVerificationDecision.RetryFrom, result.Evidence, result.Error);
        }

        if (template.ActionType is MainAction.ArmPlace or MainAction.ArmPlaceBatch)
        {
            var request = CreateVisionRequest(failedPhase, context) with { Recipe = "PLACEMENT" };
            var result = Convert(await vision.VerifyAsync(request, ct),
                "VISION", vision.Vendor, vision.Model, x => x?.Passed == true);
            return result.Success
                ? new(RetryVerificationDecision.Satisfied, result.Evidence)
                : new(RetryVerificationDecision.RetryFrom, result.Evidence, result.Error);
        }

        if (failedPhase.SubAction == SubAction.MOVE_TO_MAP_POINT)
        {
            var result = Convert(await chassis.GetStatusAsync(ct),
                "CHASSIS", chassis.Vendor, chassis.Model, x => x is { Connected: true, Moving: false });
            return result.Success ? new(RetryVerificationDecision.Satisfied, result.Evidence)
                : new(RetryVerificationDecision.RetryFrom, result.Evidence, result.Error);
        }

        var armResult = Convert(await arm.GetStatusAsync(ct),
            "ARM", arm.Vendor, arm.Model, x => x is { Connected: true, Moving: false });
        return armResult.Success ? new(RetryVerificationDecision.Satisfied, armResult.Evidence)
            : new(RetryVerificationDecision.RetryFrom, armResult.Evidence, armResult.Error);
    }

    private async Task<PhaseResult> ReplayBeforeRetryAsync(MainActionTemplate template, string retryFromPhaseId,
        string failedPhaseId, ActionTemplateContext context, List<OperationStep> steps, CancellationToken ct)
    {
        var from = template.Phases.FindIndex(x =>
            x.PhaseId.Equals(retryFromPhaseId, StringComparison.OrdinalIgnoreCase));
        var failed = template.Phases.FindIndex(x =>
            x.PhaseId.Equals(failedPhaseId, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || failed < 0 || from >= failed)
            return new(false, null, new DeviceError(PlatformErrorCodes.InvalidActionInput,
                $"phase {failedPhaseId} 的 retryFromPhaseId={retryFromPhaseId} 无效，必须指向此前 phase。",
                Retryable: false));

        for (var index = from; index < failed; index++)
        {
            var replayPhase = template.Phases[index];
            if (!replayPhase.Enabled) continue;
            var replayResult = await ExecutePhaseAsync(replayPhase, context, ct);
            AddStep(steps, CreateStep(steps.Count + 1, replayPhase,
                replayResult.Success ? "RETRY_FROM_SUCCEEDED" : "RETRY_FROM_FAILED",
                replayResult.Evidence ?? replayResult.Error));
            if (!replayResult.Success) return replayResult;
        }
        return new(true, new { retryFromPhaseId, failedPhaseId });
    }

    private static int ResolveMaxRetries(PhaseActionTemplate phase, ActionExecutionPolicy? policy)
    {
        var configured = Math.Max(0, GetInt(phase, "maxRetries", 0));
        // 命令策略大于零时作为本次执行的统一上限/覆盖值；否则采用模板配置。
        return policy is { MaxRetries: > 0 } ? policy.MaxRetries : configured;
    }

    private static DeviceError CreateExhaustionError(PhaseActionTemplate phase,
        ActionExecutionPolicy? policy, DeviceError? cause)
    {
        var onExhaust = ResolveOnExhaust(phase, policy);
        var exhaustion = onExhaust switch
        {
            "CANCEL" => new DeviceError(PlatformErrorCodes.RetryExhaustedCancel, $"phase {phase.PhaseId} 重试耗尽，动作已取消。",
                PhysicalResultKnown: false, Retryable: false, Category: DeviceErrorCategory.State,
                RecoveryStrategy: DeviceRecoveryStrategy.Abort,
                HandlingAdvice: "CANCEL：保持设备停止，不再自动执行。"),
            "MANUAL" => new DeviceError(PlatformErrorCodes.RetryExhaustedManual, $"phase {phase.PhaseId} 重试耗尽，转人工恢复。",
                PhysicalResultKnown: false, Retryable: false, Category: DeviceErrorCategory.State,
                RecoveryStrategy: DeviceRecoveryStrategy.ManualRecovery,
                HandlingAdvice: "MANUAL：人工确认物料和设备状态后再恢复。"),
            _ => new DeviceError(PlatformErrorCodes.RetryExhaustedHold, $"phase {phase.PhaseId} 重试耗尽，动作进入 HOLD。",
                PhysicalResultKnown: false, Retryable: false, Category: DeviceErrorCategory.State,
                RecoveryStrategy: DeviceRecoveryStrategy.ManualRecovery,
                HandlingAdvice: "HOLD：保持现场状态，等待人工确认。")
        };
        // 重试耗尽属于处置状态，不应覆盖真正的设备错误。对外保留原始 code/deviceCode/msg，
        // 同时追加 HOLD/CANCEL/MANUAL 信息，便于直接定位参数或硬件故障。
        return cause is null ? exhaustion : exhaustion with
        {
            Code = cause.Code,
            DeviceCode = cause.DeviceCode,
            Message = $"{cause.Message}；{exhaustion.Message}",
            Category = cause.Category == DeviceErrorCategory.Unknown ? exhaustion.Category : cause.Category
        };
    }

    private static string ResolveOnExhaust(PhaseActionTemplate phase, ActionExecutionPolicy? policy) =>
        (GetOptionalString(phase, "onExhaust") ?? policy?.OnExhaust ?? "HOLD").ToUpperInvariant();

    private static bool IsArmArrived(ArmPose actual, ArmPose target, double positionTolerance,
        double angleTolerance)
    {
        var positionError = Math.Sqrt(Math.Pow(actual.X - target.X, 2) +
                                      Math.Pow(actual.Y - target.Y, 2) +
                                      Math.Pow(actual.Z - target.Z, 2));
        static double AngleError(double left, double right)
        {
            var difference = Math.Abs(left - right) % 360;
            return difference > 180 ? 360 - difference : difference;
        }
        return positionError <= positionTolerance &&
               AngleError(actual.Rx, target.Rx) <= angleTolerance &&
               AngleError(actual.Ry, target.Ry) <= angleTolerance &&
               AngleError(actual.Rz, target.Rz) <= angleTolerance;
    }

    private async Task<PhaseResult> ExecutePhaseAsync(PhaseActionTemplate phase, ActionTemplateContext context,
        CancellationToken ct) => phase.SubAction switch
    {
        // MOVE 的完整设备请求由当前 phase.params 直接提供。
        // 不再要求调用方另外创建或传入 context.MoveRequest，避免同一份参数存在两套来源。
        SubAction.MOVE_TO_MAP_POINT => Convert(await chassis.MoveAsync(
            L1SubActionMoveToMapPoint.ResolveRequest(phase), ct),
            "CHASSIS", chassis.Vendor, chassis.Model),
        SubAction.MOVE_TO_POSE => Convert(await arm.MoveToPoseAsync(
            L1SubActionMoveToPose.ResolveRequest(phase, context.Station, context.Point), ct),
            "ARM", arm.Vendor, arm.Model),
        SubAction.VISION_VERIFY_MATERIAL or SubAction.VISION_VERIFY_PLACEMENT =>
            Convert(await vision.VerifyAsync(
                L1SubActionVisionVerifyMaterial.ResolveRequest(phase, context.Station), ct),
                "VISION", vision.Vendor, vision.Model, x => x?.Passed == true),
        SubAction.VISION_CAPTURE => Convert(await vision.CaptureAsync(CreateVisionRequest(phase, context), ct),
            "VISION", vision.Vendor, vision.Model),
        SubAction.GRIP_OPEN => Convert(await gripper.ReleaseAsync(L1SubActionGripOpen.ResolveRequest(
            phase, context.ReleaseProfile ?? context.GraspProfile), ct), "GRIPPER", gripper.Vendor, gripper.Model),
        SubAction.GRIP_CLOSE => Convert(await gripper.GripAsync(L1SubActionGripClose.ResolveRequest(
            phase, context.GraspProfile), ct), "GRIPPER", gripper.Vendor, gripper.Model),
        SubAction.GRIP_VERIFY_LOAD => Convert(await gripper.VerifyLoadAsync(
            L1SubActionGripVerifyLoad.ResolveRequest(
                phase, context.GraspProfile ?? context.ReleaseProfile), ct),
            "GRIPPER", gripper.Vendor, gripper.Model),
        SubAction.CHASSIS_VERIFY_STOPPED => Convert(await chassis.GetStatusAsync(ct),
            "CHASSIS", chassis.Vendor, chassis.Model, x => x is { Connected: true, Moving: false }),
        SubAction.ARM_VERIFY_HOME => Convert(await arm.GetStatusAsync(ct),
            "ARM", arm.Vendor, arm.Model, x => x is { Connected: true, Moving: false, Homed: true }),
        _ => new(false, null, new DeviceError(PlatformErrorCodes.UnsupportedAction,
            $"模板执行器不支持子动作：{phase.SubAction.ToProtocolName()}"))
    };

    private static VisionRequest CreateVisionRequest(PhaseActionTemplate phase, ActionTemplateContext context) => new(
        GetString(phase, "station", context.Station), GetString(phase, "recipe", phase.SubAction.ToProtocolName()),
        GetString(phase, "cameraId", "CAM01"),
        GetDouble(phase, "exposureMs", 10), GetDouble(phase, "gain", 1), GetInt(phase, "timeoutMs", 5000),
        GetString(phase, "outputFormat", "png"), GetBool(phase, "simulatedPass", true));

    private static bool IsFinalPlacementVerification(PhaseActionTemplate phase) =>
        phase.SubAction == SubAction.VISION_VERIFY_PLACEMENT &&
        phase.PhaseId.EndsWith("verifyPlaced", StringComparison.OrdinalIgnoreCase);

    private static PhaseResult Convert<T>(DeviceResult<T> result, string deviceType,
        string vendor, string model, Func<T?, bool>? predicate = null)
    {
        var success = result.Success && (predicate?.Invoke(result.Value) ?? true);
        if (success) return new(true, result.Value);
        var error = result.Error ?? new DeviceError(PlatformErrorCodes.PhaseExecutionFailed, "子动作成功条件未满足。");
        var physical = error.PhysicalDevice ?? new PhysicalDeviceError(deviceType, vendor, model,
            Code: error.DeviceCode, Message: error.Message);
        return new(false, result.Value, error with { PhysicalDevice = physical });
    }

    private static string GetString(PhaseActionTemplate phase, string key, string? fallback = null) =>
        phase.Parameters?[key]?.GetValue<string>() ?? fallback
        ?? throw new InvalidDataException($"phase {phase.PhaseId} 缺少参数 {key}。");
    private static string? GetOptionalString(PhaseActionTemplate phase, string key) =>
        phase.Parameters?[key]?.GetValue<string>();
    private static int GetInt(PhaseActionTemplate phase, string key, int fallback) =>
        phase.Parameters?[key]?.GetValue<int>() ?? fallback;
    private static double GetDouble(PhaseActionTemplate phase, string key, double fallback) =>
        phase.Parameters?[key]?.GetValue<double>() ?? fallback;
    private static bool GetBool(PhaseActionTemplate phase, string key, bool fallback) =>
        phase.Parameters?[key]?.GetValue<bool>() ?? fallback;

    private static OperationStep CreateStep(int sequence, PhaseActionTemplate phase, string state,
        object? evidence = null, string? subActionOverride = null) => new(sequence, phase.PhaseId,
        subActionOverride ?? phase.SubAction.ToProtocolName(), state, evidence,
        GetOptionalString(phase, "slotId"), GetOptionalString(phase, "cacheSlot"),
        GetOptionalString(phase, "poseRef"));
    private static T? GetObject<T>(PhaseActionTemplate phase, string key) where T : class
    {
        var node = phase.Parameters?[key];
        return node is null ? null : node.Deserialize<T>();
    }

    private sealed record PhaseResult(bool Success, object? Evidence = null, DeviceError? Error = null);
    private enum RetryVerificationDecision { Satisfied, RetryFrom, Abort }
    private sealed record RetryVerification(RetryVerificationDecision Decision, object? Evidence = null,
        DeviceError? Error = null);
    private sealed record PhaseAttemptEvidence(int Attempt, object? Evidence, DeviceError? Error);
    private sealed record PhaseFailureEvidence(DeviceError? ActionError, object? VerificationResult,
        VisionResult? CaptureResult, DeviceError? CaptureError);
}
