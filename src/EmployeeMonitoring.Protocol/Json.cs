using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmployeeMonitoring.Protocol;

public static class Json
{
    public static readonly JsonSerializerOptions Options = CreateOptions(indented: false);

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(byte[] utf8) => JsonSerializer.Deserialize<T>(utf8, Options);

    public static string SerializeToString<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = indented
    };
}
