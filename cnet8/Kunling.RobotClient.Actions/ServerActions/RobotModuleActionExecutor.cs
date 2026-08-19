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

        // Core 每产生一个 phase 状态，就立即更新本地查询缓存并向服务器发送 Running 快照。
        // 事件是同步产生的，因此这里只复制不可变快照；网络发送异步执行且自行吞掉断线异常，
        // 不能反向阻塞或破坏设备动作线程。
        void OnStepChanged(object? _, OperationStep step)
        {
            IReadOnlyList<ResolvedStep> snapshot;
            lock (liveStepsLock)
            {
                liveSteps.Add(ConvertStep(step));
                snapshot = liveSteps.ToArray();
                _states[command.ActionInstanceId] = new(MainActionState.Running, ResolvedSteps: snapshot);
            }
            _ = ReportProgressSafelyAsync(context, snapshot, cancellationToken);
        }

        var progressSource = _robot as IRobotExecutionProgressSource;
        if (progressSource is not null) progressSource.StepChanged += OnStepChanged;
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
            converted = embeddedAction switch
                {
                    // 从 input.MainAction 反序列化具体 MOVE 主动作，再按其中 phases 调用设备。
                    MainAction.Move => Convert(await _robot.ExecuteMoveAsync(
                        ParseRequired<MoveActionMessage>(command.Input).MainAction, cancellationToken), command, receivedTemplate),
                    MainAction.ArmPick => Convert(await _robot.ExecutePickAsync(
                        receivedTemplate, cancellationToken), command, receivedTemplate),
                    MainAction.ArmPlace => Convert(await _robot.ExecutePlaceAsync(
                        receivedTemplate, cancellationToken), command, receivedTemplate),
                    MainAction.ArmPickBatch => Convert(await _robot.ExecutePickBatchAsync(
                        receivedTemplate, cancellationToken), command, receivedTemplate),
                    MainAction.ArmPlaceBatch => Convert(await _robot.ExecutePlaceBatchAsync(
                        receivedTemplate, cancellationToken), command, receivedTemplate),
                    MainAction.ArmHome => Convert(await _robot.ExecuteHomeAsync(
                        receivedTemplate, cancellationToken), command, receivedTemplate),
                    MainAction.VisionCapture => Convert(await _robot.ExecuteCaptureAsync(
                        receivedTemplate, cancellationToken), command, receivedTemplate),
                    _ => ServerActionExecutionResult.Failed(PlatformErrorCodes.UnsupportedAction,
                        $"不支持 MainAction：{embeddedAction.ToActionType()}")
                };
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
            if (progressSource is not null) progressSource.StepChanged -= OnStepChanged;
        }

        _states[command.ActionInstanceId] = new(converted.State, converted.PhysicalResult, converted.ResolvedSteps, converted.Error);
        return converted;
    }

    private static ResolvedStep ConvertStep(OperationStep step) => new(step.Sequence, step.PhaseId,
        step.SubAction, step.State,
        step.Evidence is null ? null : JsonSerializer.SerializeToElement(step.Evidence, ServerActionJson.Default),
        step.SlotId, step.CacheSlot, step.PoseRef);

    private static async Task ReportProgressSafelyAsync(IServerActionExecutionContext context,
        IReadOnlyList<ResolvedStep> steps, CancellationToken cancellationToken)
    {
        try { await context.ReportRunningAsync(steps, cancellationToken: cancellationToken); }
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
