using System.Text.Json;
using Kunling.RobotClient.Core.Controller.Templates;

namespace Kunling.RobotClient.Actions.ServerActions;

public interface IServerActionExecutionContext
{
    ActionCommand Command { get; }
    ValueTask ReportRunningAsync(IReadOnlyList<ResolvedStep>? steps = null,
        PhaseExecutionEvent? phaseEvent = null, CancellationToken cancellationToken = default);
}

public interface IServerActionExecutor
{
    bool CanExecute(string actionType, string actionVersion, ExecutionMode executionMode);
    Task<ServerActionExecutionResult> ExecuteAsync(IServerActionExecutionContext context, CancellationToken cancellationToken);
    Task<ServerActionQueryResult> QueryAsync(string actionInstanceId, string deviceCommandId, CancellationToken cancellationToken);
}

public sealed record ServerActionExecutionResult(
    MainActionState State,
    JsonElement? PhysicalResult = null,
    IReadOnlyList<ResolvedStep>? ResolvedSteps = null,
    ActionError? Error = null)
{
    public static ServerActionExecutionResult PhysicalDone(object? result = null, IReadOnlyList<ResolvedStep>? steps = null) =>
        new(MainActionState.Finished, result is null ? null : JsonSerializer.SerializeToElement(result, ServerActionJson.Default), steps);

    public static ServerActionExecutionResult Failed(int code, string message, bool physicalResultKnown = true, string? deviceCode = null) =>
        new(MainActionState.Error, Error: new ActionError(code, message, deviceCode, physicalResultKnown));

    public static ServerActionExecutionResult Unknown(int code, string message) =>
        new(MainActionState.Hang, Error: new ActionError(code, message, PhysicalResultKnown: false));

    public static ServerActionExecutionResult Busy(string message, ActionFailureContext context) =>
        new(MainActionState.Busy, Error: new ActionError(PlatformErrorCodes.RobotBusy, message, PhysicalResultKnown: true,
            Retryable: true, Category: Kunling.RobotClient.Core.Models.DeviceErrorCategory.State,
            RecoveryStrategy: Kunling.RobotClient.Core.Models.DeviceRecoveryStrategy.WaitAndRetry,
            HandlingAdvice: "机器人正在执行其他动作，请等待当前动作结束后重试。", Context: context));
}

public sealed record ServerActionQueryResult(
    MainActionState State,
    JsonElement? PhysicalResult = null,
    IReadOnlyList<ResolvedStep>? ResolvedSteps = null,
    ActionError? Error = null);

public interface IRobotSnapshotProvider
{
    ValueTask<RobotStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 由 Action 客户端通知快照提供器当前正在执行的动作。
/// 心跳线程据此上报 IDLE/EXECUTING，而不是始终返回固定状态。
/// </summary>
public interface IRobotActivitySnapshotProvider : IRobotSnapshotProvider
{
    void SetCurrentAction(string? actionInstanceId, string? state = null);
}

public sealed class DefaultRobotSnapshotProvider : IRobotActivitySnapshotProvider
{
    private string? _currentAction;
    private string? _state;

    public void SetCurrentAction(string? actionInstanceId, string? state = null)
    {
        Interlocked.Exchange(ref _currentAction, actionInstanceId);
        Interlocked.Exchange(ref _state, state);
    }

    public ValueTask<RobotStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var currentAction = Volatile.Read(ref _currentAction);
        var state = Volatile.Read(ref _state);
        return ValueTask.FromResult(new RobotStateSnapshot(
            currentAction is null ? "IDLE" : state ?? "EXECUTING",
            Battery: null,
            Emergency: false,
            ChassisConnected: true,
            ArmConnected: true,
            CurrentActionInstanceId: currentAction,
            Timestamp: DateTimeOffset.UtcNow));
    }
}
