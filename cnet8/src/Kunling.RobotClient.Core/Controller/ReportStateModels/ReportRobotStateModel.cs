using Kunling.RobotClient.Core.Controller.Templates;

namespace Kunling.RobotClient.Core.Controller.ReportStateModels;

/// <summary>
/// 机器人向调度服务器上报的统一执行状态。服务器界面和日志只需解析本模型，
/// 不再分别推断 ActionEvent、resolvedSteps 和 error.context。
/// </summary>
public sealed record ReportRobotStateModel(
    string RobotName,
    string RobotState,
    string ActionInstanceId,
    ReportMainActionStateModel MainAction,
    ReportSubActionStateModel? SubAction,
    DateTimeOffset Timestamp);

/// <summary>L2 主动作现场。</summary>
public sealed record ReportMainActionStateModel(string Name, MainActionState State);

/// <summary>L1 子动作/Phase 现场及其具体错误。</summary>
public sealed record ReportSubActionStateModel(
    string Name,
    string State,
    string? PhaseId = null,
    string? OnFail = null,
    string? Code = null,
    string? Msg = null,
    UnifiedRobotErrorModel? Error = null);
