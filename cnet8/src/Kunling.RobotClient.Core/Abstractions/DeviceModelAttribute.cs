namespace Kunling.RobotClient.Core.Abstractions;

/// <summary>
/// 标记具体设备类的型号，供上层注册器或依赖注入配置读取。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DeviceModelAttribute(string model) : Attribute
{
    public string Model { get; } = model;
}
