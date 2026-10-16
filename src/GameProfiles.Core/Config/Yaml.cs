using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GameProfiles.Config;

internal static class Yaml
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static T Load<T>(string path) where T : new()
    {
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new T();
        return Deserializer.Deserialize<T>(text) ?? new T();
    }

    public static T Parse<T>(string text) where T : new() =>
        string.IsNullOrWhiteSpace(text) ? new T() : Deserializer.Deserialize<T>(text) ?? new T();

    public static string Serialize(object value) => Serializer.Serialize(value);

    /// <summary>YamlDotNet yields Dictionary&lt;object, object&gt; for untyped maps; normalize to string keys recursively.</summary>
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        IDictionary<object, object?> map => map.ToDictionary(
            kv => kv.Key.ToString() ?? "", kv => Normalize(kv.Value), StringComparer.Ordinal),
        IDictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => Normalize(kv.Value), StringComparer.Ordinal),
        IList<object?> list => list.Select(Normalize).ToList(),
        _ => value,
    };

    public static Dictionary<string, object?> NormalizeMap(IDictionary<string, object?>? map) =>
        map is null ? new() : (Dictionary<string, object?>)Normalize(map)!;
}
