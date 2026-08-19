using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Devices.HuayanRobot;

/// <summary>
/// 华沿机械臂适配器配置。
/// <para>
/// 默认值仅用于开发环境创建对象；连接真实设备前应从项目配置文件覆盖 IP、坐标系、运动参数等内容。
/// 涉及上电、复位和使能的选项默认关闭，防止程序启动后机械臂未经现场确认便进入可运动状态。
/// </para>
/// </summary>
public sealed class HuayanRobotOptions
{
    /// <summary>机器人控制器 IP 地址，对应 HRIF_Connect 的 hostName。</summary>
    public string Host { get; init; } = "192.168.0.10";

    /// <summary>机器人控制服务端口；华沿手册默认值为 10003。</summary>
    public int Port { get; init; } = 10003;

    /// <summary>电箱编号，厂家 SDK 有效范围为 0～5。</summary>
    public int BoxId { get; init; }

    /// <summary>机器人轴组编号，厂家 SDK 有效范围为 0～5。</summary>
    public int RobotId { get; init; }

    /// <summary>写入设备日志和上报设备信息时使用的具体型号名称。</summary>
    public string Model { get; init; } = "HUAYAN";

    /// <summary>执行笛卡尔运动时使用的默认工具坐标系名称，必须与示教器中的 TCP 名称一致。</summary>
    public string DefaultTcp { get; init; } = "TCP";

    /// <summary>模板未指定其他坐标系时使用的用户坐标系名称，必须与示教器中的 UCS 名称一致。</summary>
    public string DefaultUcs { get; init; } = "Base";

    /// <summary>执行动作时若尚未连接，是否自动调用 ConnectAsync。</summary>
    public bool AutoConnect { get; init; } = true;

    /// <summary>连接控制器成功后是否继续调用 HRIF_Connect2Box。</summary>
    public bool ConnectToBox { get; init; }

    /// <summary>连接后是否调用 HRIF_Electrify。真实现场启用前必须完成安全评估。</summary>
    public bool Electrify { get; init; }

    /// <summary>连接后是否调用 HRIF_GrpReset 清除轴组错误。</summary>
    public bool ResetOnConnect { get; init; }

    /// <summary>连接后是否调用 HRIF_GrpEnable。使能后机械臂具备运动条件，默认禁止。</summary>
    public bool EnableOnConnect { get; init; }

    /// <summary>
    /// 可选的默认 HOME 位姿。正常业务推荐在 ARM.HOME.Templates.json 中配置，保持项目差异配置化。
    /// </summary>
    public ArmPose? HomePose { get; init; }

    /// <summary>没有从动作模板传入超时时间时使用的运动超时，单位毫秒。</summary>
    public int MotionTimeoutMs { get; init; } = 60_000;

    /// <summary>读取运动完成状态与实际位姿的轮询周期，单位毫秒。</summary>
    public int PollMs { get; init; } = 50;

    /// <summary>首次判断到位后继续等待的稳定时间，单位毫秒。</summary>
    public int SettleMs { get; init; } = 200;

    /// <summary>末端 XYZ 三维欧氏距离允许误差，单位毫米。</summary>
    public double PositionToleranceMm { get; init; } = 2;

    /// <summary>末端 Rx/Ry/Rz 各轴允许角度误差，单位度；判定时处理 0°/360° 环绕。</summary>
    public double AngleToleranceDeg { get; init; } = 1;

    /// <summary>
    /// 模板 speedProfile 到华沿 MoveL 速度、加速度和过渡半径的映射。
    /// key 不区分大小写；未知 profile 自动回退到 NORMAL。
    /// </summary>
    public IReadOnlyDictionary<string, HuayanMotionProfile> MotionProfiles { get; init; } =
        new Dictionary<string, HuayanMotionProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["SLOW"] = new(25, 50), ["NORMAL"] = new(50, 100),
            ["FAST"] = new(100, 200), ["HOME"] = new(40, 80)
        };

    /// <summary>可选设备日志回调，由上层接入统一日志窗口或日志框架。</summary>
    public Action<string>? Log { get; init; }

    /// <summary>启动连接前校验配置，尽早阻止无效参数进入厂家 SDK。</summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("Host 不能为空。", nameof(Host));
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (BoxId is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(BoxId));
        if (RobotId is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(RobotId));
        if (MotionTimeoutMs <= 0 || PollMs <= 0 || SettleMs < 0) throw new ArgumentOutOfRangeException(nameof(MotionTimeoutMs));
        if (PositionToleranceMm <= 0 || AngleToleranceDeg <= 0) throw new ArgumentOutOfRangeException(nameof(PositionToleranceMm));
        foreach (var profile in MotionProfiles.Values) profile.Validate();
    }

    /// <summary>将业务模板中的速度档位解析为厂家 MoveL 使用的数值参数。</summary>
    internal (double Velocity, double Acceleration, double Radius) ResolveMotion(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && MotionProfiles.TryGetValue(name, out var selected))
            return (selected.Velocity, selected.Acceleration, selected.Radius);
        var fallback = MotionProfiles.TryGetValue("NORMAL", out var normal) ? normal : new HuayanMotionProfile(50, 100);
        return (fallback.Velocity, fallback.Acceleration, fallback.Radius);
    }

    /// <summary>统一真实机械臂日志前缀，使服务器和本地日志能够明确识别执行设备。</summary>
    internal void WriteLog(string action, string message) => Log?.Invoke($"[DEVICE][ARM:{Model}] {action} {message}");
}

/// <summary>
/// 华沿直线运动参数：速度单位 mm/s（姿态分量为 °/s），加速度单位 mm/s²（姿态分量为 °/s²），
/// Radius 为轨迹过渡半径，单位 mm；单点精确到位通常配置为 0。
/// </summary>
public sealed record HuayanMotionProfile(double Velocity, double Acceleration, double Radius = 0)
{
    /// <summary>按照厂家 MoveL 的有效范围校验运动参数。</summary>
    internal void Validate()
    {
        if (Velocity <= 0.1) throw new ArgumentOutOfRangeException(nameof(Velocity));
        if (Acceleration <= 0.1) throw new ArgumentOutOfRangeException(nameof(Acceleration));
        if (Radius < 0) throw new ArgumentOutOfRangeException(nameof(Radius));
    }
}
