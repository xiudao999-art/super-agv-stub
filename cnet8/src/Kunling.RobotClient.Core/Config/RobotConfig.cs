using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kunling.RobotClient.Core.Config;

/// <summary>机器人客户端的厂商无关基础配置。</summary>
public sealed class RobotConfig
{
    [JsonPropertyName("robotId")]
    public string RobotId { get; init; } = "R01";

    [JsonPropertyName("robotType")]
    public string RobotType { get; init; } = "COMPOSITE_ROBOT";

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; init; } = "1.0.0";

    [JsonPropertyName("devices")]
    public RobotDeviceSelection Devices { get; init; } = new();

    [JsonPropertyName("server")]
    public RobotServerConfig Server { get; init; } = new();

    [JsonPropertyName("simulation")]
    public RobotSimulationConfig Simulation { get; init; } = new();

    [JsonPropertyName("chassisArrival")]
    public ChassisArrivalConfig ChassisArrival { get; init; } = new();

    /// <summary>华沿真实机械臂连接与启动参数；使用模拟机械臂时保留但不会建立连接。</summary>
    [JsonPropertyName("huayanRobot")]
    public RobotHuayanConfig HuayanRobot { get; init; } = new();

    /// <summary>海康移动机器人通信与潜伏底盘参数。</summary>
    [JsonPropertyName("hikvisionRobot")]
    public RobotHikvisionConfig HikvisionRobot { get; init; } = new();

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RobotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(RobotType);
        Server.Validate();
        Simulation.Validate();
        ChassisArrival.Validate();
        HuayanRobot.Validate();
        HikvisionRobot.Validate();
    }
}

public sealed class RobotHikvisionConfig
{
    [JsonPropertyName("host")] public string Host { get; init; } = "192.168.0.10";
    [JsonPropertyName("port")] public int Port { get; init; } = 5000;
    [JsonPropertyName("localHost")] public string LocalHost { get; init; } = "0.0.0.0";
    [JsonPropertyName("localPort")] public int LocalPort { get; init; } = 5000;
    [JsonPropertyName("transport")] public string Transport { get; init; } = "UDP";
    [JsonPropertyName("deviceId")] public uint DeviceId { get; init; } = 6001;
    [JsonPropertyName("model")] public string Model { get; init; } = "HIKROBOT_UNDERRIDE";
    [JsonPropertyName("map")] public string? Map { get; init; }
    [JsonPropertyName("requestTimeoutMs")] public int RequestTimeoutMs { get; init; } = 5_000;
    [JsonPropertyName("ackRetryIntervalMs")] public int AckRetryIntervalMs { get; init; } = 100;
    [JsonPropertyName("taskRetryIntervalMs")] public int TaskRetryIntervalMs { get; init; } = 1_000;
    [JsonPropertyName("heartbeatTimeoutMs")] public int HeartbeatTimeoutMs { get; init; } = 2_000;
    [JsonPropertyName("reconnectDelayMs")] public int ReconnectDelayMs { get; init; } = 2_000;
    [JsonPropertyName("defaultSpeed")] public double DefaultSpeed { get; init; } = 0.5;
    [JsonPropertyName("maxSpeedMmPerSecond")] public int MaxSpeedMmPerSecond { get; init; } = 1200;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(LocalHost);
        if (Port is < 1 or > 65535 || LocalPort is < 1 or > 65535) throw new InvalidDataException("海康远端端口和RCS固定监听端口必须在1-65535之间。");
        if (!Transport.Equals("UDP", StringComparison.OrdinalIgnoreCase) && !Transport.Equals("TCP", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("海康transport只能是UDP或TCP。");
        if (DeviceId == 0 || RequestTimeoutMs <= 0 || AckRetryIntervalMs <= 0 || TaskRetryIntervalMs <= 0
            || HeartbeatTimeoutMs <= 0 || ReconnectDelayMs < 0 || DefaultSpeed <= 0 || MaxSpeedMmPerSecond <= 0)
            throw new InvalidDataException("海康设备或运动参数无效。");
    }
}

/// <summary>
/// 华沿厂商配置的序列化模型。Core 只保存配置数据，不引用 RobotLibrarys.dll；
/// Program 在组合根中把它转换为 Devices.HuayanRobot 项目的 HuayanRobotOptions。
/// </summary>
public sealed class RobotHuayanConfig
{
    /// <summary>华沿控制器 IPv4/主机地址，厂家默认示例为 192.168.0.10。</summary>
    [JsonPropertyName("host")] public string Host { get; init; } = "192.168.0.10";

    /// <summary>RobotLibrarys 控制服务端口，厂家默认端口为 10003。</summary>
    [JsonPropertyName("port")] public int Port { get; init; } = 10003;

    /// <summary>电箱 ID，厂家接口允许 0-5，单电箱通常使用 0。</summary>
    [JsonPropertyName("boxId")] public int BoxId { get; init; }

    /// <summary>机器人/轴组 ID，厂家接口允许 0-5，单机械臂通常使用 0。</summary>
    [JsonPropertyName("robotId")] public int RobotId { get; init; }

    /// <summary>实际设备型号，仅用于设备标识、注册和日志，不参与 SDK 通讯。</summary>
    [JsonPropertyName("model")] public string Model { get; init; } = "HUAYAN";

    /// <summary>默认工具坐标系名称，必须与机器人示教器配置完全一致。</summary>
    [JsonPropertyName("defaultTcp")] public string DefaultTcp { get; init; } = "TCP";

    /// <summary>默认用户坐标系名称，模板使用 BASE 时会映射为该名称。</summary>
    [JsonPropertyName("defaultUcs")] public string DefaultUcs { get; init; } = "Base";

    /// <summary>执行首个真实动作时若未连接，是否自动连接控制器。</summary>
    [JsonPropertyName("autoConnect")] public bool AutoConnect { get; init; } = true;

    /// <summary>网络连接成功后是否自动连接电箱；默认关闭。</summary>
    [JsonPropertyName("connectToBox")] public bool ConnectToBox { get; init; }

    /// <summary>连接后是否自动上电。属于安全敏感操作，默认关闭。</summary>
    [JsonPropertyName("electrify")] public bool Electrify { get; init; }

    /// <summary>连接后是否自动清除轴组错误。默认关闭，避免掩盖未确认的现场故障。</summary>
    [JsonPropertyName("resetOnConnect")] public bool ResetOnConnect { get; init; }

    /// <summary>连接后是否自动使能。使能后设备具备运动条件，因此默认关闭。</summary>
    [JsonPropertyName("enableOnConnect")] public bool EnableOnConnect { get; init; }

    /// <summary>未被动作模板覆盖时的单次运动超时，单位毫秒。</summary>
    [JsonPropertyName("motionTimeoutMs")] public int MotionTimeoutMs { get; init; } = 60_000;

    /// <summary>厂家运动状态与实际位姿查询周期，单位毫秒。</summary>
    [JsonPropertyName("pollMs")] public int PollMs { get; init; } = 50;

    /// <summary>到位后保持稳定的确认时间，单位毫秒。</summary>
    [JsonPropertyName("settleMs")] public int SettleMs { get; init; } = 200;

    /// <summary>末端 XYZ 三维位置允许误差，单位毫米。</summary>
    [JsonPropertyName("positionToleranceMm")] public double PositionToleranceMm { get; init; } = 2;

    /// <summary>Rx/Ry/Rz 每个旋转轴的允许误差，单位度。</summary>
    [JsonPropertyName("angleToleranceDeg")] public double AngleToleranceDeg { get; init; } = 1;

    /// <summary>配置加载后立即校验，防止无效地址、ID或到位参数进入设备执行层。</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(DefaultTcp);
        ArgumentException.ThrowIfNullOrWhiteSpace(DefaultUcs);
        if (Port is < 1 or > 65_535) throw new InvalidDataException($"华沿控制器端口无效：{Port}");
        if (BoxId is < 0 or > 5) throw new InvalidDataException("华沿 boxId 必须在 0-5 之间。");
        if (RobotId is < 0 or > 5) throw new InvalidDataException("华沿 robotId 必须在 0-5 之间。");
        if (MotionTimeoutMs <= 0 || PollMs <= 0 || SettleMs < 0)
            throw new InvalidDataException("华沿运动时间参数无效。");
        if (PositionToleranceMm <= 0 || AngleToleranceDeg <= 0 || AngleToleranceDeg > 180)
            throw new InvalidDataException("华沿机械臂到位误差配置无效。");
    }
}

public sealed class ChassisArrivalConfig
{
    [JsonPropertyName("xyToleranceMm")] public double XyToleranceMm { get; init; } = 5;
    [JsonPropertyName("yawToleranceDeg")] public double YawToleranceDeg { get; init; } = 5;

    public void Validate()
    {
        if (XyToleranceMm <= 0 || double.IsNaN(XyToleranceMm) || double.IsInfinity(XyToleranceMm))
            throw new InvalidDataException("底盘 XY 到位误差必须是大于 0 的有效数值。");
        if (YawToleranceDeg <= 0 || YawToleranceDeg > 180 || double.IsNaN(YawToleranceDeg) || double.IsInfinity(YawToleranceDeg))
            throw new InvalidDataException("底盘角度到位误差必须在 0～180 度之间。");
    }
}

public sealed class RobotSimulationConfig
{
    [JsonPropertyName("actionDelayMs")] public int ActionDelayMs { get; init; } = 500;
    [JsonPropertyName("initialBattery")] public int InitialBattery { get; init; } = 90;
    [JsonPropertyName("failureProbability")] public double FailureProbability { get; init; }

    public void Validate()
    {
        if (ActionDelayMs < 0) throw new InvalidDataException("模拟动作延时不能小于零。");
        if (InitialBattery is < 0 or > 100) throw new InvalidDataException("模拟电量必须在 0-100 之间。");
        if (FailureProbability is < 0 or > 1) throw new InvalidDataException("模拟故障概率必须在 0-1 之间。");
    }
}

/// <summary>只选择设备型号；IP、端口等厂商参数由 Devices 项目定义。</summary>
public sealed class RobotDeviceSelection
{
    [JsonPropertyName("chassisModel")] public string ChassisModel { get; init; } = "SimulatedRobotChassis";
    [JsonPropertyName("armModel")] public string ArmModel { get; init; } = "SimulatedRobotArm";
    [JsonPropertyName("visionModel")] public string VisionModel { get; init; } = "SimulatedRobotVision";
    [JsonPropertyName("gripperModel")] public string GripperModel { get; init; } = "SimulatedRobotGripper";
    [JsonPropertyName("rfidModel")] public string RfidModel { get; init; } = "SimulatedRobotRfid";
    [JsonPropertyName("doorModel")] public string DoorModel { get; init; } = "SimulatedRobotDoor";
}

public sealed class RobotServerConfig
{
    [JsonPropertyName("host")] public string Host { get; init; } = "127.0.0.1";
    [JsonPropertyName("port")] public int Port { get; init; } = 8080;
    /// <summary>机器人向调度服务器上报状态快照的周期，默认每 3 秒一次。</summary>
    [JsonPropertyName("heartbeatMs")] public int HeartbeatMs { get; init; } = 3_000;
    [JsonPropertyName("connectTimeoutMs")] public int ConnectTimeoutMs { get; init; } = 5_000;
    [JsonPropertyName("registerTimeoutMs")] public int RegisterTimeoutMs { get; init; } = 5_000;
    [JsonPropertyName("reconnectDelayMs")] public int ReconnectDelayMs { get; init; } = 2_000;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        if (Port is < 1 or > 65_535) throw new InvalidDataException($"服务器端口无效：{Port}");
        if (HeartbeatMs <= 0 || ConnectTimeoutMs <= 0 || RegisterTimeoutMs <= 0 || ReconnectDelayMs < 0)
            throw new InvalidDataException("服务器时间参数必须为正数，重连间隔不能小于零。");
    }
}

public static class RobotConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static RobotConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到机器人配置文件。", path);

        var config = JsonSerializer.Deserialize<RobotConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("机器人配置内容为空。");
        config.Validate();
        return config;
    }

    public static async Task<RobotConfig> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到机器人配置文件。", path);

        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<RobotConfig>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("机器人配置内容为空。");
        config.Validate();
        return config;
    }
}
