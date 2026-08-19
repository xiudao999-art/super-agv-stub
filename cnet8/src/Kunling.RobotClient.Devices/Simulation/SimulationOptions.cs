namespace Kunling.RobotClient.Devices.Simulation;

public sealed class SimulationOptions
{
    public Action<string>? Logger { get; init; }
    public int ActionDelayMs { get; init; } = 500;
    public int InitialBattery { get; init; } = 90;
    public double FailureProbability { get; init; }
    public void Validate()
    {
        if (ActionDelayMs < 0) throw new ArgumentOutOfRangeException(nameof(ActionDelayMs));
        if (InitialBattery is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(InitialBattery));
        if (FailureProbability is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(FailureProbability));
    }

    internal void WriteLog(string device, string action, string detail) =>
        Logger?.Invoke($"[DEVICE][{device}] {action} {detail}");
}
