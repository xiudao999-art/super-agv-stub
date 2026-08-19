using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.L1SubActions.L1SubActionTemplates;

/// <summary>
/// L1 子动作 MOVE_TO_POSE 的强类型 phase 模板。
/// 用于把机械臂移动到 SAFE、APPROACH、PICK、PLACE、RETREAT、HOME 等角色位姿，
/// 并携带设备执行及真实到位判定所需的全部参数。
/// </summary>
public sealed class L1SubActionMoveToPose : PhaseActionTemplate
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 创建标准 MOVE_TO_POSE phase。参数暂不绑定具体工位，适合反序列化或稍后调用 SetRequest。
    /// </summary>
    public L1SubActionMoveToPose()
    {
        PhaseId = "moveToPose";
        SubAction = SubAction.MOVE_TO_POSE;
        Enabled = true;
        Parameters = CreateDefaultParameters();
        Gate = false;
        OnFail = PhaseFailAction.RETRY_PHASE;
    }

    /// <summary>使用设备层强类型请求创建可直接执行的 phase。</summary>
    public L1SubActionMoveToPose(
        string phaseId,
        ArmMoveRequest request,
        bool gate = false,
        PhaseFailAction onFail = PhaseFailAction.RETRY_PHASE) : this()
    {
        if (string.IsNullOrWhiteSpace(phaseId))
            throw new ArgumentException("phaseId 不能为空。", nameof(phaseId));

        PhaseId = phaseId;
        Gate = gate;
        OnFail = onFail;
        SetRequest(request);
    }

    /// <summary>
    /// 使用已有 params 创建 phase。用于主动作工厂保留 graspProfile、orderPolicy 等附加编排参数。
    /// </summary>
    public L1SubActionMoveToPose(
        string phaseId,
        JsonObject parameters,
        bool gate = false,
        PhaseFailAction onFail = PhaseFailAction.RETRY_PHASE) : this()
    {
        if (string.IsNullOrWhiteSpace(phaseId))
            throw new ArgumentException("phaseId 不能为空。", nameof(phaseId));
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateRequiredParameters(phaseId, parameters);

        PhaseId = phaseId;
        Parameters = parameters.DeepClone().AsObject();
        Gate = gate;
        OnFail = onFail;
    }

    /// <summary>将机械臂移动请求完整写入 params。</summary>
    public void SetRequest(ArmMoveRequest request)
    {
        ValidateRequest(request);
        Parameters ??= new JsonObject();
        Parameters["station"] = request.Station;
        Parameters["poseRole"] = request.PoseRole;
        Parameters["point"] = request.Point;
        Parameters["pose"] = JsonSerializer.SerializeToNode(request.Pose, JsonOptions);
        Parameters["positionToleranceMm"] = request.PositionToleranceMm;
        Parameters["angleToleranceDeg"] = request.AngleToleranceDeg;
        Parameters["settleMs"] = request.SettleMs;
        Parameters["timeoutMs"] = request.TimeoutMs;
        Parameters["pollMs"] = request.PollMs;
        Parameters["frame"] = request.Frame;
        Parameters["speedProfile"] = request.SpeedProfile;
        Parameters["collisionProfile"] = request.CollisionProfile;
    }

    /// <summary>从当前 phase 还原 IArm.MoveToPoseAsync 所需请求。</summary>
    public ArmMoveRequest ResolveRequest(string? fallbackStation = null, string? fallbackPoint = null) =>
        ResolveRequest(this, fallbackStation, fallbackPoint);

    /// <summary>
    /// 从反序列化得到的基础 PhaseActionTemplate 还原 ArmMoveRequest，
    /// 因此模板加载器不需要启用 JSON 多态也能调用本子动作实现。
    /// </summary>
    public static ArmMoveRequest ResolveRequest(
        PhaseActionTemplate phase,
        string? fallbackStation = null,
        string? fallbackPoint = null)
    {
        ArgumentNullException.ThrowIfNull(phase);
        if (phase.SubAction != SubAction.MOVE_TO_POSE)
            throw new InvalidDataException($"phase {phase.PhaseId} 不是 MOVE_TO_POSE 子动作。");

        var p = phase.Parameters
            ?? throw new InvalidDataException($"phase {phase.PhaseId} 缺少 params。");
        var station = OptionalString(p, "station") ?? fallbackStation;
        var poseRole = OptionalString(p, "poseRole");
        if (string.IsNullOrWhiteSpace(station))
            throw new InvalidDataException($"phase {phase.PhaseId} 的 station 不能为空。");
        if (string.IsNullOrWhiteSpace(poseRole))
            throw new InvalidDataException($"phase {phase.PhaseId} 的 poseRole 不能为空。");

        var request = new ArmMoveRequest(
            station,
            poseRole,
            OptionalString(p, "point") ?? fallbackPoint,
            p["pose"]?.Deserialize<ArmPose>(JsonOptions),
            GetDouble(p, "positionToleranceMm", 2),
            GetDouble(p, "angleToleranceDeg", 1),
            GetInt(p, "settleMs", 200),
            GetInt(p, "timeoutMs", 10_000),
            GetInt(p, "pollMs", 50),
            OptionalString(p, "frame") ?? "BASE",
            OptionalString(p, "speedProfile") ?? "NORMAL",
            OptionalString(p, "collisionProfile") ?? "NORMAL");
        ValidateRequest(request);
        return request;
    }

    private static JsonObject CreateDefaultParameters() => new()
    {
        ["station"] = string.Empty,
        ["poseRole"] = string.Empty,
        ["point"] = null,
        ["pose"] = null,
        ["positionToleranceMm"] = 2,
        ["angleToleranceDeg"] = 1,
        ["settleMs"] = 200,
        ["timeoutMs"] = 10_000,
        ["pollMs"] = 50,
        ["frame"] = "BASE",
        ["speedProfile"] = "NORMAL",
        ["collisionProfile"] = "NORMAL"
    };

    private static void ValidateRequiredParameters(string phaseId, JsonObject parameters)
    {
        if (string.IsNullOrWhiteSpace(OptionalString(parameters, "station")))
            throw new InvalidDataException($"phase {phaseId} 的 station 不能为空。");
        if (string.IsNullOrWhiteSpace(OptionalString(parameters, "poseRole")))
            throw new InvalidDataException($"phase {phaseId} 的 poseRole 不能为空。");
    }

    private static void ValidateRequest(ArmMoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Station))
            throw new InvalidDataException("MOVE_TO_POSE station 不能为空。");
        if (string.IsNullOrWhiteSpace(request.PoseRole))
            throw new InvalidDataException("MOVE_TO_POSE poseRole 不能为空。");
        if (request.Pose is not null &&
            (!double.IsFinite(request.Pose.X) || !double.IsFinite(request.Pose.Y) ||
             !double.IsFinite(request.Pose.Z) || !double.IsFinite(request.Pose.Rx) ||
             !double.IsFinite(request.Pose.Ry) || !double.IsFinite(request.Pose.Rz)))
            throw new InvalidDataException("MOVE_TO_POSE pose 必须全部为有效数字。");
        if (request.PositionToleranceMm <= 0 || request.AngleToleranceDeg <= 0 ||
            request.AngleToleranceDeg > 180 || request.SettleMs < 0 ||
            request.TimeoutMs <= 0 || request.PollMs <= 0 || request.PollMs > request.TimeoutMs)
            throw new InvalidDataException("MOVE_TO_POSE 到位判定参数无效。");
        if (string.IsNullOrWhiteSpace(request.Frame) || string.IsNullOrWhiteSpace(request.SpeedProfile) ||
            string.IsNullOrWhiteSpace(request.CollisionProfile))
            throw new InvalidDataException("MOVE_TO_POSE frame、speedProfile、collisionProfile 不能为空。");
    }

    private static string? OptionalString(JsonObject parameters, string key) =>
        parameters[key]?.GetValue<string>();

    private static int GetInt(JsonObject parameters, string key, int fallback) =>
        parameters[key]?.GetValue<int>() ?? fallback;

    private static double GetDouble(JsonObject parameters, string key, double fallback) =>
        parameters[key]?.GetValue<double>() ?? fallback;
}
