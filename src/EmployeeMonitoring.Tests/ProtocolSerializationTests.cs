using System.Text.Json;
using EmployeeMonitoring.Protocol;

namespace EmployeeMonitoring.Tests;

/// <summary>Тесты сериализации протокола: совместимость имён полей, значения по умолчанию, устойчивость к мусору.</summary>
public static class ProtocolSerializationTests
{
    [Test]
    public static void AuthRequestUsesCamelCaseNames()
    {
        var request = new AuthRequest
        {
            ClientId = "id-1",
            MachineName = "PC-1",
            Domain = "corp",
            UserName = "ivanov",
            MacAddress = "AA:BB:CC:DD:EE:FF",
            SessionId = 3,
            ScreenCount = 2,
            ProtocolVersion = 1,
            Token = "secret"
        };

        string json = Json.SerializeToString(request);

        Assert.Contains(json, "\"clientId\":\"id-1\"");
        Assert.Contains(json, "\"machineName\":\"PC-1\"");
        Assert.Contains(json, "\"screenCount\":2");
        Assert.Contains(json, "\"protocolVersion\":1");
    }

    [Test]
    public static void AuthRequestSurvivesRoundTrip()
    {
        var original = new AuthRequest
        {
            ClientId = "id-2",
            MachineName = "PC-2",
            Domain = "corp.local",
            UserName = "petrov",
            OsDescription = "Microsoft Windows NT 10.0",
            AgentVersion = "1.0.0",
            MacAddress = "11:22:33:44:55:66",
            SessionId = 1,
            ScreenCount = 1,
            StartedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ProtocolVersion = 1,
            Token = "t"
        };

        AuthRequest? restored = Json.Deserialize<AuthRequest>(Json.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.ClientId, restored!.ClientId);
        Assert.Equal(original.MachineName, restored.MachineName);
        Assert.Equal(original.Domain, restored.Domain);
        Assert.Equal(original.UserName, restored.UserName);
        Assert.Equal(original.MacAddress, restored.MacAddress);
        Assert.Equal(original.ScreenCount, restored.ScreenCount);
        Assert.Equal(original.StartedAtUtc, restored.StartedAtUtc);
    }

    [Test]
    public static void HeartbeatKeepsNullFieldsOutOfJson()
    {
        var heartbeat = new Heartbeat { ClientId = "id-3", IdleSeconds = 12 };
        string json = Json.SerializeToString(heartbeat);

        Assert.Contains(json, "\"idleSeconds\":12");
        Assert.False(json.Contains("lastScreenshotAtUtc"), "незаполненные поля не должны попадать в JSON");
    }

    [Test]
    public static void ScreenshotMetaKeepsUtcTimestamp()
    {
        DateTime moment = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var meta = new ScreenshotMeta { ClientId = "id-4", CapturedAtUtc = moment, Width = 1920, Height = 1080, SizeBytes = 4242 };

        ScreenshotMeta? restored = Json.Deserialize<ScreenshotMeta>(Json.Serialize(meta));

        Assert.NotNull(restored);
        Assert.Equal(moment, restored!.CapturedAtUtc.ToUniversalTime());
        Assert.Equal(1920, restored.Width);
        Assert.Equal(1080, restored.Height);
        Assert.Equal(4242, restored.SizeBytes);
    }

    [Test]
    public static void UnknownPropertiesAreIgnored()
    {
        const string json = "{\"clientId\":\"x\",\"unknownField\":123,\"nested\":{\"a\":[1,2]}}";
        AuthRequest? request = Json.Deserialize<AuthRequest>(json);

        Assert.NotNull(request);
        Assert.Equal("x", request!.ClientId);
    }

    [Test]
    public static void CaseInsensitivePropertyNames()
    {
        const string json = "{\"CLIENTID\":\"y\",\"machinename\":\"PC-9\"}";
        AuthRequest? request = Json.Deserialize<AuthRequest>(json);

        Assert.NotNull(request);
        Assert.Equal("y", request!.ClientId);
        Assert.Equal("PC-9", request.MachineName);
    }

    [Test]
    public static void AuthResultHasExpectedDefaults()
    {
        var result = new AuthResult();
        Assert.Equal(20, result.HeartbeatSeconds, "интервал heartbeat по умолчанию");
        Assert.Equal(120, result.CaptureIntervalSeconds, "интервал снимка по умолчанию");
        Assert.False(result.Success);
    }

    [Test]
    public static void CommandDefaultsToScreenshotRequest()
    {
        var command = new AgentCommand();
        Assert.Equal(CommandActions.CaptureScreenshot, command.Action);
        Assert.Equal(32, command.Id.Length, "идентификатор команды должен быть непустым");
    }

    [Test]
    public static void OptionsUseCamelCaseAndIgnoreNulls()
    {
        var options = Json.CreateOptions(indented: false);
        Assert.Equal(JsonNamingPolicy.CamelCase, options.PropertyNamingPolicy);
        Assert.True(options.PropertyNameCaseInsensitive);
        Assert.Equal(System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, options.DefaultIgnoreCondition);
    }
}
