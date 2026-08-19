namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>
/// L1 子动作参数模板的抽象基类。
/// 每个子动作有固定的参数 Schema；phase.params（JsonObject）运行时反序列化为对应的具体模板。
/// </summary>
public abstract record SubActionTemplate
{
    /// <summary>该参数模板对应的子动作。</summary>
    public abstract SubAction SubAction { get; }
}
