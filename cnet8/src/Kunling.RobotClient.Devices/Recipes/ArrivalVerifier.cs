using System.Diagnostics;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Devices.Recipes;

internal static class ArrivalVerifier
{
    public static Task<bool> WaitChassisAsync(Func<RobotPose> feedback, RobotPose target, ArrivalCriteria criteria, CancellationToken ct) =>
        WaitAsync(() => ChassisReached(feedback(), target, criteria), criteria, ct);

    public static Task<bool> WaitArmAsync(Func<ArmPose> feedback, ArmPose target, ArrivalCriteria criteria, CancellationToken ct) =>
        WaitAsync(() => ArmReached(feedback(), target, criteria), criteria, ct);

    private static async Task<bool> WaitAsync(Func<bool> reached, ArrivalCriteria criteria, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        Stopwatch? stable = null;
        while (total.ElapsedMilliseconds < criteria.TimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            if (reached())
            {
                stable ??= Stopwatch.StartNew();
                if (stable.ElapsedMilliseconds >= criteria.SettleMs) return true;
            }
            else stable = null;
            await Task.Delay(Math.Max(10, criteria.PollMs), ct);
        }
        return false;
    }

    private static bool ChassisReached(RobotPose actual, RobotPose target, ArrivalCriteria c) =>
        Math.Abs(actual.X - target.X) <= c.PositionToleranceMm &&
        Math.Abs(actual.Y - target.Y) <= c.PositionToleranceMm &&
        ShortestAngleDistance(actual.Yaw, target.Yaw) <= c.AngleToleranceDeg;

    /// <summary>返回两个角度在圆周上的最短距离，结果范围为 0～180°。</summary>
    private static double ShortestAngleDistance(double actual, double target)
    {
        var difference = Math.Abs((actual - target) % 360d);
        return difference > 180d ? 360d - difference : difference;
    }

    private static bool ArmReached(ArmPose a, ArmPose t, ArrivalCriteria c) =>
        Math.Sqrt(Math.Pow(a.X-t.X,2)+Math.Pow(a.Y-t.Y,2)+Math.Pow(a.Z-t.Z,2)) <= c.PositionToleranceMm &&
        Math.Max(Math.Abs(a.Rx-t.Rx), Math.Max(Math.Abs(a.Ry-t.Ry), Math.Abs(a.Rz-t.Rz))) <= c.AngleToleranceDeg;

    private static double Distance(double x1, double y1, double x2, double y2) =>
        Math.Sqrt(Math.Pow(x1 - x2, 2) + Math.Pow(y1 - y2, 2));
}
