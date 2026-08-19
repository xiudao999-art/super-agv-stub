using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>L1 子动作内部枚举；JSON 协议名由 SubActionJsonConverter 统一映射。</summary>
[JsonConverter(typeof(SubActionJsonConverter))]
public enum SubAction
{
    MOVE_TO_MAP_POINT,
    MOVE_TO_POSE,
    GRIP_OPEN,
    GRIP_CLOSE,
    GRIP_VERIFY_LOAD,
    VISION_VERIFY_MATERIAL,
    VISION_VERIFY_PLACEMENT,
    VISION_CAPTURE,
    CHASSIS_VERIFY_STOPPED,
    ARM_VERIFY_HOME
}

public static class SubActionCatalog
{
    private static readonly IReadOnlyDictionary<SubAction, string> Names =
        new Dictionary<SubAction, string>
        {
            [SubAction.MOVE_TO_MAP_POINT] = "MOVE_TO_MAP_POINT",
            [SubAction.MOVE_TO_POSE] = "MOVE_TO_POSE",
            [SubAction.GRIP_OPEN] = "GRIP.OPEN",
            [SubAction.GRIP_CLOSE] = "GRIP.CLOSE",
            [SubAction.GRIP_VERIFY_LOAD] = "GRIP.VERIFY_LOAD",
            [SubAction.VISION_VERIFY_MATERIAL] = "VISION.VERIFY_MATERIAL",
            [SubAction.VISION_VERIFY_PLACEMENT] = "VISION.VERIFY_PLACEMENT",
            [SubAction.VISION_CAPTURE] = "VISION.CAPTURE",
            [SubAction.CHASSIS_VERIFY_STOPPED] = "CHASSIS_VERIFY_STOPPED",
            [SubAction.ARM_VERIFY_HOME] = "ARM_VERIFY_HOME"
        };

    public static string ToProtocolName(this SubAction action) =>
        Names.TryGetValue(action, out var name)
            ? name : throw new ArgumentOutOfRangeException(nameof(action));

    public static bool TryParse(string? value, out SubAction action)
    {
        foreach (var item in Names)
            if (item.Value.Equals(value, StringComparison.OrdinalIgnoreCase) ||
                item.Key.ToString().Equals(value, StringComparison.OrdinalIgnoreCase))
            { action = item.Key; return true; }
        action = default;
        return false;
    }
}

/// <summary>输出规范点号名称；读取时兼容历史下划线名称。</summary>
public sealed class SubActionJsonConverter : JsonConverter<SubAction>
{
    public override SubAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("subAction 必须是字符串。");
        var value = reader.GetString();
        if (SubActionCatalog.TryParse(value, out var action)) return action;
        throw new JsonException($"不支持的 subAction：{value}");
    }

    public override void Write(Utf8JsonWriter writer, SubAction value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToProtocolName());
}
