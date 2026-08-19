using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>
/// L2 主动作清单。这些动作可以注册给调度服务器并由上游直接调用；
/// ArmPickMoveToPose、GRIP_OPEN 等 L1 子动作不能出现在该枚举中。
/// </summary>
[JsonConverter(typeof(MainActionJsonConverter))]
public enum MainAction
{
    Move,
    ArmPick,
    ArmPlace,
    ArmPickBatch,
    ArmPlaceBatch,
    ArmHome,
    VisionCapture
}

/// <summary>主动作协议名的唯一映射，供注册、命令解析和模板加载共同使用。</summary>
public static class MainActionCatalog
{
    private static readonly IReadOnlyDictionary<MainAction, string> Names =
        new Dictionary<MainAction, string>
        {
            [MainAction.Move] = "MOVE",
            [MainAction.ArmPick] = "ARM.PICK",
            [MainAction.ArmPlace] = "ARM.PLACE",
            [MainAction.ArmPickBatch] = "ARM.PICK_BATCH",
            [MainAction.ArmPlaceBatch] = "ARM.PLACE_BATCH",
            [MainAction.ArmHome] = "ARM.HOME",
            [MainAction.VisionCapture] = "VISION.CAPTURE"
        };

    public static IReadOnlyList<MainActionDefinition> All { get; } =
        Names.Select(x => new MainActionDefinition(x.Key, x.Value)).ToArray();

    public static string ToActionType(this MainAction action) =>
        Names.TryGetValue(action, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(action));

    public static bool TryParse(string? value, out MainAction action)
    {
        foreach (var item in Names)
            if (item.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) { action = item.Key; return true; }
        action = default;
        return false;
    }

    public static MainAction Parse(string value) => TryParse(value, out var action)
        ? action : throw new ArgumentException($"不支持的主动作：{value}", nameof(value));
}

public sealed record MainActionDefinition(MainAction Action, string ActionType);

public sealed class MainActionJsonConverter : JsonConverter<MainAction>
{
    public override MainAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && MainActionCatalog.TryParse(reader.GetString(), out var action)
            ? action : throw new JsonException("无效的主动作 actionType。");

    public override void Write(Utf8JsonWriter writer, MainAction value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToActionType());
}

