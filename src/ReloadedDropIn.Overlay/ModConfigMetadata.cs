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

    private const string DisplayNameAttributeName =
        "System.ComponentModel.DisplayNameAttribute";

    private const string DefaultValueAttributeName =
        "System.ComponentModel.DefaultValueAttribute";

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
        // Resolve against the mod folder (template assemblies, dependencies) and
        // the running runtime. Only metadata is read; nothing is executed.
        var paths = new List<string> { dllPath };
        paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(dllPath)!, "*.dll"));
        paths.AddRange(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));

        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
        var assembly = mlc.LoadFromAssemblyPath(dllPath);

        // Find the config class: inherits from Configurable<T> (the Reloaded-II
        // template pattern). The base type can live in the template assembly or
        // the mod's own copy (e.g. p5rpc.modloader ships a Template copy), so
        // match on the generic type name rather than the full namespace.
        Type? configType = null;
        foreach (var type in GetLoadableTypes(assembly))
        {
            try
            {
                var baseType = type.BaseType;
                while (baseType is not null)
                {
                    if (baseType.IsGenericType && baseType.GetGenericTypeDefinition().Name == "Configurable`1")
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

        // No instantiation: MetadataLoadContext types can't be constructed, so
        // defaults come from [DefaultValue] metadata and type defaults.
        return BuildJsonObject(configType);
    }

    private static JsonObject BuildJsonObject(Type type)
    {
        var obj = new JsonObject();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead)
                continue;

            try
            {
                var jsonKey = ReadJsonPropertyName(property) ?? property.Name;
                var propType = property.PropertyType;

                // Handle nested config objects (e.g. ConfigCommon, ConfigP5R).
                if (propType.IsClass && propType != typeof(string) && !propType.IsArray)
                {
                    var nested = BuildJsonObject(propType);
                    if (nested.Count > 0)
                        obj[jsonKey] = nested;
                }
                else
                {
                    var value = ReadDefaultValue(property) ?? GetTypeDefault(propType);
                    var node = SerializeValue(propType, value);
                    if (node is not null)
                        obj[jsonKey] = node;
                }
            }
            catch
            {
                // Skip properties that can't be read.
            }
        }

        return obj;
    }

    /// <summary>Reads [DefaultValue(...)] from a property, if present.</summary>
    private static object? ReadDefaultValue(PropertyInfo property)
    {
        foreach (var attribute in property.GetCustomAttributesData())
        {
            if (!TryGetFullName(attribute, out var fullName) || fullName != DefaultValueAttributeName)
                continue;

            if (attribute.ConstructorArguments.Count > 0)
                return attribute.ConstructorArguments[0].Value;
        }

        return null;
    }

    /// <summary>Fallback default for a config scalar when no [DefaultValue] exists.</summary>
    private static object? GetTypeDefault(Type type)
    {
        if (type == typeof(bool))
            return false;
        if (type == typeof(int))
            return 0;
        if (type == typeof(long))
            return 0L;
        if (type == typeof(float))
            return 0f;
        if (type == typeof(double))
            return 0d;
        if (type == typeof(string))
            return string.Empty;
        if (type.IsEnum)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
            return fields.FirstOrDefault()?.Name;
        }

        return null;
    }

    private static JsonNode? SerializeValue(Type propType, object? value)
    {
        if (propType == typeof(string))
            return JsonValue.Create((string)(value ?? string.Empty));
        if (propType.IsEnum)
            return JsonValue.Create(value?.ToString() ?? string.Empty);
        var validValue = value ?? GetTypeDefault(propType);
        return validValue is null ? null : JsonValue.Create(validValue);
    }

    private static IReadOnlyDictionary<string, string> ReadDisplayNamesCore(string dllPath)
    {
        // Resolve framework attribute types (DisplayAttribute, DisplayNameAttribute,
        // JsonPropertyNameAttribute) from the running runtime, plus the mod's own
        // DLLs so nested config types and template assemblies resolve. Only
        // attribute metadata is read, never executed.
        var paths = new List<string> { dllPath };
        paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(dllPath)!, "*.dll"));
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
            if (!TryGetFullName(attribute, out var fullName))
                continue;

            if (fullName == DisplayNameAttributeName)
            {
                // System.ComponentModel.DisplayNameAttribute: the name is a ctor arg.
                if (attribute.ConstructorArguments.Count > 0 &&
                    attribute.ConstructorArguments[0].Value is string constructorName &&
                    !string.IsNullOrWhiteSpace(constructorName))
                {
                    return constructorName;
                }
            }
            else if (fullName == DisplayAttributeName)
            {
                // System.ComponentModel.DataAnnotations.DisplayAttribute: Name is
                // supplied as a named argument.
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
