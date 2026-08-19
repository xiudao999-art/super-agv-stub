using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;

namespace Kunling.RobotClient.Core.Controller.L1SubActions.L1SubActionTemplates;

/// <summary>GRIP_OPEN：取料前把夹爪张开到目标开口。</summary>
public sealed class L1SubActionGripOpen : PhaseActionTemplate
{
    public L1SubActionGripOpen()
    {
        PhaseId = "preOpen";
        SubAction = SubAction.GRIP_OPEN;
        Enabled = true;
        Parameters = new JsonObject
        {
            ["graspProfile"] = "DEFAULT_PICK", ["targetWidthMm"] = 80,
            ["holdMs"] = 100, ["emptyDetectedMinWidth"] = 70
        };
        Gate = false;
        OnFail = PhaseFailAction.RETRY_PHASE;
    }

    public L1SubActionGripOpen(string phaseId, GripRequest request,
        PhaseFailAction onFail = PhaseFailAction.RETRY_PHASE) : this()
    {
        PhaseId = RequireText(phaseId, nameof(phaseId));
        SetRequest(request);
        OnFail = onFail;
    }

    public void SetRequest(GripRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Parameters = new JsonObject
        {
            ["graspProfile"] = request.Profile, ["targetWidthMm"] = request.Width,
            ["holdMs"] = request.HoldMs, ["emptyDetectedMinWidth"] = request.MinDetectedWidth
        };
    }

    public static GripRequest ResolveRequest(PhaseActionTemplate phase, string? profileOverride = null)
    {
        var p = RequiredParameters(phase, SubAction.GRIP_OPEN);
        return new GripRequest(Width: Number(p, "targetWidthMm", 80),
            Profile: profileOverride ?? Text(p, "graspProfile") ?? Text(p, "releaseProfile"),
            HoldMs: Integer(p, "holdMs", 100),
            MinDetectedWidth: Number(p, "emptyDetectedMinWidth", 70));
    }

    private static JsonObject RequiredParameters(PhaseActionTemplate phase, SubAction expected)
    {
        ArgumentNullException.ThrowIfNull(phase);
        if (phase.SubAction != expected) throw new InvalidDataException($"phase {phase.PhaseId} 不是 {expected}。");
        return phase.Parameters ?? throw new InvalidDataException($"phase {phase.PhaseId} 缺少 params。");
    }
    internal static string RequireText(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"{name} 不能为空。", name);
    internal static string? Text(JsonObject p, string key) => p[key]?.GetValue<string>();
    internal static double Number(JsonObject p, string key, double fallback) => p[key]?.GetValue<double>() ?? fallback;
    internal static int Integer(JsonObject p, string key, int fallback) => p[key]?.GetValue<int>() ?? fallback;
    internal static bool Boolean(JsonObject p, string key, bool fallback) => p[key]?.GetValue<bool>() ?? fallback;
}
