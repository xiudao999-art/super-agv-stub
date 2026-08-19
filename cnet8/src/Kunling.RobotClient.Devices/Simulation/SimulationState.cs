using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Devices.Simulation;

public sealed class SimulationState(int battery)
{
    public object Sync { get; } = new();
    public SemaphoreSlim MotionLock { get; } = new(1, 1);
    public RobotPose ChassisPose { get; set; } = new(0, 0, 0, "SIM_MAP");
    public ArmPose ArmPose { get; set; } = new(0, 0, 500, 0, 0, 0);
    public Dictionary<string, bool> Doors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ChassisMoving { get; set; }
    public bool ArmMoving { get; set; }
    public bool Homed { get; set; } = true;
    public bool Gripped { get; set; }
    public double GripWidth { get; set; } = 80;
    public double GripForce { get; set; }
    public int Battery { get; set; } = battery;
}
