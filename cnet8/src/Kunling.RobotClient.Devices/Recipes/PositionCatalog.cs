using System.Text.Json;
using System.Text.Json.Serialization;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Devices.Recipes;

/// <summary>MOVE 使用的站点与对接窗口配置。</summary>
public sealed class PositionCatalog
{
    [JsonPropertyName("positions")]
    public List<PositionDefinition> Positions { get; init; } = [];

    public ChassisRecipe Resolve(string target, string? port)
    {
        var position = Positions.FirstOrDefault(x =>
            x.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"未找到 MOVE 站点：{target}");
        var portName = string.IsNullOrWhiteSpace(port) ? position.DefaultPort : port;
        var selectedPort = position.Ports.FirstOrDefault(x =>
            x.Name.Equals(portName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"站点 {target} 未找到 port：{portName}");

        // port 保存最终对接位姿；上游只需要传 target 和可选 port 名称。
        return new ChassisRecipe(selectedPort.Pose, selectedPort.Arrival ?? position.Arrival,
            selectedPort.Speed ?? position.Speed, selectedPort.MoveType,
            selectedPort.TargetType, selectedPort.AccuracyLevel, selectedPort.AvoidanceProfile);
    }
}

public sealed record PositionDefinition(
    string Name,
    string DefaultPort,
    IReadOnlyList<PositionPort> Ports,
    ArrivalCriteria Arrival,
    double Speed = 0.5);

public sealed record PositionPort(string Name, RobotPose Pose, ArrivalCriteria? Arrival = null,
    double? Speed = null, int? MoveType = null, byte? TargetType = null,
    byte? AccuracyLevel = null, string? AvoidanceProfile = null);
public sealed record ArrivalCriteria(double PositionToleranceMm = 2, double AngleToleranceDeg = 1,
    int SettleMs = 200, int TimeoutMs = 10_000, int PollMs = 50);
public sealed record ChassisRecipe(RobotPose Pose, ArrivalCriteria Arrival, double Speed = 0.5,
    int? MoveType = null, byte? TargetType = null, byte? AccuracyLevel = null, string? AvoidanceProfile = null);

public static class PositionCatalogLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static PositionCatalog Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 MOVE 站点配置。", path);
        var catalog = JsonSerializer.Deserialize<PositionCatalog>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("MOVE 站点配置为空。");
        if (catalog.Positions.Count == 0) throw new InvalidDataException("MOVE 站点配置至少需要一个站点。");
        var duplicate = catalog.Positions.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"MOVE 站点名称重复：{duplicate.Key}");
        foreach (var position in catalog.Positions)
        {
            if (string.IsNullOrWhiteSpace(position.Name) || position.Ports.Count == 0)
                throw new InvalidDataException("MOVE站点名称不能为空且至少配置一个port。");
            var duplicatePort = position.Ports.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => x.Count() > 1);
            if (duplicatePort is not null) throw new InvalidDataException($"站点 {position.Name} 的port名称重复：{duplicatePort.Key}");
            if (!position.Ports.Any(x => x.Name.Equals(position.DefaultPort, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"站点 {position.Name} 的defaultPort不存在：{position.DefaultPort}");
            ValidateArrival(position.Name, position.Arrival);
            foreach (var port in position.Ports)
            {
                if (string.IsNullOrWhiteSpace(port.Name) || string.IsNullOrWhiteSpace(port.Pose.Map))
                    throw new InvalidDataException($"站点 {position.Name} 的port名称和map不能为空。");
                if (!double.IsFinite(port.Pose.X) || !double.IsFinite(port.Pose.Y) || !double.IsFinite(port.Pose.Yaw)
                    || port.Pose.Yaw is < -360 or > 360)
                    throw new InvalidDataException($"站点 {position.Name}/{port.Name} 的坐标或角度无效。");
                if (port.Arrival is not null) ValidateArrival($"{position.Name}/{port.Name}", port.Arrival);
                if (port.MoveType is < 0 or > 9) throw new InvalidDataException($"站点 {position.Name}/{port.Name} 的moveType无效。");
            }
        }
        return catalog;
    }

    private static void ValidateArrival(string name, ArrivalCriteria value)
    {
        if (value.PositionToleranceMm <= 0 || value.AngleToleranceDeg is <= 0 or > 180
            || value.SettleMs < 0 || value.TimeoutMs <= 0 || value.PollMs <= 0)
            throw new InvalidDataException($"{name} 的arrival配置无效。");
    }
}
