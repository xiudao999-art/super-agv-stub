using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Abstractions;

/// <summary>机器人内部事件出口，支持等待、取消和发送失败传播。</summary>
public interface IRobotEventSink
{
    ValueTask EmitAsync(RobotEvent robotEvent, CancellationToken cancellationToken = default);
}
