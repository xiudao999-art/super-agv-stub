using System.Collections.Concurrent;
using System.Text.Json;
using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Controller.Actions;
using Kunling.RobotClient.Core.Controller.ReportStateModels;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Actions.ServerActions;

/// <summary>服务器 Action 到 Core 强类型机器人操作的唯一协议适配点。</summary>
public sealed class RobotModuleActionExecutor : IServerActionExecutor
{
    private readonly IRobotOperations _robot;
    private readonly ConcurrentDictionary<string, ServerActionQueryResult> _states = new(StringComparer.OrdinalIgnoreCase);

    public RobotModuleActionExecutor(IRobotOperations robot) => _robot = robot;

    public bool CanExecute(string actionType, string actionVersion, ExecutionMode executionMode) =>
        actionVersion == "1.0" && executionMode == ExecutionMode.Package &&
        MainActionCatalog.TryParse(actionType, out _);

    public async Task<ServerActionExecutionResult> ExecuteAsync(IServerActionExecutionContext context, CancellationToken cancellationToken)
    {
        var command = context.Command;
        _states[command.ActionInstanceId] = new(MainActionState.Running);
        var liveSteps = new List<ResolvedStep>();
        var liveStepsLock = new object();
        var progressReports = new List<Task>();
        var progressReportsLock = new object();

        // StepChanged 只维护累计结果快照；ProgressChanged 才负责发送实时结构化事件。
        // 两个事件由 Core 同步按顺序产生，因此完成事件发送时快照已经包含刚完成的步骤。
        void OnStepChanged(object? _, OperationStep step)
        {
            lock (liveStepsLock)
            {
                liveSteps.Add(ConvertStep(step));
                _states[command.ActionInstanceId] = new(MainActionState.Running,
                    ResolvedSteps: liveSteps.ToArray());
            }
        }

        void OnProgressChanged(object? _, OperationProgress progress)
        {
            IReadOnlyList<ResolvedStep> snapshot;
            lock (liveStepsLock) snapshot = liveSteps.ToArray();
            var report = ReportProgressSafelyAsync(
                context, snapshot, ConvertProgress(progress), cancellationToken);
            lock (progressReportsLock) progressReports.Add(report);
        }

        var progressSource = _robot as IRobotExecutionProgressSource;
        if (progressSource is not null)
        {
            progressSource.StepChanged += OnStepChanged;
            progressSource.ProgressChanged += OnProgressChanged;
        }
        ServerActionExecutionResult converted;
        try
        {
            // COMMAND 外层不再包含 actionType，业务路由唯一取自 input.MainAction.actionType。
            var embeddedAction = ReadMainActionType(command.Input);
            var receivedTemplate = ParseRequired<MainActionMessage>(command.Input).MainAction;
            var isResume = command.ConfigSnapshot.ValueKind == JsonValueKind.Object &&
                           command.ConfigSnapshot.TryGetProperty("resume", out var resumeElement) &&
                           resumeElement.ValueKind == JsonValueKind.True;
            var templateErrors = isResume
                ? MainActionTemplateValidator.ValidateResume(receivedTemplate)
                : MainActionTemplateValidator.Validate(receivedTemplate);
            if (templateErrors.Count > 0)
                throw new ArgumentException(
                    $"MainAction 模板安全校验失败：{string.Join(" ", templateErrors)}");
            // 所有已注册的 L2 MainAction 使用同一条执行链：
            // 不根据 MOVE/PICK/PLACE 等名称调用专用业务方法，只按服务器实际下发的
            // phases 顺序解释 subAction、params、gate 和 onFail，并路由到对应组合设备。
            converted = Convert(await _robot.ExecuteMainActionPhasesAsync(
                receivedTemplate, cancellationToken), command, receivedTemplate);
        }
        catch (JsonException ex)
        {
            converted = ServerActionExecutionResult.Failed(PlatformErrorCodes.InvalidActionInput, $"Action Input 格式错误：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            converted = ServerActionExecutionResult.Failed(PlatformErrorCodes.InvalidActionInput, ex.Message);
        }
        finally
        {
            if (progressSource is not null)
            {
                progressSource.StepChanged -= OnStepChanged;
                progressSource.ProgressChanged -= OnProgressChanged;
            }
        }

        // 设备执行线程不等待单条网络写入，但整包终态必须排在所有 phase 事件之后。
        // ReportProgressSafelyAsync 已隔离断线异常，因此这里只保证顺序，不改变物理执行结果。
        Task[] pendingReports;
        lock (progressReportsLock) pendingReports = progressReports.ToArray();
        await Task.WhenAll(pendingReports);

        _states[command.ActionInstanceId] = new(converted.State, converted.PhysicalResult, converted.ResolvedSteps, converted.Error);
        return converted;
    }

    private static ResolvedStep ConvertStep(OperationStep step) => new(step.Sequence, step.PhaseId,
        step.SubAction, step.State,
        step.Evidence is null ? null : JsonSerializer.SerializeToElement(step.Evidence, ServerActionJson.Default),
        step.SlotId, step.CacheSlot, step.PoseRef);

    private static PhaseExecutionEvent ConvertProgress(OperationProgress progress) => new(
        progress.Type switch
        {
            OperationProgressType.PhaseStarted => "PHASE_STARTED",
            OperationProgressType.PhaseSucceeded => "PHASE_SUCCEEDED",
            OperationProgressType.PhaseFailed => "PHASE_FAILED",
            OperationProgressType.PhaseRetryPending => "PHASE_RETRY_PENDING",
            OperationProgressType.PhaseSkipped => "PHASE_SKIPPED",
            OperationProgressType.PhaseVerification => "PHASE_VERIFICATION",
            OperationProgressType.PhasePolicyApplied => "PHASE_POLICY_APPLIED",
            OperationProgressType.EvidenceCaptured => "EVIDENCE_CAPTURED",
            _ => throw new ArgumentOutOfRangeException(nameof(progress.Type), progress.Type, "未知 phase 进度类型。")
        },
        progress.StepSequence, progress.PhaseId, progress.SubAction, progress.StepState,
        progress.OccurredAt, progress.Attempt, progress.StartedAt, progress.CompletedAt,
        progress.DurationMs,
        progress.Evidence is null
            ? null : JsonSerializer.SerializeToElement(progress.Evidence, ServerActionJson.Default),
        progress.DeviceError);

    private static async Task ReportProgressSafelyAsync(IServerActionExecutionContext context,
        IReadOnlyList<ResolvedStep> steps, PhaseExecutionEvent phaseEvent,
        CancellationToken cancellationToken)
    {
        try { await context.ReportRunningAsync(steps, phaseEvent, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            // 状态上报失败不能改变物理设备执行结果；TCP 会话层负责断线和重连。
        }
    }

    public Task<ServerActionQueryResult> QueryAsync(string actionInstanceId, string deviceCommandId, CancellationToken cancellationToken) =>
        Task.FromResult(_states.TryGetValue(actionInstanceId, out var state)
            ? state
            : new ServerActionQueryResult(MainActionState.Hang,
                Error: new ActionError(PlatformErrorCodes.ActionStateUnknown, "客户端没有该动作实例的可确认记录", PhysicalResultKnown: false)));

    private static ServerActionExecutionResult Convert<T>(DeviceResult<T> result, ActionCommand command,
        MainActionTemplate template)
    {
        var steps = result.Steps?.Select(ConvertStep).ToArray();
        if (result.Success)
            return ServerActionExecutionResult.PhysicalDone(result.Value, steps);

        var error = result.Error ?? new DeviceError(PlatformErrorCodes.InternalExecutionError, "设备返回失败但没有错误信息。", PhysicalResultKnown: false);
        var failedStep = result.Steps?.LastOrDefault(x => x.State.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
            x.State is "HOLD" or "CANCEL" or "MANUAL") ?? result.Steps?.LastOrDefault();
        var failedPhase = failedStep is null ? null : template.Phases.FirstOrDefault(x =>
            x.PhaseId.Equals(failedStep.PhaseId, StringComparison.OrdinalIgnoreCase));
        var onFail = failedPhase?.OnFail;
        var choices = onFail switch
        {
            PhaseFailAction.RETRY_PHASE => new[] { "RETRY", "TERMINATE" },
            PhaseFailAction.VERIFY_BEFORE_RETRY => new[] { "RETRY_AFTER_VERIFY", "TERMINATE" },
            PhaseFailAction.SKIP => new[] { "SKIP", "TERMINATE" },
            _ => new[] { "TERMINATE" }
        };
        var failureContext = new ActionFailureContext(command.ActionInstanceId,
            template.ActionType.ToActionType(), template.TemplateId, failedStep?.PhaseId,
            failedStep?.SubAction, onFail, choices,
            error.PhysicalResultKnown ? MainActionState.Error : MainActionState.Hang,
            failedStep?.State ?? "FAILED");
        var detail = UnifiedRobotErrorModel.Create(error, failedStep?.SubAction ?? "UNKNOWN",
            onFail?.ToString());
        return error.PhysicalResultKnown
            ? new ServerActionExecutionResult(MainActionState.Error, ResolvedSteps: steps,
                Error: new ActionError(error.Code, error.Message, error.DeviceCode, true,
                    error.Retryable, error.Category, error.RecoveryStrategy, error.HandlingAdvice,
                    failureContext, detail))
            : new ServerActionExecutionResult(MainActionState.Hang, ResolvedSteps: steps,
                Error: new ActionError(error.Code, error.Message, error.DeviceCode, false,
                    error.Retryable, error.Category, error.RecoveryStrategy, error.HandlingAdvice,
                    failureContext, detail));
    }

    private static T ParseRequired<T>(JsonElement input)
    {
        var value = input.Deserialize<T>(ServerActionJson.Default);
        return value ?? throw new ArgumentException($"{typeof(T).Name} Input 不能为空。");
    }

    /// <summary>
    /// 从 COMMAND.input.MainAction.actionType 读取真正要执行的 L2 主动作。
    /// 不使用外层 actionType 选择业务处理方法，防止协议元数据与实际动作体脱节。
    /// </summary>
    private static MainAction ReadMainActionType(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(input, "MainAction", out var mainAction) ||
            mainAction.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("COMMAND.input.MainAction 不能为空。");
        if (!TryGetProperty(mainAction, "actionType", out var actionType) ||
            actionType.ValueKind != JsonValueKind.String)
            throw new ArgumentException("COMMAND.input.MainAction.actionType 不能为空。");
        return MainActionCatalog.Parse(actionType.GetString()!);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static T ParseOrDefault<T>(JsonElement input, T defaultValue) =>
        input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
        input.ValueKind == JsonValueKind.Object && !input.EnumerateObject().Any()
            ? defaultValue
            : ParseRequired<T>(input);
}
