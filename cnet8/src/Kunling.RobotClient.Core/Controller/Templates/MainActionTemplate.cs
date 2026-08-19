using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>
/// 主动作模板（Action 配置规范 §5）。
/// 模板 = 主动作类型 + 有序的 phase 数组；每个 phase 绑定一个子动作，并携带参数、成功判定、失败处置。
/// 对应 JSON 文件（如 ARM.PICK.Templates.json）中 actionTemplates 数组的元素。
/// </summary>
public  class MainActionTemplate
{
    /// <summary>配置模板唯一标识；服务器选择模板后随 MainAction 一并下发，便于审计追踪。</summary>
    [JsonPropertyName("templateId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TemplateId { get; set; }

    /// <summary>所属主动作类型（如 ARM.PICK / ARM.PLACE / ARM.HOME / VISION.CAPTURE）。</summary>
    [JsonPropertyName("actionType")]
    public MainAction ActionType { get; set; }

    /// <summary>模板内有序 phase 列表。</summary>
    [JsonPropertyName("phases")]
    public List<PhaseActionTemplate> Phases { get; set; } = [];

}

/// <summary>服务器 COMMAND.input 的统一 L2 主动作包装结构。</summary>
public sealed class MainActionMessage
{
    [JsonPropertyName("MainAction")]
    public MainActionTemplate MainAction { get; set; } = new();

    public MainActionMessage() { }

    public MainActionMessage(MainActionTemplate mainAction) =>
        MainAction = mainAction ?? throw new ArgumentNullException(nameof(mainAction));
}

 

 
