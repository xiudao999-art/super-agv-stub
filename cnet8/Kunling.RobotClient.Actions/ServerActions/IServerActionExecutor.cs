using System.Text.Json;

namespace Kunling.RobotClient.Actions.ServerActions;

public interface IServerActionExecutionContext
{
    ActionCommand Command { get; }
    ValueTask ReportRunningAsync(IReadOnlyList<ResolvedStep>? steps = null, JsonElement? evidence = null, CancellationToken cancellationToken = default);
}

public interface IServerActionExecutor
{
    bool CanExecute(string actionType, string actionVersion, ExecutionMode executionMode);
    Task<ServerActionExecutionResult> ExecuteAsync(IServerActionExecutionContext context, CancellationToken cancellationToken);
    Task<ServerActionQueryResult> QueryAsync(string actionInstanceId, string deviceCommandId, CancellationToken cancellationToken);
}

public sealed record ServerActionExecutionResult(
    ClientActionState State,
    JsonElement? PhysicalResult = null,
    IReadOnlyList<ResolvedStep>? ResolvedSteps = null,
    ActionError? Error = null)
{
    public static ServerActionExecutionResult PhysicalDone(object? result = null, IReadOnlyList<ResolvedStep>? steps = null) =>
        new(ClientActionState.PhysicalDone, result is null ? null : JsonSerializer.SerializeToElement(result, ServerActionJson.Default), steps);

    public static ServerActionExecutionResult Failed(int code, string message, bool physicalResultKnown = true, string? deviceCode = null) =>
        new(ClientActionState.Failed, Error: new ActionError(code, message, deviceCode, physicalResultKnown));

    public static ServerActionExecutionResult Unknown(int code, string message) =>
        new(ClientActionState.Unknown, Error: new ActionError(code, message, PhysicalResultKnown: false));
}

public sealed record ServerActionQueryResult(
    ClientActionState State,
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
    void SetCurrentAction(string? actionInstanceId);
}

public sealed class DefaultRobotSnapshotProvider : IRobotActivitySnapshotProvider
{
    private string? _currentAction;

    public void SetCurrentAction(string? actionInstanceId) =>
        Interlocked.Exchange(ref _currentAction, actionInstanceId);

    public ValueTask<RobotStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var currentAction = Volatile.Read(ref _currentAction);
        return ValueTask.FromResult(new RobotStateSnapshot(
            currentAction is null ? "IDLE" : "EXECUTING",
            Battery: null,
            Emergency: false,
            ChassisConnected: true,
            ArmConnected: true,
            CurrentActionInstanceId: currentAction,
            Timestamp: DateTimeOffset.UtcNow));
    }
}
