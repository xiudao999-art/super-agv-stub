using System.Reflection;
using Kunling.RobotClient.Core.Abstractions;

namespace Kunling.RobotClient.Core.Controller;

/// <summary>按 DeviceModel 型号反射创建设备，并为构造函数注入已注册依赖。</summary>
public static class ComponentFactory
{
    private static readonly object Sync = new();
    private static readonly HashSet<Assembly> Assemblies = [];
    private static readonly Dictionary<Type, object> Services = [];

    public static void RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (Sync) Assemblies.Add(assembly);
    }

    public static void RegisterInstance<T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (Sync) Services[typeof(T)] = instance;
    }

    public static T Resolve<T>(string model) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var contract = typeof(T);
        var implementations = GetCandidateTypes()
            .Where(t => contract.IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToArray();
        var match = implementations.FirstOrDefault(t =>
            t.GetCustomAttribute<DeviceModelAttribute>()?.Model.Equals(model, StringComparison.OrdinalIgnoreCase) == true)
            ?? implementations.FirstOrDefault(t => t.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new InvalidOperationException($"未找到 {contract.Name} 型号 '{model}'。已发现：{string.Join(", ", implementations.Select(x => x.Name))}");
        return (T)Create(match);
    }

    private static object Create(Type implementation)
    {
        foreach (var constructor in implementation.GetConstructors().OrderByDescending(x => x.GetParameters().Length))
        {
            var arguments = new List<object>();
            var resolvable = true;
            foreach (var parameter in constructor.GetParameters())
            {
                var dependency = GetService(parameter.ParameterType);
                if (dependency is null) { resolvable = false; break; }
                arguments.Add(dependency);
            }
            if (resolvable) return constructor.Invoke(arguments.ToArray());
        }
        throw new InvalidOperationException($"无法创建 {implementation.FullName}，请用 RegisterInstance 注册其构造函数依赖。");
    }

    private static object? GetService(Type type)
    {
        lock (Sync)
        {
            if (Services.TryGetValue(type, out var exact)) return exact;
            return Services.FirstOrDefault(x => type.IsAssignableFrom(x.Key)).Value;
        }
    }

    private static IEnumerable<Type> GetCandidateTypes()
    {
        Assembly[] assemblies;
        lock (Sync) assemblies = Assemblies.Count == 0 ? AppDomain.CurrentDomain.GetAssemblies() : Assemblies.ToArray();
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x is not null).Cast<Type>().ToArray(); }
            foreach (var type in types) yield return type;
        }
    }
}
