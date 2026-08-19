using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Devices.HuayanRobot;

/// <summary>
/// 华沿 2026-01-08 错误码的调度侧恢复策略目录。
/// 厂家原始错误文字仍由 HRIF_GetErrorCodeStr 获取；本目录负责分类、处理建议和是否允许自动重试。
/// 未显式列出的错误采用所属号段的保守策略，绝不会因为未知错误而自动重试真实机械臂。
/// </summary>
internal static class HuayanErrorCatalog
{
    private static readonly IReadOnlyDictionary<int, HuayanErrorPolicy> Policies =
        new Dictionary<int, HuayanErrorPolicy>
        {
            [10017] = Safety("安全碰撞错误；检查负载、负载辨识文件和现场碰撞原因，确认安全后复位。"),
            [10027] = Config("轨迹经过奇异点；调整目标姿态、途经点或机械臂构型。"),
            [10054] = Manual("关节超出工作范围；复位后使用长按恢复将机器人移回安全范围。"),
            [10063] = Power("轴组停止失败；确认没有在停止过程中重复下发运动，无法复位时重新上电。"),
            [15018] = Power("运动中轴意外失能；检查安装、驱动及运动错误，确认现场后重新上电。"),
            [15019] = Retry("轴尚未准备好；等待停止流程结束并重新读取状态后，允许有限重试。"),
            [15021] = Config("最大速度超限；降低动作模板速度或检查机型速度上限。"),
            [15022] = Config("最大加速度超限；降低动作模板加速度或检查机型上限。"),
            [15024] = Config("速度或加速度参数无效；修正 motionProfile 后重新下发。"),
            [15060] = Power("底层通讯错误；检查控制器、本体网线和供电，必要时重新上电。"),
            [15068] = Retry("轴组已经在停止；等待 nMovingState=0 后再执行后续动作。"),
            [20018] = State("当前状态禁止执行命令；读取完整状态并消除急停、光幕、暂停或失能条件。"),
            [40015] = State("机器人处于暂停状态；由授权流程继续或终止任务，禁止直接重复运动。"),
            [40029] = State("机器人未使能；确认现场安全后执行轴组使能。"),
            [40034] = Config("输入参数错误；检查接口参数、坐标系、分隔符及结束符。"),
            [40035] = Manual("命令执行超时；先核实实际状态，禁止在结果未知时直接重试。"),
            [40047] = Power("控制器停车失败；确认机器人是否仍在运动，无法复位时重新上电。"),
            [40048] = Power("停车超时；立即确认现场运动状态，必要时执行现场安全停机。"),
            [40053] = State("机器人未上电；检查供电并由授权流程上电。"),
            [40054] = Safety("急停已触发；检查电箱急停和外部急停，人工解除后复位。"),
            [40055] = Safety("安全光幕已触发；检查人员和障碍物，人工确认安全后恢复。"),
            [40082] = Config("速度超过最大限制；降低 motionProfile 速度。"),
            [40083] = Config("加速度超过最大限制；降低 motionProfile 加速度。"),
            [40084] = Config("过渡半径超过最大限制；降低 Radius 或使用精确到位半径 0。"),
            [40102] = State("当前状态禁止执行命令；读取状态机并完成对应恢复操作。"),
            [40092] = Retry("等待 MovePathJ 完成后再重试。"),
            [40093] = Retry("等待 MovePathL 完成后再重试。"),
            [40094] = Retry("等待 MoveZ 完成后再重试。"),
            [49502] = Manual("目标超出关节安全空间；修改点位，禁止自动重试。"),
            [49503] = Config("轨迹存在奇异点；调整点位或增加安全途经点。"),
            [49504] = Manual("目标超出笛卡尔安全空间；检查模板点位和安全空间配置。"),
            [49505] = Manual("规划异常并进入停止保护；检查轨迹合理性并由人工恢复。"),
            [49621] = Power("机器人异常停止；确认现场状态，无法复位时重新上电并联系技术人员。"),
            [49622] = Retry("机器人正在停止；等待停止完成并确认 nMovingState=0 后有限重试。"),
            [60825] = Peripheral("真空检测异常；检查真空源、吸盘、管路和检测信号。"),
            [60826] = Peripheral("螺丝机异常；检查螺丝机状态和外围设备报警。")
        };

    internal static HuayanErrorPolicy Resolve(int code)
    {
        if (Policies.TryGetValue(code, out var policy)) return policy;

        // 未知码采用号段级保守分类。没有厂家明确证据时一律不可自动重试。
        return code switch
        {
            >= 10000 and <= 10068 => new(DeviceErrorCategory.Hardware,
                DeviceRecoveryStrategy.ResetRequired, false, "检查轴、驱动、负载及安全状态；确认现场后复位，必要时重新上电。"),
            >= 15000 and <= 15375 => new(DeviceErrorCategory.Motion,
                DeviceRecoveryStrategy.ResetRequired, false, "检查轴状态、运动参数和总线通讯；确认原因后复位。"),
            >= 20001 and <= 20934 => new(DeviceErrorCategory.Configuration,
                DeviceRecoveryStrategy.CorrectConfiguration, false, "检查厂家命令参数和当前控制器状态。"),
            >= 40000 and <= 40205 => new(DeviceErrorCategory.State,
                DeviceRecoveryStrategy.ManualRecovery, false, "读取完整机器人状态，根据厂家说明处理后再执行。"),
            >= 49502 and <= 49636 => new(DeviceErrorCategory.Safety,
                DeviceRecoveryStrategy.ManualRecovery, false, "检查安全空间、轨迹和停止状态，禁止自动重试。"),
            60825 or 60826 => Peripheral("检查外围设备及其检测信号。"),
            _ => new(DeviceErrorCategory.Unknown, DeviceRecoveryStrategy.ManualRecovery, false,
                "未知厂家错误；保留现场状态并联系华沿技术支持。")
        };
    }

    private static HuayanErrorPolicy Retry(string advice) =>
        new(DeviceErrorCategory.State, DeviceRecoveryStrategy.WaitAndRetry, true, advice);
    private static HuayanErrorPolicy Safety(string advice) =>
        new(DeviceErrorCategory.Safety, DeviceRecoveryStrategy.ManualRecovery, false, advice);
    private static HuayanErrorPolicy Manual(string advice) =>
        new(DeviceErrorCategory.Safety, DeviceRecoveryStrategy.ManualRecovery, false, advice);
    private static HuayanErrorPolicy Power(string advice) =>
        new(DeviceErrorCategory.Hardware, DeviceRecoveryStrategy.PowerCycle, false, advice);
    private static HuayanErrorPolicy Config(string advice) =>
        new(DeviceErrorCategory.Configuration, DeviceRecoveryStrategy.CorrectConfiguration, false, advice);
    private static HuayanErrorPolicy State(string advice) =>
        new(DeviceErrorCategory.State, DeviceRecoveryStrategy.ManualRecovery, false, advice);
    private static HuayanErrorPolicy Peripheral(string advice) =>
        new(DeviceErrorCategory.Peripheral, DeviceRecoveryStrategy.ManualRecovery, false, advice);
}

/// <summary>单个厂家错误在调度层的恢复语义。</summary>
internal sealed record HuayanErrorPolicy(DeviceErrorCategory Category,
    DeviceRecoveryStrategy RecoveryStrategy, bool Retryable, string HandlingAdvice);
