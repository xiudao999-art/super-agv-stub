using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Config;
using Kunling.RobotClient.Core.Controller;
using Kunling.RobotClient.Core.Models;
using HuayanSdkRobot = Kunling.RobotClient.Devices.HuayanRobot.HuayanRobot;
using HuayanSdkOptions = Kunling.RobotClient.Devices.HuayanRobot.HuayanRobotOptions;

namespace Kunling.RobotClient.Devices.Arm;

/// <summary>
/// 华沿真实机械臂的统一设备门面。
/// <para>
/// 本类与 <see cref="SimulatedRobotArm"/> 实现同一个 <see cref="IArm"/>，因此上层
/// RobotController、动作模板执行器和服务器 Action 无须区分模拟设备与真实设备。
/// </para>
/// <para>
/// RobotLibrarys.dll 调用、连接生命周期、到位轮询和厂家错误码转换全部委托给
/// <see cref="HuayanSdkRobot"/>；本层只保持 Devices 项目对外稳定的 IArm 边界。
/// </para>
/// </summary>
[DeviceModel("HuayanRobotArm")]
public sealed class HuayanRobotArm : IArm, IDisposable, IAsyncDisposable
{
    // 真正执行 RobotLibrarys.dll 调用的厂家适配器。
    private readonly HuayanSdkRobot _robot;

    // true 表示 _robot 由本门面创建，Dispose 时需要负责释放；注入实例则由应用组合根释放。
    private readonly bool _ownsRobot;

    /// <summary>
    /// 供 ComponentFactory 在没有注册 SDK 实例时反射创建。
    /// 默认配置不会自动上电、复位或使能；生产环境推荐注入已经按 appsettings 配置好的实例。
    /// </summary>
    public HuayanRobotArm() : this(new HuayanSdkRobot(), ownsRobot: true) { }

    /// <summary>
    /// 注入已配置的真实适配器。调用 ComponentFactory.RegisterInstance(robot) 后，
    /// 工厂会优先选择这个可解析的构造函数。
    /// </summary>
    public HuayanRobotArm(HuayanSdkRobot robot) : this(robot, ownsRobot: false) { }

    /// <summary>
    /// 在 Devices 层完成“应用配置 → 厂家配置”的转换，并把真实 SDK 实例注册到 ComponentFactory。
    /// Program 只需要引用 Kunling.RobotClient.Devices，无须直接引用厂家项目和 RobotLibrarys.dll 类型。
    /// 返回值由应用组合根使用 await using 管理，保证程序退出时断开 SDK 会话。
    /// </summary>
    public static IAsyncDisposable CreateAndRegister(
        RobotHuayanConfig config, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var robot = new HuayanSdkRobot(new HuayanSdkOptions
        {
            Host = config.Host,
            Port = config.Port,
            BoxId = config.BoxId,
            RobotId = config.RobotId,
            Model = config.Model,
            DefaultTcp = config.DefaultTcp,
            DefaultUcs = config.DefaultUcs,
            AutoConnect = config.AutoConnect,
            ConnectToBox = config.ConnectToBox,
            Electrify = config.Electrify,
            ResetOnConnect = config.ResetOnConnect,
            EnableOnConnect = config.EnableOnConnect,
            MotionTimeoutMs = config.MotionTimeoutMs,
            PollMs = config.PollMs,
            SettleMs = config.SettleMs,
            PositionToleranceMm = config.PositionToleranceMm,
            AngleToleranceDeg = config.AngleToleranceDeg,
            Log = log
        });
        ComponentFactory.RegisterInstance(robot);
        return robot;
    }

    /// <summary>统一两个公开构造入口并明确底层实例所有权。</summary>
    private HuayanRobotArm(HuayanSdkRobot robot, bool ownsRobot)
    {
        _robot = robot ?? throw new ArgumentNullException(nameof(robot));
        _ownsRobot = ownsRobot;
    }

    /// <summary>统一设备厂商标识，沿用真实适配器。</summary>
    public string Vendor => _robot.Vendor;

    /// <summary>实际机械臂型号，由 HuayanRobotOptions.Model 配置。</summary>
    public string Model => _robot.Model;

    /// <summary>
    /// HOME 兼容入口。正常业务应由 ARM.HOME.Templates.json 展开成 MOVE_TO_POSE 后执行。
    /// </summary>
    public Task<DeviceResult<ArmActionResult>> HomeAsync(CancellationToken cancellationToken) =>
        _robot.HomeAsync(cancellationToken);

    /// <summary>
    /// 执行单个机械臂位姿子动作。底层调用 HRIF_MoveL，并等待完成标志和实际位姿到位。
    /// 模板中的位姿、坐标系、速度档位、误差和超时参数均原样传递。
    /// </summary>
    public Task<DeviceResult<ArmActionResult>> MoveToPoseAsync(
        ArmMoveRequest request, CancellationToken cancellationToken) =>
        _robot.MoveToPoseAsync(request, cancellationToken);

    /// <summary>
    /// PICK 是 L2 主动作，必须由 ARM.PICK.Templates.json 展开，避免绕过视觉、夹爪与安全阶段。
    /// </summary>
    public Task<DeviceResult<ArmActionResult>> PickAsync(
        ArmPickRequest request, CancellationToken cancellationToken) =>
        _robot.PickAsync(request, cancellationToken);

    /// <summary>PLACE 是 L2 主动作，必须由 ARM.PLACE.Templates.json 展开执行。</summary>
    public Task<DeviceResult<ArmActionResult>> PlaceAsync(
        ArmPlaceRequest request, CancellationToken cancellationToken) =>
        _robot.PlaceAsync(request, cancellationToken);

    /// <summary>读取真实控制器的连接、运动、使能、故障与实际末端位姿。</summary>
    public Task<DeviceResult<ArmStatus>> GetStatusAsync(CancellationToken cancellationToken) =>
        _robot.GetStatusAsync(cancellationToken);

    /// <summary>
    /// 仅释放由无参构造函数自行创建的底层实例；外部注入实例由应用组合根管理生命周期。
    /// </summary>
    public void Dispose()
    {
        if (_ownsRobot) _robot.Dispose();
    }

    /// <summary>异步断开并释放自有 SDK 实例；外部注入实例不会被重复释放。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_ownsRobot) await _robot.DisposeAsync().ConfigureAwait(false);
    }
}
