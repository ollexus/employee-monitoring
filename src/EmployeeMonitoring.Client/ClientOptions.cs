using System.Text.Json;
using System.Text.Json.Serialization;
using EmployeeMonitoring.Protocol;

namespace EmployeeMonitoring.Client;

internal sealed class ClientOptions
{
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = ProtocolConstants.DefaultAgentPort;
    public string Token { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public int HeartbeatSeconds { get; set; } = 15;
    public int CaptureIntervalSeconds { get; set; } = 120;
    public int MaxScreenshotWidth { get; set; } = 1600;
    public int JpegQuality { get; set; } = 60;

    public bool CaptureOnStart { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ShowTrayNotifications { get; set; } = true;

    /// <summary>Минимальный интервал между уведомлениями о снимках по расписанию (0 — без ограничения).</summary>
    public int NotificationThrottleMinutes { get; set; } = 5;

    public bool SendLogMessages { get; set; } = true;

    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int ReconnectMinSeconds { get; set; } = 2;
    public int ReconnectMaxSeconds { get; set; } = 30;

    [JsonIgnore]
    public string ConfigPath { get; set; } = string.Empty;

    public static ClientOptions Load(string path)
    {
        ClientOptions options;
        if (File.Exists(path))
        {
            options = ReadOrDefault(path);
        }
        else
        {
            options = new ClientOptions();
        }

        options.ConfigPath = path;
        options.Normalize();
        return options;
    }

    /// <summary>
    /// Читает конфигурацию; при повреждённом файле агент не падает,
    /// а работает на значениях по умолчанию (журналирует проблему).
    /// </summary>
    private static ClientOptions ReadOrDefault(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClientOptions>(json, Json.CreateOptions(indented: false)) ?? new ClientOptions();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            AgentLog.Error($"Файл конфигурации {path} не удалось прочитать, применяются значения по умолчанию", ex);
            return new ClientOptions();
        }
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(ServerHost))
        {
            ServerHost = "127.0.0.1";
        }

        if (ServerPort is <= 0 or > 65535)
        {
            ServerPort = ProtocolConstants.DefaultAgentPort;
        }

        HeartbeatSeconds = Math.Clamp(HeartbeatSeconds, 5, 600);
        CaptureIntervalSeconds = Math.Clamp(CaptureIntervalSeconds, 10, 3600);
        MaxScreenshotWidth = Math.Clamp(MaxScreenshotWidth, 320, 7680);
        JpegQuality = Math.Clamp(JpegQuality, 10, 100);
        NotificationThrottleMinutes = Math.Clamp(NotificationThrottleMinutes, 0, 60);
        ConnectTimeoutSeconds = Math.Clamp(ConnectTimeoutSeconds, 3, 120);
        ReconnectMinSeconds = Math.Clamp(ReconnectMinSeconds, 1, 60);
        ReconnectMaxSeconds = Math.Clamp(ReconnectMaxSeconds, ReconnectMinSeconds, 600);
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(ConfigPath))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(this, Json.CreateOptions(indented: true));
        File.WriteAllText(ConfigPath, json);
    }

    /// <summary>
    /// Сохраняет конфигурацию; если папка назначения доступна только для чтения
    /// (например, Program Files), конфигурация переносится в профиль пользователя.
    /// </summary>
    public bool TrySave(string fallbackPath)
    {
        try
        {
            Save();
            return true;
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"Не удалось сохранить конфигурацию {ConfigPath}: {ex.Message}. Используется {fallbackPath}");

            if (string.IsNullOrEmpty(fallbackPath) ||
                string.Equals(Path.GetFullPath(fallbackPath), Path.GetFullPath(ConfigPath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            ConfigPath = fallbackPath;
            try
            {
                Save();
                return true;
            }
            catch (Exception fallbackException)
            {
                AgentLog.Error($"Не удалось сохранить конфигурацию {fallbackPath}", fallbackException);
                return false;
            }
        }
    }
}
