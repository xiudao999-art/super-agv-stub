namespace Kunling.RobotClient.Devices.HikvisionMobileRobot;

/// <summary>
/// 海康设备接入协议 V2.0 信令表。请求值通常为偶数、对应回复值为请求值加 1；
/// 注册信令从 0x0001 开始，是协议规定的例外。保留区间没有定义具体常量，可通过 SendRawAsync 直接使用。
/// </summary>
public static class HikvisionSignals
{
    public const ushort Register = 0x0001;

    // 0x0100-0x01FF：平台配置设备。
    public const ushort ConfigureStatusReport = 0x0100;
    public const ushort ConfigureGlobalAccuracy = 0x0102;
    public const ushort ConfigureAlarm = 0x0104;
    public const ushort ConfigureNtp = 0x0106;
    public const ushort ConfigureMotion = 0x0108;
    public const ushort ConfigurePlatformCapability = 0x010A;
    public const ushort ConfigureForwardLane = 0x010C;
    public const ushort EnterLowPower = 0x010E;

    // 0x0200-0x02FF：能力、位置、电池、版本和维护信息。
    public const ushort QueryBasicCapability = 0x0200;
    public const ushort ReportBasicCapability = 0x0202;
    public const ushort ConfigureCodeReport = 0x0204;
    public const ushort ReportCode = 0x0206;
    public const ushort QueryVersion = 0x0208;
    public const ushort ReportVersion = 0x020A;
    public const ushort QueryMaintenance = 0x020C;
    public const ushort ReportMaintenance = 0x020E;
    public const ushort ConfigureBatteryReport = 0x0210;
    public const ushort ReportBattery = 0x0212;
    public const ushort ReportSlamPose = 0x0214;
    public const ushort ConfigureObstacleContourReport = 0x0216;
    public const ushort ReportObstacleContour = 0x0218;
    public const ushort QueryAccessPoint = 0x021A;
    public const ushort ReportAccessPoint = 0x021C;

    // 0x0300-0x06FF：状态、运动、执行机构和复合控制。
    public const ushort ReportDeviceState = 0x0300;
    public const ushort MoveStraight = 0x0302;
    public const ushort MoveArc = 0x0304;
    public const ushort DetectRack = 0x0306;
    public const ushort ControlBattery = 0x0308;
    public const ushort PickAndMoveStraight = 0x030A;
    public const ushort PlaceAndMoveStraight = 0x030C;
    public const ushort PickAndMoveArc = 0x030E;
    public const ushort PlaceAndMoveArc = 0x0310;
    public const ushort LiftRack = 0x0312;
    public const ushort LowerRack = 0x0314;
    public const ushort SlamAutoOnline = 0x0316;
    public const ushort SwitchMap = 0x0318;
    public const ushort NotifyMapSwitchSucceeded = 0x031A;
    public const ushort NotifyMapSwitchFailed = 0x031C;
    public const ushort SwitchNavigation = 0x031E;
    public const ushort ControlRoller = 0x0320;
    public const ushort MoveComplexPath = 0x0322;
    public const ushort PickAndMoveComplexPath = 0x0324;
    public const ushort PlaceAndMoveComplexPath = 0x0326;
    public const ushort MoveThenPickComplexPath = 0x0328;
    public const ushort MoveThenPlaceComplexPath = 0x032A;
    public const ushort ChargeComplexPath = 0x032C;
    public const ushort DetectComplexPath = 0x032E;
    public const ushort SlamAutoOnlineComplexPath = 0x0330;
    public const ushort SendGlobalPath = 0x0332;

    // 0x0700-0x07FF：强制执行机构控制。
    public const ushort ForceLiftOrLower = 0x0700;

    // 0x0800-0x08FF：设备向平台申请资源。
    public const ushort RequestSpaceLock = 0x0800;
    public const ushort RequestSpaceRelease = 0x0802;
    public const ushort RequestFullChargeMaintenance = 0x0804;
    public const ushort RequestAutoOnline = 0x0806;
    public const ushort RequestPathLock = 0x080A;
    public const ushort RequestPathRelease = 0x080C;
    public const ushort RequestFollowingPath = 0x080E;
    public const ushort SendFollowingPath = 0x0810;
    public const ushort RequestDecisionPathLock = 0x0818;

    // 0x0900-0x09FF：任务异常和紧急控制。
    public const ushort PauseTask = 0x0900;
    public const ushort ContinueTask = 0x0902;
    public const ushort CancelTask = 0x0904;
    public const ushort Stop = 0x0906;
    public const ushort NotifyPlatformException = 0x0908;

    // 0x0A00-0x0AFF：人机交互。
    public const ushort DisplayPlatformMessage = 0x0A00;
    public const ushort ConfigureAlarmVolume = 0x0A02;

    // 0x7E00：辅助设备；0x7F00：升级与资源下载。
    public const ushort ReportWirelessChargerState = 0x7E00;
    public const ushort Upgrade = 0x7F00;
    public const ushort NotifyUpgradeFailed = 0x7F02;
    public const ushort DownloadResource = 0x7F04;
    public const ushort NotifyDownloadFailed = 0x7F06;
    public const ushort NotifyMapUpdate = 0x7F08;

    /// <summary>按照协议的奇偶配对规则计算请求信令对应的回复信令。</summary>
    public static ushort ResponseOf(ushort requestSignal) => checked((ushort)(requestSignal + 1));
}
