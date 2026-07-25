using System.Reflection;
using System.Runtime.InteropServices;

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
