using System.Text.Json.Serialization;

namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>
/// 阶段失败处置（Action 配置规范：失败处置枚举）。
/// 5.1 片段示例仅列 ABORT / RETRY_PHASE；完整规范另含 VERIFY_BEFORE_RETRY、SKIP，
/// 且现有模板 JSON（ARM.PICK / ARM.PLACE / ARM.HOME 等）已使用 VERIFY_BEFORE_RETRY，
/// 故此处保留全部 4 值以保证反序列化兼容。如确需严格收窄为 2 值，删除后两项即可。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PhaseFailAction>))]
public enum PhaseFailAction
{
    /// <summary>中止主动作（闸门阶段常用，如视觉复核/夹持校验失败）。</summary>
    ABORT,

    /// <summary>重试本阶段。</summary>
    RETRY_PHASE,

    /// <summary>复核后重试：前置条件（互锁）不满足不启动，取放类强制。</summary>
    VERIFY_BEFORE_RETRY,

    /// <summary>跳过本阶段，继续后续阶段。</summary>
    SKIP
}
