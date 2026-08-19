using System.Collections.Concurrent;
using System.Text.Json;
using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Controller.Actions;
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
        _states[command.ActionInstanceId] = new(ClientActionState.Running);
        ServerActionExecutionResult converted;
        try
        {
            // COMMAND 外层不再包含 actionType，业务路由唯一取自 input.MainAction.actionType。
            var embeddedAction = ReadMainActionType(command.Input);
            var receivedTemplate = ParseRequired<MainActionMessage>(command.Input).MainAction;
            var templateErrors = MainActionTemplateValidator.Validate(receivedTemplate);
            if (templateErrors.Count > 0)
                throw new ArgumentException(
                    $"MainAction 模板安全校验失败：{string.Join(" ", templateErrors)}");
            converted = embeddedAction switch
                {
                    // 从 input.MainAction 反序列化具体 MOVE 主动作，再按其中 phases 调用设备。
                    MainAction.Move => Convert(await _robot.ExecuteMoveAsync(
                        ParseRequired<MoveActionMessage>(command.Input).MainAction, cancellationToken)),
                    MainAction.ArmPick => Convert(await _robot.ExecutePickAsync(
                        receivedTemplate, cancellationToken)),
                    MainAction.ArmPlace => Convert(await _robot.ExecutePlaceAsync(
                        receivedTemplate, cancellationToken)),
                    MainAction.ArmPickBatch => Convert(await _robot.ExecutePickBatchAsync(
                        receivedTemplate, cancellationToken)),
                    MainAction.ArmPlaceBatch => Convert(await _robot.ExecutePlaceBatchAsync(
                        receivedTemplate, cancellationToken)),
                    MainAction.ArmHome => Convert(await _robot.ExecuteHomeAsync(
                        receivedTemplate, cancellationToken)),
                    MainAction.VisionCapture => Convert(await _robot.ExecuteCaptureAsync(
                        receivedTemplate, cancellationToken)),
                    _ => ServerActionExecutionResult.Failed(4004,
                        $"不支持 MainAction：{embeddedAction.ToActionType()}")
                };
        }
        catch (JsonException ex)
        {
            converted = ServerActionExecutionResult.Failed(4000, $"Action Input 格式错误：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            converted = ServerActionExecutionResult.Failed(4000, ex.Message);
        }

        _states[command.ActionInstanceId] = new(converted.State, converted.PhysicalResult, converted.ResolvedSteps, converted.Error);
        return converted;
    }

    public Task<ServerActionQueryResult> QueryAsync(string actionInstanceId, string deviceCommandId, CancellationToken cancellationToken) =>
        Task.FromResult(_states.TryGetValue(actionInstanceId, out var state)
            ? state
            : new ServerActionQueryResult(ClientActionState.Unknown,
                Error: new ActionError(5004, "客户端没有该动作实例的可确认记录", PhysicalResultKnown: false)));

    private static ServerActionExecutionResult Convert<T>(DeviceResult<T> result)
    {
        var steps = result.Steps?.Select(x => new ResolvedStep(x.Sequence, x.PhaseId, x.SubAction, x.State,
            x.Evidence is null ? null : JsonSerializer.SerializeToElement(x.Evidence, ServerActionJson.Default),
            x.SlotId, x.CacheSlot, x.PoseRef)).ToArray();
        if (result.Success)
            return ServerActionExecutionResult.PhysicalDone(result.Value, steps);

        var error = result.Error ?? new DeviceError(5001, "设备返回失败但没有错误信息。", PhysicalResultKnown: false);
        return error.PhysicalResultKnown
            ? new ServerActionExecutionResult(ClientActionState.Failed, ResolvedSteps: steps,
                Error: new ActionError(error.Code, error.Message, error.DeviceCode, true,
                    error.Retryable, error.Category, error.RecoveryStrategy, error.HandlingAdvice))
            : new ServerActionExecutionResult(ClientActionState.Unknown, ResolvedSteps: steps,
                Error: new ActionError(error.Code, error.Message, error.DeviceCode, false,
                    error.Retryable, error.Category, error.RecoveryStrategy, error.HandlingAdvice));
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
