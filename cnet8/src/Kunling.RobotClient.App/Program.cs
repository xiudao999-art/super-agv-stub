using Kunling.RobotClient.Actions.ServerActions;
using Kunling.RobotClient.Core.Abstractions;
using Kunling.RobotClient.Core.Config;
using Kunling.RobotClient.Core.Controller;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Devices.Arm;
using Kunling.RobotClient.Devices.Chassis;
using Kunling.RobotClient.Devices.Recipes;
using Kunling.RobotClient.Devices.Simulation;

var config = RobotConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"机器人 {config.RobotId}，服务器 {config.Server.Host}:{config.Server.Port}");

var simulationOptions = new SimulationOptions
{
    Logger = message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}"),
    ActionDelayMs = config.Simulation.ActionDelayMs,
    InitialBattery = config.Simulation.InitialBattery,
    FailureProbability = config.Simulation.FailureProbability
};
simulationOptions.Validate();
var simulationState = new SimulationState(simulationOptions.InitialBattery);

// 厂家 SDK 的创建与注册封装在 Devices 层，App 不直接引用 RobotLibrarys.dll 类型。
// 创建实例不会连接、上电或使能；只有 HuayanRobotArm 执行动作时才会按配置自动连接。
await using var huayanRobot = HuayanRobotArm.CreateAndRegister(config.HuayanRobot,
    message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}"));

// 创建并注册唯一海康协议客户端；只有选择HikvisionRobotChassis并执行动作时才建立网络连接。
await using var hikvisionRobot = HikvisionRobotChassis.CreateAndRegister(
    new HikvisionRobotRegistrationOptions(
        config.HikvisionRobot.Host,
        config.HikvisionRobot.Port,
        config.HikvisionRobot.LocalHost,
        config.HikvisionRobot.LocalPort,
        config.HikvisionRobot.Transport,
        config.HikvisionRobot.DeviceId,
        config.HikvisionRobot.Model,
        config.HikvisionRobot.Map,
        config.HikvisionRobot.RequestTimeoutMs,
        config.HikvisionRobot.AckRetryIntervalMs,
        config.HikvisionRobot.TaskRetryIntervalMs,
        config.HikvisionRobot.HeartbeatTimeoutMs,
        config.HikvisionRobot.ReconnectDelayMs,
        config.HikvisionRobot.DefaultSpeed,
        config.HikvisionRobot.MaxSpeedMmPerSecond),
    message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}"));
// 选择真实海康底盘时，程序启动即绑定固定端口接收 AGV 注册；模拟模式不占用现场通信端口。
if (config.Devices.ChassisModel.Equals("HikvisionRobotChassis", StringComparison.OrdinalIgnoreCase))
    await hikvisionRobot.ConnectAsync();
var actionTemplates = ActionTemplateLoader.LoadMany(
    Path.Combine(AppContext.BaseDirectory, "Configs", "ARM.PICK.Templates.json"),
    Path.Combine(AppContext.BaseDirectory, "Configs", "ARM.PLACE.Templates.json"),
    Path.Combine(AppContext.BaseDirectory, "Configs", "ARM.HOME.Templates.json"),
    Path.Combine(AppContext.BaseDirectory, "Configs", "VISION.CAPTURE.Templates.json"));

// 注册设备程序集及反射构造函数需要的共享依赖。
ComponentFactory.RegisterAssembly(typeof(SimulatedRobotChassis).Assembly);
ComponentFactory.RegisterInstance(simulationOptions);
ComponentFactory.RegisterInstance(simulationState);
ComponentFactory.RegisterInstance(config.ChassisArrival);

// 按配置型号装载具体底盘、机械臂、视觉、夹爪、RFID 和门。
var chassis = ComponentFactory.Resolve<IChassis>(config.Devices.ChassisModel);
var arm = ComponentFactory.Resolve<IArm>(config.Devices.ArmModel);
var vision = ComponentFactory.Resolve<IVision>(config.Devices.VisionModel);
var gripper = ComponentFactory.Resolve<IGripper>(config.Devices.GripperModel);
var rfid = ComponentFactory.Resolve<IRfidReader>(config.Devices.RfidModel);
var door = ComponentFactory.Resolve<IDoor>(config.Devices.DoorModel);

// RobotController 不是资源对象，不使用 using/await using。
var robot = new RobotController(config.RobotId, chassis, arm, vision, gripper, rfid, door,
    actionTemplates, message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}"));
var serverOptions = new ServerActionOptions
{
    Host = config.Server.Host,
    Port = config.Server.Port,
    RobotId = config.RobotId,
    RobotType = config.RobotType,
    ClientVersion = config.ClientVersion,
    ConnectTimeoutMs = config.Server.ConnectTimeoutMs,
    RegisterTimeoutMs = config.Server.RegisterTimeoutMs,
    DefaultHeartbeatMs = config.Server.HeartbeatMs,
    ReconnectDelayMs = config.Server.ReconnectDelayMs
};

var snapshot = new DefaultRobotSnapshotProvider();
await using var client = new ServerActionClient(serverOptions, ServerActionCatalog.HikrobotHuayanV1(),
    new RobotModuleActionExecutor(robot), snapshot);
client.LogReceived += (_, message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
client.RegistrationChanged += (_, registered) => Console.WriteLine($"注册状态：{(registered ? "已注册" : "未注册")}");

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
await client.StartAsync(shutdown.Token);
try { await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token); }
catch (OperationCanceledException) { }
await client.StopAsync();
