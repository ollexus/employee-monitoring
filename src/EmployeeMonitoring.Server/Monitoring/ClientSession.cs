using EmployeeMonitoring.Protocol;

namespace EmployeeMonitoring.Server.Monitoring;

/// <summary>
/// Состояние одного агента: реквизиты рабочей станции, последняя активность и последний снимок экрана.
/// Обращение к полям потокобезопасно: запись из сетевого потока, чтение из HTTP-обработчиков.
/// </summary>
public sealed class ClientSession
{
    private readonly object _sync = new();
    private AgentConnection? _connection;
    private Heartbeat? _lastHeartbeat;
    private byte[]? _screenshot;
    private DateTime? _screenshotAtUtc;
    private int _screenshotWidth;
    private int _screenshotHeight;
    private bool _screenshotLocked;
    private int _screenshotFailures;
    private DateTime? _lastSeenUtc;
    private DateTime? _lastActivityUtc;
    private DateTime? _connectedAtUtc;
    private DateTime? _disconnectedAtUtc;

    public ClientSession(AuthRequest auth, string ipAddress)
    {
        ClientId = string.IsNullOrWhiteSpace(auth.ClientId)
            ? Guid.NewGuid().ToString("N")
            : auth.ClientId;
        MachineName = auth.MachineName;
        Domain = auth.Domain;
        UserName = auth.UserName;
        IpAddress = ipAddress;
        OsDescription = auth.OsDescription;
        AgentVersion = auth.AgentVersion;
        MacAddress = auth.MacAddress;
        SessionId = auth.SessionId;
        ScreenCount = auth.ScreenCount;
        ProtocolVersion = auth.ProtocolVersion;
        AgentStartedAtUtc = Normalize(auth.StartedAtUtc);
        _lastSeenUtc = DateTime.UtcNow;
    }

    public string ClientId { get; }

    public string MachineName { get; }

    public string Domain { get; }

    public string UserName { get; }

    public string IpAddress { get; }

    public string OsDescription { get; }

    public string AgentVersion { get; }

    public string MacAddress { get; }

    public int SessionId { get; }

    public int ScreenCount { get; }

    public int ProtocolVersion { get; }

    public DateTime? AgentStartedAtUtc { get; }

    public DateTime? LastSeenUtc
    {
        get
        {
            lock (_sync)
            {
                return _lastSeenUtc;
            }
        }
    }

    public bool IsOnline
    {
        get
        {
            lock (_sync)
            {
                return _connection is not null;
            }
        }
    }

    public byte[]? GetScreenshot()
    {
        lock (_sync)
        {
            return _screenshot;
        }
    }

    public DateTime? ScreenshotAtUtc
    {
        get
        {
            lock (_sync)
            {
                return _screenshotAtUtc;
            }
        }
    }

    internal void Attach(AgentConnection connection)
    {
        lock (_sync)
        {
            AgentConnection? previous = _connection;
            _connection = connection;
            _connectedAtUtc = DateTime.UtcNow;
            _disconnectedAtUtc = null;
            _lastSeenUtc = _connectedAtUtc;
            if (previous is not null && !ReferenceEquals(previous, connection))
            {
                previous.RequestDisconnect("Агент подключился повторно");
            }
        }
    }

    /// <summary>
    /// Просит разорвать текущее соединение агента. Агент переподключится автоматически
    /// и снова появится в списке — это нужно оператору при удалении записи из панели.
    /// </summary>
    public bool Disconnect(string reason)
    {
        AgentConnection? connection;
        lock (_sync)
        {
            connection = _connection;
        }

        if (connection is null)
        {
            return false;
        }

        connection.RequestDisconnect(reason);
        return true;
    }

    internal void Detach(AgentConnection connection)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
                _disconnectedAtUtc = DateTime.UtcNow;
            }
        }
    }

    internal void Touch(Heartbeat? heartbeat)
    {
        lock (_sync)
        {
            _lastSeenUtc = DateTime.UtcNow;
            if (heartbeat is not null)
            {
                _lastHeartbeat = heartbeat;
                _screenshotFailures = heartbeat.ScreenshotFailures;
                if (heartbeat.LastScreenshotAtUtc is not null)
                {
                    _screenshotAtUtc ??= Normalize(heartbeat.LastScreenshotAtUtc.Value);
                }

                // Время последней активности сотрудника: агент сообщает момент последнего
                // ввода. Для старых агентов без этого поля берётся текущий момент,
                // а при длительном бездействии значение больше не обновляется.
                if (heartbeat.LastInputAtUtc is DateTime inputAtUtc)
                {
                    _lastActivityUtc = Normalize(inputAtUtc);
                }
                else if (heartbeat.IdleSeconds < 60 || _lastActivityUtc is null)
                {
                    _lastActivityUtc = _lastSeenUtc;
                }
            }
        }
    }

    internal void StoreScreenshot(ScreenshotMeta meta, byte[]? jpeg)
    {
        lock (_sync)
        {
            _lastSeenUtc = DateTime.UtcNow;
            _screenshotAtUtc = Normalize(meta.CapturedAtUtc) ?? DateTime.UtcNow;
            _screenshotWidth = meta.Width;
            _screenshotHeight = meta.Height;
            _screenshotLocked = meta.ScreenLocked;
            if (jpeg is not null && jpeg.Length > 0)
            {
                _screenshot = jpeg;
            }
            else
            {
                _screenshot = null;
            }
        }
    }

    internal void MarkOffline()
    {
        lock (_sync)
        {
            if (_connection is null)
            {
                _disconnectedAtUtc ??= DateTime.UtcNow;
            }
        }
    }

    public async Task<bool> SendCommandAsync(AgentCommand command, CancellationToken cancellationToken = default)
    {
        AgentConnection? connection;
        lock (_sync)
        {
            connection = _connection;
        }

        if (connection is null)
        {
            return false;
        }

        return await connection.EnqueueAsync(FrameType.Command, Json.Serialize(command), cancellationToken).ConfigureAwait(false);
    }

    public ClientSnapshot ToSnapshot()
    {
        lock (_sync)
        {
            return new ClientSnapshot
            {
                ClientId = ClientId,
                MachineName = MachineName,
                Domain = Domain,
                UserName = UserName,
                IpAddress = IpAddress,
                IsOnline = _connection is not null,
                ConnectedAtUtc = _connectedAtUtc,
                DisconnectedAtUtc = _disconnectedAtUtc,
                LastSeenUtc = _lastSeenUtc,
                LastActivityUtc = _lastActivityUtc,
                LastHeartbeatUtc = _lastHeartbeat?.SentAtUtc,
                IdleSeconds = _lastHeartbeat?.IdleSeconds ?? 0,
                ActiveWindowTitle = _lastHeartbeat?.ActiveWindowTitle ?? string.Empty,
                ActiveProcessName = _lastHeartbeat?.ActiveProcessName ?? string.Empty,
                CpuLoadPercent = _lastHeartbeat?.CpuLoadPercent ?? 0,
                UsedMemoryBytes = _lastHeartbeat?.UsedMemoryBytes ?? 0,
                TotalMemoryBytes = _lastHeartbeat?.TotalMemoryBytes ?? 0,
                UptimeSeconds = _lastHeartbeat?.UptimeSeconds ?? 0,
                AgentStatus = _lastHeartbeat?.AgentStatus ?? string.Empty,
                ScreenLocked = _lastHeartbeat?.ScreenLocked ?? false,
                ScreenshotFailures = _screenshotFailures,
                HasScreenshot = _screenshot is not null,
                ScreenshotAtUtc = _screenshotAtUtc,
                ScreenshotWidth = _screenshotWidth,
                ScreenshotHeight = _screenshotHeight,
                ScreenshotScreenLocked = _screenshotLocked,
                AgentVersion = AgentVersion,
                OsDescription = OsDescription,
                MacAddress = MacAddress,
                ScreenCount = ScreenCount,
                SessionId = SessionId,
                AgentStartedAtUtc = AgentStartedAtUtc
            };
        }
    }

    private static DateTime? Normalize(DateTime value) =>
        value == default ? null : (value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime());
}

public sealed class ClientSnapshot
{
    public string ClientId { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public DateTime? ConnectedAtUtc { get; init; }
    public DateTime? DisconnectedAtUtc { get; init; }
    public DateTime? LastSeenUtc { get; init; }
    public DateTime? LastActivityUtc { get; init; }
    public DateTime? LastHeartbeatUtc { get; init; }
    public int IdleSeconds { get; init; }
    public string ActiveWindowTitle { get; init; } = string.Empty;
    public string ActiveProcessName { get; init; } = string.Empty;
    public double CpuLoadPercent { get; init; }
    public long UsedMemoryBytes { get; init; }
    public long TotalMemoryBytes { get; init; }
    public long UptimeSeconds { get; init; }
    public string AgentStatus { get; init; } = string.Empty;
    public bool ScreenLocked { get; init; }
    public int ScreenshotFailures { get; init; }
    public bool HasScreenshot { get; init; }
    public DateTime? ScreenshotAtUtc { get; init; }
    public int ScreenshotWidth { get; init; }
    public int ScreenshotHeight { get; init; }
    public bool ScreenshotScreenLocked { get; init; }
    public string AgentVersion { get; init; } = string.Empty;
    public string OsDescription { get; init; } = string.Empty;
    public string MacAddress { get; init; } = string.Empty;
    public int ScreenCount { get; init; }
    public int SessionId { get; init; }
    public DateTime? AgentStartedAtUtc { get; init; }
}
