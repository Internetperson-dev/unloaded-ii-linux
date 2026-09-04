using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReloadedDropIn.Overlay;

/// <summary>
/// Reads the friendly config-setting labels (the <c>[Display(Name = ...)]</c> attribute)
/// from a mod's compiled DLL so the overlay can show them instead of the raw JSON keys.
///
/// Uses <see cref="MetadataLoadContext"/>, which reads assembly metadata in isolation:
/// the mod's code and its dependencies are never executed or loaded into the game
/// process. Attributes are matched by full type name, so the mod's own reference
/// assemblies do not need to be resolvable. Any failure falls back silently to an empty
/// map, in which case the overlay shows the raw JSON keys as before.
/// </summary>
internal static class ModConfigMetadata
{
    private const string DisplayAttributeName =
        "System.ComponentModel.DataAnnotations.DisplayAttribute";

    private const string JsonPropertyNameAttributeName =
        "System.Text.Json.Serialization.JsonPropertyNameAttribute";

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();

    /// <summary>
    /// Returns a map of config JSON key to display name for the given mod DLL.
    /// Empty when the DLL is missing/unreadable or declares no display names.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadDisplayNames(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
            return Empty;

        try
        {
            return ReadDisplayNamesCore(dllPath);
        }
        catch
        {
            // Best-effort: the overlay falls back to raw JSON keys.
            return Empty;
        }
    }

    /// <summary>
    /// Reads a mod DLL's [Configurable] class and produces a default JsonObject
    /// with all config properties and their default values. Returns null when the
    /// DLL is missing, unreadable, or declares no [Configurable] class.
    /// </summary>
    public static JsonObject? ReadDefaultConfig(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
            return null;

        try
        {
            return ReadDefaultConfigCore(dllPath);
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? ReadDefaultConfigCore(string dllPath)
    {
        var paths = new List<string> { dllPath };
        paths.AddRange(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));

        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
        var assembly = mlc.LoadFromAssemblyPath(dllPath);

        // Find the config class: inherits from Configurable<T> (Reloaded-II template pattern).
        // The base class is typically `Reloaded.Mod.Template.Configuration.Configurable<T>`.
        Type? configType = null;
        foreach (var type in GetLoadableTypes(assembly))
        {
            try
            {
                var baseType = type.BaseType;
                while (baseType is not null)
                {
                    if (baseType.IsGenericType &&
                        baseType.GetGenericTypeDefinition().FullName?
                            .StartsWith("Reloaded.Mod.Template.Configuration.Configurable") == true)
                    {
                        configType = type;
                        break;
                    }
                    baseType = baseType.BaseType;
                }

                if (configType is not null)
                    break;
            }
            catch { }
        }

        if (configType is null)
            return null;

        // Instantiate via parameterless constructor to get defaults.
        object? instance;
        try
        {
            instance = Activator.CreateInstance(configType);
        }
        catch
        {
            return null;
        }

        if (instance is null)
            return null;

        return BuildJsonObject(configType, instance);
    }

    private static JsonObject BuildJsonObject(Type type, object instance)
    {
        var obj = new JsonObject();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead)
                continue;

            try
            {
                var value = property.GetValue(instance);
                var jsonKey = ReadJsonPropertyName(property) ?? property.Name;

                if (value is null)
                    continue;

                var propType = property.PropertyType;

                // Handle nested config objects (e.g. ConfigCommon, ConfigP5R).
                if (propType.IsClass && propType != typeof(string) && !propType.IsArray)
                {
                    var nested = BuildJsonObject(propType, value);
                    if (nested.Count > 0)
                        obj[jsonKey] = nested;
                }
                else if (propType == typeof(bool))
                {
                    obj[jsonKey] = (bool)value;
                }
                else if (propType == typeof(int))
                {
                    obj[jsonKey] = (long)(int)value;
                }
                else if (propType == typeof(long))
                {
                    obj[jsonKey] = (long)value;
                }
                else if (propType == typeof(float))
                {
                    obj[jsonKey] = (double)(float)value;
                }
                else if (propType == typeof(double))
                {
                    obj[jsonKey] = (double)value;
                }
                else if (propType == typeof(string))
                {
                    obj[jsonKey] = (string)value;
                }
                else if (propType.IsEnum)
                {
                    obj[jsonKey] = value.ToString() ?? "";
                }
            }
            catch
            {
                // Skip properties that can't be read.
            }
        }

        return obj;
    }

    private static IReadOnlyDictionary<string, string> ReadDisplayNamesCore(string dllPath)
    {
        // Resolve framework attribute types (DisplayAttribute, JsonPropertyNameAttribute)
        // from the running runtime, plus the mod DLL itself. The mod's own dependencies are
        // intentionally not required - only attribute metadata is read.
        var paths = new List<string> { dllPath };
        paths.AddRange(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));

        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
        var assembly = mlc.LoadFromAssemblyPath(dllPath);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in GetLoadableTypes(assembly))
        {
            PropertyInfo[] properties;
            try
            {
                properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                continue;
            }

            foreach (var property in properties)
            {
                string? displayName;
                try
                {
                    displayName = ReadDisplayName(property);
                }
                catch
                {
                    continue;
                }

                if (displayName is null)
                    continue;

                var jsonKey = ReadJsonPropertyName(property) ?? property.Name;
                result.TryAdd(jsonKey, displayName);
            }
        }

        return result;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types may reference assemblies we did not provide; keep the loadable ones.
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static string? ReadDisplayName(PropertyInfo property)
    {
        foreach (var attribute in property.GetCustomAttributesData())
        {
            if (!TryGetFullName(attribute, out var fullName) || fullName != DisplayAttributeName)
                continue;

            foreach (var named in attribute.NamedArguments)
            {
                if (named.MemberName == "Name" &&
                    named.TypedValue.Value is string name &&
                    !string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return null;
    }

    private static string? ReadJsonPropertyName(PropertyInfo property)
    {
        foreach (var attribute in property.GetCustomAttributesData())
        {
            if (!TryGetFullName(attribute, out var fullName) || fullName != JsonPropertyNameAttributeName)
                continue;

            if (attribute.ConstructorArguments.Count > 0 &&
                attribute.ConstructorArguments[0].Value is string name)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Safely reads an attribute's full type name. Attributes defined in assemblies we
    /// did not provide (e.g. a mod's own interface assembly behind
    /// <c>[SliderControlParams]</c>) cannot be resolved in isolation and throw; we skip
    /// them, since only the framework Display/JsonPropertyName attributes matter here.
    /// </summary>
    private static bool TryGetFullName(CustomAttributeData attribute, out string? fullName)
    {
        try
        {
            fullName = attribute.AttributeType.FullName;
            return true;
        }
        catch
        {
            fullName = null;
            return false;
        }
    }
}
