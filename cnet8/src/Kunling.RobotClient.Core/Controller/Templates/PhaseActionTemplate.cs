using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>
/// 5.1 phase 结构：主动作模板中的一个阶段（phase）。
/// 字段与《Action 配置设计规范》5.1 严格对齐，可直接反序列化
/// appsettings / *.Templates.json 中的阶段定义。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class PhaseActionTemplate
{
    /// <summary>阶段标识（模板内唯一）。</summary>
    [JsonPropertyName("phaseId")]
    public string PhaseId { get; set; } = string.Empty;

    /// <summary>子动作：第 4 节子动作之一（ArmPickMoveToPose / GRIP_OPEN / VISION_VERIFY_MATERIAL …）。</summary>
    [JsonPropertyName("subAction")]
    public SubAction SubAction { get; set; }

    /// <summary>是否启用。工位级可覆写，用于开关视觉复核等。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 该子动作参数；未显式给出的从档案/配方继承。
    /// 用 JsonObject 保留任意键值与嵌套结构，运行时按“覆盖层”与档案/配方合并。
    /// </summary>
    [JsonPropertyName("params")]
    public JsonObject? Parameters { get; set; } = new();

    /// <summary>是否为“闸门阶段”：失败则中止主动作（如夹持校验、视觉复核）。</summary>
    [JsonPropertyName("gate")]
    public bool Gate { get; set; }

    /// <summary>失败处置：ABORT / RETRY_PHASE / VERIFY_BEFORE_RETRY / SKIP（见 PhaseFailAction）。</summary>
    [JsonPropertyName("onFail")]
    public PhaseFailAction OnFail { get; set; }
}
