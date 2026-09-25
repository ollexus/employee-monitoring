using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using EmployeeMonitoring.Protocol;

namespace EmployeeMonitoring.Server.Monitoring;

/// <summary>
/// TCP-шлюз: принимает подключения агентов, выполняет авторизацию и обработку кадров.
/// Реализован на стандартной библиотеке (System.Net.Sockets), без сторонних пакетов.
/// </summary>
public sealed class AgentGateway : BackgroundService
{
    private readonly ClientRegistry _registry;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentGateway> _logger;
    private TcpListener? _listener;

    public AgentGateway(ClientRegistry registry, AgentOptions options, ILogger<AgentGateway> logger)
    {
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    public int ActiveConnections => _active;

    private int _active;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.LogCritical(
                "Не удалось занять TCP-порт {Port} для агентов: {Error}. " +
                "Освободите порт или измените параметр Agent:Port в appsettings.json.",
                _options.Port, ex.SocketErrorCode);
            throw;
        }

        _listener = listener;
        _logger.LogInformation("Шлюз агентов слушает {Address}:{Port} (интервал снимка {Interval} с)",
            GetLocalEndPoint(), _options.Port, _options.CaptureIntervalSeconds);

        using var sweeper = new PeriodicTimer(TimeSpan.FromSeconds(15));
        Task sweepTask = SweepAsync(sweeper, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), CancellationToken.None);
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Ошибка приёма подключений агентов");
        }
        finally
        {
            await sweepTask.ConfigureAwait(false);
            listener.Stop();
            _logger.LogInformation("Шлюз агентов остановлен");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Stop();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken stoppingToken)
    {
        Interlocked.Increment(ref _active);
        AgentConnection? connection = null;

        try
        {
            tcpClient.NoDelay = true;
            tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            using NetworkStream stream = tcpClient.GetStream();
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(_options.HandshakeTimeoutSeconds));

            Frame? frame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, handshakeCts.Token).ConfigureAwait(false);
            if (frame is null || frame.Value.Type != FrameType.Auth)
            {
                _logger.LogWarning("Ожидался кадр авторизации, соединение закрыто ({Peer})", Describe(tcpClient));
                return;
            }

            var auth = Json.Deserialize<AuthRequest>(frame.Value.Payload);
            if (auth is null || string.IsNullOrWhiteSpace(auth.ClientId))
            {
                await FrameChannel.WriteAsync(stream, FrameType.AuthResult,
                    new AuthResult { Success = false, Message = "Некорректный запрос авторизации" }, stoppingToken).ConfigureAwait(false);
                return;
            }

            if (_options.RequireToken && !string.Equals(auth.Token, _options.Token, StringComparison.Ordinal))
            {
                _logger.LogWarning("Отклонён агент {Machine}\\{User}: неверный токен", auth.MachineName, auth.UserName);
                await FrameChannel.WriteAsync(stream, FrameType.AuthResult,
                    new AuthResult { Success = false, Message = "Неверный токен доступа" }, stoppingToken).ConfigureAwait(false);
                return;
            }

            var session = new ClientSession(auth, DescribeIp(tcpClient));
            _registry.Register(session);
            connection = new AgentConnection(session, _options, _logger);
            session.Attach(connection);

            await FrameChannel.WriteAsync(stream, FrameType.AuthResult, new AuthResult
            {
                Success = true,
                Message = "Подключение принято",
                HeartbeatSeconds = _options.HeartbeatSeconds,
                CaptureIntervalSeconds = _options.CaptureIntervalSeconds,
                ServerTimeUtc = DateTime.UtcNow,
                ServerName = Environment.MachineName
            }, stoppingToken).ConfigureAwait(false);

            _logger.LogInformation("Агент подключён: {Domain}\\{Machine}\\{User} [{Ip}] (версия {Version})",
                string.IsNullOrEmpty(session.Domain) ? "-" : session.Domain,
                session.MachineName, session.UserName, session.IpAddress, session.AgentVersion);

            await connection.RunAsync(stream, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Соединение агента завершено: {Message}", ex.Message);
        }
        catch (OperationCanceledException)
        {
            // Шлюз останавливается.
        }
        finally
        {
            connection?.Dispose();
            try
            {
                tcpClient.Dispose();
            }
            catch (Exception)
            {
                // Игнорируем.
            }

            Interlocked.Decrement(ref _active);
        }
    }

    private async Task SweepAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (ClientSnapshot snapshot in _registry.Snapshots())
                {
                    if (snapshot.IsOnline || snapshot.LastSeenUtc is null)
                    {
                        continue;
                    }

                    TimeSpan silence = DateTime.UtcNow - snapshot.LastSeenUtc.Value;
                    if (silence > TimeSpan.FromSeconds(_options.OfflineAfterSeconds))
                    {
                        _logger.LogInformation("Агент {Machine}\\{User} помечен как отключённый (нет данных {Seconds} с)",
                            snapshot.MachineName, snapshot.UserName, (int)silence.TotalSeconds);
                        _registry.Find(snapshot.ClientId)?.MarkOffline();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ожидаемо при остановке.
        }
    }

    private string GetLocalEndPoint() =>
        _listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Address.ToString() : "0.0.0.0";

    private static string Describe(TcpClient client) =>
        client.Client.RemoteEndPoint?.ToString() ?? "неизвестный адрес";

    private static string DescribeIp(TcpClient client) =>
        client.Client.RemoteEndPoint is IPEndPoint endpoint
            ? (endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address).ToString()
            : "неизвестно";
}
