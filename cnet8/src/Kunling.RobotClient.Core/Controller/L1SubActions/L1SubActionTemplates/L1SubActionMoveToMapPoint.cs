using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.Templates.MoveActionTemplates;

/// <summary>
/// L1 子动作 MOVE_TO_MAP_POINT 的强类型 phase 模板。
/// JSON 严格保持 phaseId、subAction、enabled、params、gate、onFail 六个字段；
/// 点位、端口、速度、位姿和到位条件全部封装在 params 内。
/// </summary>
public sealed class L1SubActionMoveToMapPoint : PhaseActionTemplate
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>创建尚未绑定具体点位的标准 MOVE_TO_MAP_POINT phase。</summary>
    public L1SubActionMoveToMapPoint()
    {
        PhaseId = "moveToMapPoint";
        SubAction = SubAction.MOVE_TO_MAP_POINT;
        Enabled = true;
        Parameters = new JsonObject
        {
            ["pointName"] = string.Empty,
            ["port"] = null,
            ["speed"] = 0.5,
            ["pose"] = null,
            ["arrival"] = null
        };
        Gate = true;
        OnFail = PhaseFailAction.ABORT;
    }

    /// <summary>把 MOVE.Templates.json 中的一个点位转换为可下发 phase。</summary>
    public L1SubActionMoveToMapPoint(PointInfo pointInfo, string? port = null) : this()
    {
        ArgumentNullException.ThrowIfNull(pointInfo);
        pointInfo.Validate();
        SetRequest(new MoveRequest(
            pointInfo.PointName,
            pointInfo.Speed,
            pointInfo.Pose,
            pointInfo.Arrival is null
                ? null
                : new MoveArrivalRequest(
                    pointInfo.Arrival.PositionToleranceMm,
                    pointInfo.Arrival.AngleToleranceDeg,
                    pointInfo.Arrival.TimeoutMs),
            port));
    }

    /// <summary>直接使用服务器组装完成的请求创建 phase。</summary>
    public L1SubActionMoveToMapPoint(MoveRequest request) : this() => SetRequest(request);

    /// <summary>将请求写入 params，供 JSON 序列化及通用执行器读取。</summary>
    public void SetRequest(MoveRequest request)
    {
        ValidateRequest(request);
        Parameters ??= new JsonObject();
        Parameters["pointName"] = request.PointName;
        Parameters["port"] = request.Port;
        Parameters["speed"] = request.Speed;
        Parameters["pose"] = JsonSerializer.SerializeToNode(request.Pose, JsonOptions);
        Parameters["arrival"] = JsonSerializer.SerializeToNode(request.Arrival, JsonOptions);
    }

    /// <summary>从当前 phase 的 params 还原设备层 MoveRequest。</summary>
    public MoveRequest ResolveRequest() => ResolveRequest(this);

    /// <summary>
    /// 从基础 phase 还原 MoveRequest。即使 JSON 加载器创建的是基类实例，
    /// 也可以通过本方法调用正确的 MOVE_TO_MAP_POINT 处理流程。
    /// </summary>
    public static MoveRequest ResolveRequest(PhaseActionTemplate phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        if (phase.SubAction != SubAction.MOVE_TO_MAP_POINT)
            throw new InvalidDataException($"phase {phase.PhaseId} 不是 MOVE_TO_MAP_POINT 子动作。");

        var parameters = phase.Parameters
            ?? throw new InvalidDataException($"phase {phase.PhaseId} 缺少 params。");
        var pointName = parameters["pointName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(pointName))
            throw new InvalidDataException($"phase {phase.PhaseId} 的 pointName 不能为空。");

        var request = new MoveRequest(
            pointName,
            parameters["speed"]?.GetValue<double>() ?? 0.5,
            parameters["pose"]?.Deserialize<RobotPose>(JsonOptions),
            parameters["arrival"]?.Deserialize<MoveArrivalRequest>(JsonOptions),
            parameters["port"]?.GetValue<string>());
        ValidateRequest(request);
        return request;
    }

    private static void ValidateRequest(MoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PointName))
            throw new InvalidDataException("MOVE pointName 不能为空。");
        if (!double.IsFinite(request.Speed) || request.Speed <= 0)
            throw new InvalidDataException("MOVE speed 必须大于 0。");
        if (request.Pose is not null &&
            (!double.IsFinite(request.Pose.X) || !double.IsFinite(request.Pose.Y) ||
             !double.IsFinite(request.Pose.Yaw) || string.IsNullOrWhiteSpace(request.Pose.Map)))
            throw new InvalidDataException("MOVE pose 的坐标、角度和 map 必须有效。");
        if (request.Arrival is not null &&
            (request.Arrival.PositionToleranceMm <= 0 || request.Arrival.AngleToleranceDeg <= 0 ||
             request.Arrival.AngleToleranceDeg > 180 || request.Arrival.TimeoutMs <= 0))
            throw new InvalidDataException("MOVE arrival 参数无效。");
    }
}
