using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kunling.RobotClient.Core.Controller.Templates;

/// <summary>
/// 服务端动作配置组合目录。四类配置只保存各自职责内的参数：站点负责位姿，
/// graspProfile 负责夹爪，actionPolicy 负责重试，recipe 负责视觉。
/// </summary>
public sealed class ActionConfigurationCatalog
{
    public Dictionary<string, JsonObject> StationProfiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonObject> GraspProfiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonObject> ActionPolicies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonObject> Recipes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static ActionConfigurationCatalog Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到动作组合配置。", path);
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("动作组合配置不能为空。");
        return new()
        {
            StationProfiles = ReadMap(root, "stationProfiles", "station"),
            GraspProfiles = ReadMap(root, "graspProfiles", "name"),
            ActionPolicies = ReadMap(root, "actionPolicies", "name"),
            Recipes = ReadMap(root, "recipes", "name")
        };
    }

    private static Dictionary<string, JsonObject> ReadMap(JsonObject root, string arrayName, string keyName)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root[arrayName]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var key = item[keyName]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key) || !result.TryAdd(key, item))
                throw new InvalidDataException($"{arrayName} 中存在空名称或重复名称：{key}");
        }
        return result;
    }
}

/// <summary>
/// 将 L2 模板与项目配置组合为可以安全下发的完整 phases。
/// 合并优先级：模板 &lt; recipe &lt; graspProfile &lt; actionPolicy &lt; StationProfile &lt; 本次请求。
/// </summary>
public static class ActionConfigurationResolver
{
    public static MainActionTemplate Resolve(MainActionTemplate source, ActionConfigurationCatalog catalog,
        string station, string? graspProfile = null, string? actionPolicy = null,
        IReadOnlyDictionary<string, JsonObject>? requestPhaseOverrides = null)
    {
        var result = new MainActionTemplate
        {
            TemplateId = source.TemplateId,
            ActionType = source.ActionType,
            Phases = []
        };
        catalog.StationProfiles.TryGetValue(station, out var stationProfile);
        catalog.GraspProfiles.TryGetValue(graspProfile ?? string.Empty, out var gripProfile);
        catalog.ActionPolicies.TryGetValue(actionPolicy ?? string.Empty, out var policy);

        foreach (var phase in source.Phases)
        {
            var parameters = phase.Parameters?.DeepClone().AsObject() ?? new JsonObject();
            parameters["station"] = station;

            var recipeName = parameters["recipe"]?.GetValue<string>();
            if (recipeName is not null && catalog.Recipes.TryGetValue(recipeName, out var recipe))
                Merge(parameters, recipe["params"] as JsonObject);
            if (phase.SubAction is SubAction.GRIP_OPEN or SubAction.GRIP_CLOSE or SubAction.GRIP_VERIFY_LOAD)
            {
                Merge(parameters, gripProfile?["params"] as JsonObject);
                Merge(parameters, gripProfile?["phases"]?[phase.PhaseId] as JsonObject);
            }
            Merge(parameters, policy?["params"] as JsonObject);
            Merge(parameters, stationProfile?["common"] as JsonObject);
            Merge(parameters, stationProfile?["phases"]?[phase.PhaseId] as JsonObject);
            if (requestPhaseOverrides?.TryGetValue(phase.PhaseId, out var request) == true)
                Merge(parameters, request);

            result.Phases.Add(new PhaseActionTemplate
            {
                PhaseId = phase.PhaseId,
                SubAction = phase.SubAction,
                Enabled = phase.Enabled,
                Parameters = parameters,
                Gate = phase.Gate,
                OnFail = phase.OnFail
            });
        }
        MainActionTemplateValidator.EnsureValid(result);
        return result;
    }

    private static void Merge(JsonObject target, JsonObject? source)
    {
        if (source is null) return;
        foreach (var property in source)
            target[property.Key] = property.Value?.DeepClone();
    }
}
