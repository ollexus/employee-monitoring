namespace EmployeeMonitoring.Protocol;

public sealed class AuthRequest
{
    public string ClientId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string OsDescription { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public int SessionId { get; set; }
    public int ScreenCount { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public int ProtocolVersion { get; set; }

    /// <summary>Общий токен доступа, если сервер его требует.</summary>
    public string Token { get; set; } = string.Empty;
}

public sealed class AuthResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int HeartbeatSeconds { get; set; } = 20;
    public int CaptureIntervalSeconds { get; set; } = 120;
    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;
    public string ServerName { get; set; } = string.Empty;
}

public sealed class Heartbeat
{
    public string ClientId { get; set; } = string.Empty;
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
    public int IdleSeconds { get; set; }

    /// <summary>Момент последнего действия пользователя (ввод с клавиатуры или мыши).</summary>
    public DateTime? LastInputAtUtc { get; set; }

    public string ActiveWindowTitle { get; set; } = string.Empty;
    public string ActiveProcessName { get; set; } = string.Empty;
    public double CpuLoadPercent { get; set; }
    public long UsedMemoryBytes { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long UptimeSeconds { get; set; }
    public DateTime? LastScreenshotAtUtc { get; set; }
    public bool ScreenLocked { get; set; }
    public int ScreenshotFailures { get; set; }
    public string AgentStatus { get; set; } = string.Empty;
}

public static class CommandActions
{
    public const string CaptureScreenshot = "captureScreenshot";
    public const string Ping = "ping";
    public const string ShowNotification = "showNotification";
    public const string Shutdown = "shutdown";
}

public sealed class AgentCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Action { get; set; } = CommandActions.CaptureScreenshot;
    public string? Text { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CommandAck
{
    public string Id { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ScreenshotMeta
{
    public string ClientId { get; set; } = string.Empty;
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
    public bool ScreenLocked { get; set; }
    public bool TriggeredByCommand { get; set; }
    public string WindowTitle { get; set; } = string.Empty;
}

public sealed class AgentLogEntry
{
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}
