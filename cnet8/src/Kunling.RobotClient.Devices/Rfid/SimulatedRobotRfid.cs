using Kunling.RobotClient.Core.Abstractions;using Kunling.RobotClient.Core.Models;using Kunling.RobotClient.Devices.Simulation;
namespace Kunling.RobotClient.Devices.Rfid;
[DeviceModel("SimulatedRobotRfid")]
public sealed class SimulatedRobotRfid(SimulationOptions options):IRfidReader{public async Task<DeviceResult<RfidReadResult>> ReadAsync(RfidReadRequest r,CancellationToken ct){options.WriteLog("RFID:SIM_RFID","READ",$"expectedTag={r.ExpectedTag} 开始");await Task.Delay(options.ActionDelayMs,ct);var tag=r.ExpectedTag??$"SIM-{Random.Shared.Next(100000,999999)}";options.WriteLog("RFID:SIM_RFID","READ",$"完成 tag={tag}");return DeviceResult<RfidReadResult>.Ok(new(tag,DateTimeOffset.UtcNow));}}
