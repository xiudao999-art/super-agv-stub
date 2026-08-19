using Kunling.RobotClient.Core.Models;
using Kunling.RobotClient.Devices.Arm;
using Kunling.RobotClient.Protocols.Huayan;

// 自测：用模拟 V8 控制器（mock_v8_server.py）验证协议收发与解析。
var opt = new HuaYanV8Options
{
    Host = "127.0.0.1",
    CommandPort = 10003,
    FastPort = 10001,
    RobotId = 0,
    DataSheetPort = 10004,
};
var arm = new HuaYanArm(opt);

Console.WriteLine($"V8 命令总数: {V8Commands.All.Count}");
Console.WriteLine("---- 上电初始化序列 ----");
Dump("ConnectToBox", await arm.ConnectToBoxAsync());
Dump("Electrify", await arm.ElectrifyAsync());
Dump("ReadRobotState", await arm.ReadRobotStateAsync());
Dump("ReadActPos", await arm.ReadActPosAsync());

Console.WriteLine("---- 运动指令（MoveLTo 取料位）----");
var pick = new RobotCommand { Cmd = "PICK", TaskId = "T9", Params = new System.Text.Json.Nodes.JsonObject
{
    ["pickX"] = 0.35, ["pickY"] = -0.12, ["pickZ"] = 0.20,
    ["pickRx"] = 3.14, ["pickRy"] = 0, ["pickRz"] = 0,
    ["tcp"] = "gripper", ["ucs"] = "base",
} };
var pr = await arm.PickAsync(pick, default);
Console.WriteLine($"Pick -> {pr.Status} code={pr.Code}");

Console.WriteLine("---- 力控 / 坐标系 / 错误码透传 ----");
Dump("SetForceControlState", await arm.SetForceControlStateAsync(0, 1));
Dump("ConfigTCP", await arm.ConfigTCPAsync(0, "gripper", new Pose(0, 0, 0.12, 0, 0, 0)));
var txt = await arm.GetErrorCodeStringAsync(20018);
Console.WriteLine($"GetErrorCodeStr(20018) -> {txt}");

Console.WriteLine("---- 失败路径（故意下发未知命令）----");
var fail = await arm.RawAsync("NoSuchCommand", 1, 2);
Console.WriteLine($"NoSuchCommand -> Success={fail.Success} code={fail.ErrorCode} text={fail.ErrorText}");

Console.WriteLine("---- 订阅 Data Sheet（10004 推送）----");
var got = 0;
arm.V8.DataSheetReceived += ds =>
{
    got++;
    if (got <= 3)
        Console.WriteLine($"  [DataSheet#{got}] robotState={ds.GetDouble("robotState")} tcpX={ds.GetDouble("tcpX")}");
};
await arm.SubscribeDataSheetAsync();
await Task.Delay(500);
Console.WriteLine($"收到 Data Sheet 推送 {got} 次");

void Dump(string name, V8Reply r) =>
    Console.WriteLine($"  {name} -> {(r.Success ? "OK" : $"Fail({r.ErrorCode}:{r.ErrorText})")} values=[{r.ValuesJoined}]");

await arm.DisposeAsync();
Console.WriteLine("V8 自测完成。");
