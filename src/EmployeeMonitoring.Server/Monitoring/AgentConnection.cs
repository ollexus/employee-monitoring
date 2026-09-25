using System.Net.Sockets;
using System.Threading.Channels;
using EmployeeMonitoring.Protocol;

namespace EmployeeMonitoring.Server.Monitoring;

/// <summary>Одно подключение агента: чтение входящих кадров и очередь исходящих команд.</summary>
internal sealed class AgentConnection : IDisposable
{
    private readonly ClientSession _session;
    private readonly AgentOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<OutgoingFrame> _outgoing;
    private readonly CancellationTokenSource _cts = new();
    private NetworkStream? _stream;
    private int _commandTimestampsHead;

    public AgentConnection(ClientSession session, AgentOptions options, ILogger logger)
    {
        _session = session;
        _options = options;
        _logger = logger;
        _outgoing = Channel.CreateUnbounded<OutgoingFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    private readonly long[] _commandTimestamps = new long[64];
    private long _lastIoTicks = DateTime.UtcNow.Ticks;

    public async Task RunAsync(NetworkStream stream, CancellationToken stoppingToken)
    {
        _stream = stream;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _cts.Token);
        CancellationToken token = linked.Token;

        Task writer = WriteLoopAsync(stream, token);
        Task watchdog = WatchdogAsync(token);
        ScreenshotMeta? pendingMeta = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                Frame? frame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                Touch();

                switch (frame.Value.Type)
                {
                    case FrameType.Heartbeat:
                        _session.Touch(Json.Deserialize<Heartbeat>(frame.Value.Payload));
                        break;

                    case FrameType.ScreenshotMeta:
                        pendingMeta = Json.Deserialize<ScreenshotMeta>(frame.Value.Payload);
                        if (pendingMeta is null || pendingMeta.ScreenLocked)
                        {
                            _session.StoreScreenshot(pendingMeta ?? new ScreenshotMeta(), null);
                            pendingMeta = null;
                        }

                        break;

                    case FrameType.ScreenshotData:
                        if (pendingMeta is not null)
                        {
                            _session.StoreScreenshot(pendingMeta, frame.Value.Payload);
                            _logger.LogInformation("Снимок экрана {Machine}: {Width}x{Height}, {Size} КБ",
                                _session.MachineName, pendingMeta.Width, pendingMeta.Height,
                                frame.Value.Payload.Length / 1024);
                            pendingMeta = null;
                        }
                        else
                        {
                            _logger.LogWarning("Получено изображение без метаданных от {Machine}", _session.MachineName);
                        }

                        break;

                    case FrameType.CommandAck:
                        HandleAck(frame.Value.Payload);
                        break;

                    case FrameType.Log:
                        HandleAgentLog(frame.Value.Payload);
                        break;

                    default:
                        _logger.LogDebug("Неизвестный кадр {Type} от {Machine}", frame.Value.Type, _session.MachineName);
                        break;
                }
            }
        }
        finally
        {
            // Сокет закрывается первым: это освобождает поток записи, если агент исчез.
            _outgoing.Writer.TryComplete();
            try
            {
                _cts.Cancel();
            }
            catch (Exception)
            {
                // Игнорируем.
            }

            try
            {
                stream.Dispose();
            }
            catch (Exception)
            {
                // Игнорируем.
            }

            // Ожидание ограничено по времени, чтобы «зависшая» задача не блокировала освобождение сессии.
            await Task.WhenAny(Task.WhenAll(writer, watchdog), Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None))
                .ConfigureAwait(false);

            _session.Detach(this);
        }
    }

    /// <summary>
    /// Сторож соединения: если агент перестал присылать данные, соединение закрывается,
    /// чтобы сессия считалась отключённой, а агент переподключался.
    /// </summary>
    private async Task WatchdogAsync(CancellationToken token)
    {
        TimeSpan limit = TimeSpan.FromSeconds(Math.Clamp(_options.HeartbeatSeconds * 3 + 15, 60, 900));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            long ticks = Volatile.Read(ref _lastIoTicks);
            if (ticks == 0)
            {
                continue;
            }

            if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > limit)
            {
                _logger.LogWarning("Нет данных от агента {Machine} более {Seconds} с, соединение закрывается",
                    _session.MachineName, (int)limit.TotalSeconds);
                _cts.Cancel();
                return;
            }
        }
    }

    private void Touch()
    {
        Volatile.Write(ref _lastIoTicks, DateTime.UtcNow.Ticks);
        _session.Touch(null);
    }

    private void HandleAck(byte[] payload)
    {
        var ack = Json.Deserialize<CommandAck>(payload);
        if (ack is null)
        {
            return;
        }

        if (ack.Success)
        {
            _logger.LogInformation("Агент {Machine} подтвердил команду {Id}: {Message}", _session.MachineName, ack.Id, ack.Message);
        }
        else
        {
            _logger.LogWarning("Агент {Machine} не выполнил команду {Id}: {Message}", _session.MachineName, ack.Id, ack.Message);
        }
    }

    private void HandleAgentLog(byte[] payload)
    {
        var entry = Json.Deserialize<AgentLogEntry>(payload);
        if (entry is null)
        {
            return;
        }

        _logger.LogInformation("Агент {Machine}: {Level} {Message}", _session.MachineName, entry.Level, entry.Message);
    }

    private async Task WriteLoopAsync(NetworkStream stream, CancellationToken token)
    {
        try
        {
            await foreach (OutgoingFrame frame in _outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await FrameChannel.WriteRawAsync(stream, frame.Type, frame.Payload, token).ConfigureAwait(false);
                Touch();
            }
        }
        catch (OperationCanceledException)
        {
            // Ожидаемо при закрытии.
        }
    }

    public async Task<bool> EnqueueAsync(FrameType type, byte[] payload, CancellationToken cancellationToken = default)
    {
        if (!AllowCommand())
        {
            _logger.LogWarning("Превышен лимит команд к агенту {Machine}", _session.MachineName);
            return false;
        }

        if (!_outgoing.Writer.TryWrite(new OutgoingFrame(type, payload)))
        {
            return false;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    public void RequestDisconnect(string reason)
    {
        _logger.LogInformation("Отключение агента {Machine}: {Reason}", _session.MachineName, reason);
        try
        {
            _cts.Cancel();
        }
        catch (Exception)
        {
            // Игнорируем.
        }
    }

    private bool AllowCommand()
    {
        long now = Environment.TickCount64;
        int head = _commandTimestampsHead;
        long windowStart = now - 1000;

        // Ограничение частоты команд: не более N в секунду.
        int window = Math.Min(ProtocolConstants.MaxCommandsPerSecond, _commandTimestamps.Length);
        for (int i = 0; i < window; i++)
        {
            long value = _commandTimestamps[(head + i) % _commandTimestamps.Length];
            if (value > windowStart)
            {
                return false;
            }
        }

        _commandTimestamps[head] = now;
        _commandTimestampsHead = (head + 1) % _commandTimestamps.Length;
        return true;
    }

    public void Dispose()
    {
        _outgoing.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
        _stream = null;
    }

    private readonly record struct OutgoingFrame(FrameType Type, byte[] Payload);
}
