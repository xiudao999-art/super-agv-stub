# ServerActions V2

机器人客户端与调度服务器的Action交互层。

## 协议顺序

1. TCP连接成功。
2. 客户端发送 `REGISTER`。
3. 服务器返回 `REGISTER_ACK` 和 `sessionId`。
4. 客户端周期发送 `PING`，服务器返回 `PONG`。
5. 服务器发送 `COMMAND`。
6. 客户端返回 `ACTION_EVENT: ACCEPTED`。
7. 客户端执行期间返回 `RUNNING` 和 `resolvedSteps`。
8. 设备动作结束后返回 `PHYSICAL_DONE/FAILED/UNKNOWN`。
9. 服务器完成物料账和业务校验后，自行推进 `VERIFIED/SUCCEEDED`。

协议采用一行一个UTF-8 JSON，所有发送由单一写锁串行化。

## 对外Action

- `MOVE`
- `ARM.PICK`
- `ARM.PLACE`
- `ARM.PICK_BATCH`（认证后注册）
- `ARM.PLACE_BATCH`（认证后注册）
- `ARM.HOME`
- `VISION.CAPTURE`

`PERMIT_PICK/PERMIT_PLACE/RFID_STABLE`属于服务器WAIT节点；视觉和夹爪复核属于动作包内部phase，不注册为外部Action。

## 接入现有RobotController

```csharp
var options = new ServerActionOptions
{
    Host = "127.0.0.1",
    Port = 9008,
    RobotId = "AGV-001"
};

var executor = new RobotModuleActionExecutor(robotController);
var snapshot = new DefaultRobotSnapshotProvider();
var client = new ServerActionClient(
    options,
    ServerActionCatalog.HikrobotHuayanV1(),
    executor,
    snapshot);

await client.StartAsync(stoppingToken);
```
