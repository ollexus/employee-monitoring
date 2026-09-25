namespace EmployeeMonitoring.Server.Monitoring;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public int Port { get; set; } = Protocol.ProtocolConstants.DefaultAgentPort;
    public int HeartbeatSeconds { get; set; } = 15;
    public int CaptureIntervalSeconds { get; set; } = 120;
    public bool RequireToken { get; set; }
    public string Token { get; set; } = string.Empty;
    public int HandshakeTimeoutSeconds { get; set; } = 15;
    public int OfflineAfterSeconds { get; set; } = 60;
}

public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    public bool RequireAuthorization { get; set; }
    public string UserName { get; set; } = "admin";
    public string Password { get; set; } = string.Empty;
    public string Title { get; set; } = "Мониторинг рабочей активности";
}
