using System.Text.Json.Serialization;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.Templates.MoveActionTemplates;

/// <summary>
/// MOVE 使用的站点位置定义，对应 position.json 中 positions 数组的一个元素。
/// 一个逻辑站点可以包含多个窗口或对接点（port），调用 MOVE 时通过站点名和可选 port 定位。
/// </summary>
public sealed record PointInfo
{
    /// <summary>站点唯一名称，例如 P01、STANDBY。</summary>
    [JsonPropertyName("pointName")]
    public required string PointName { get; init; }

    [JsonPropertyName("pose")]
    public required RobotPose Pose { get; init; }
    /// <summary>port 级到位条件；为空时继承站点的 Arrival。</summary>
    [JsonPropertyName("arrival")]
    public PointArrivalInfo? Arrival { get; init; }

    /// <summary>底盘移动速度，直接使用数值，不再使用速度档位名称。</summary>
    [JsonPropertyName("speed")]
    public double Speed { get; init; } = 0.5;
    /// <summary>在程序启动阶段校验位置配置，避免无效坐标进入设备执行层。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PointName))
            throw new InvalidDataException("MOVE 点位的 pointName 不能为空。");
        if (!double.IsFinite(Pose.X) || !double.IsFinite(Pose.Y) || !double.IsFinite(Pose.Yaw))
            throw new InvalidDataException($"MOVE 点位 {PointName} 的位姿必须是有效数字。");
        if (string.IsNullOrWhiteSpace(Pose.Map))
            throw new InvalidDataException($"MOVE 点位 {PointName} 的 map 不能为空。");
        if (!double.IsFinite(Speed) || Speed <= 0)
            throw new InvalidDataException($"MOVE 点位 {PointName} 的 speed 必须大于 0。");
        if (Arrival is null)
            return;
        if (!double.IsFinite(Arrival.PositionToleranceMm) || Arrival.PositionToleranceMm <= 0)
            throw new InvalidDataException($"MOVE 点位 {PointName} 的位置容差必须大于 0。");
        if (!double.IsFinite(Arrival.AngleToleranceDeg) || Arrival.AngleToleranceDeg <= 0 || Arrival.AngleToleranceDeg > 180)
            throw new InvalidDataException($"MOVE 点位 {PointName} 的角度容差必须在 (0, 180] 范围内。");
        if (Arrival.TimeoutMs <= 0)
            throw new InvalidDataException($"MOVE 点位 {PointName} 的到位超时必须大于 0。");
    }
}

 

/// <summary>位置到位判定参数。</summary>
public sealed record PointArrivalInfo
{
    [JsonPropertyName("positionToleranceMm")]
    public double PositionToleranceMm { get; init; } = 5;

    [JsonPropertyName("angleToleranceDeg")]
    public double AngleToleranceDeg { get; init; } = 5;

 

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; init; } = 30_000;

 

 
}
