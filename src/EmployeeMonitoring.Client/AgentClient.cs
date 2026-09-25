using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using EmployeeMonitoring.Protocol;

namespace EmployeeMonitoring.Client;

internal enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected
}

internal sealed class AgentClient : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly Func<string, bool> _notificationSink;
    private readonly SemaphoreSlim _captureSignal = new(0);
    private readonly Channel<OutgoingFrame> _outgoing;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _sessionCts;

    private int _screenshotFailures;
    private bool _screenLocked;
    private DateTime _nextCaptureAtUtc = DateTime.MinValue;
    private int _droppedFrames;

    public AgentClient(ClientOptions options, Func<string, bool> notificationSink)
    {
        _options = options;
        _notificationSink = notificationSink;
        _outgoing = Channel.CreateBounded<OutgoingFrame>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public event Action<string>? StatusChanged;

    public event Action? ScreenshotSent;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string StatusText { get; private set; } = "Не запущено";

    public DateTime? LastScreenshotUtc { get; private set; }

    public DateTime? LastHeartbeatUtc { get; private set; }

    public DateTime? ConnectedSinceUtc { get; private set; }

    public DateTime NextCaptureAtUtc => _nextCaptureAtUtc;

    public int DroppedFrames => Volatile.Read(ref _droppedFrames);

    public void TriggerCapture()
    {
        if (_captureSignal.CurrentCount == 0)
        {
            _captureSignal.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int delaySeconds = _options.ReconnectMinSeconds;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetState(ConnectionState.Connecting, $"Подключение к {_options.ServerHost}:{_options.ServerPort}…");
                await RunSessionAsync(cancellationToken).ConfigureAwait(false);
                delaySeconds = _options.ReconnectMinSeconds;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"Соединение с сервером прервано: {ex.Message}");
                SetState(ConnectionState.Disconnected, $"Нет связи с сервером ({ex.Message})");
            }
            finally
            {
                CleanupSession();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            SetState(ConnectionState.Disconnected, $"Повтор подключения через {delaySeconds} с");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delaySeconds = Math.Min(delaySeconds * 2, _options.ReconnectMaxSeconds);
        }

        SetState(ConnectionState.Disconnected, "Остановлено");
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));

        var client = new TcpClient { NoDelay = true };
        _client = client;

        using (timeoutCts.Token.Register(() =>
        {
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // Игнорируем: отмена подключения.
            }
        }))
        {
            await client.ConnectAsync(_options.ServerHost, _options.ServerPort, timeoutCts.Token).ConfigureAwait(false);
        }

        NetworkStream stream = client.GetStream();
        _stream = stream;

        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sessionCts = sessionCts;
        CancellationToken token = sessionCts.Token;

        await FrameChannel.WriteAsync(stream, FrameType.Auth, BuildAuthRequest(), token).ConfigureAwait(false);

        Frame? authFrame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, token).ConfigureAwait(false)
            ?? throw new IOException("Сервер закрыл соединение до завершения авторизации.");

        if (authFrame.Value.Type != FrameType.AuthResult)
        {
            throw new InvalidDataException($"Ожидался кадр AuthResult, получено {authFrame.Value.Type}.");
        }

        var authResult = Json.Deserialize<AuthResult>(authFrame.Value.Payload)
            ?? throw new InvalidDataException("Не удалось разобрать ответ авторизации.");

        if (!authResult.Success)
        {
            throw new InvalidOperationException($"Сервер отклонил подключение: {authResult.Message}");
        }

        int heartbeatSeconds = Math.Clamp(authResult.HeartbeatSeconds, 5, 600);
        int captureSeconds = Math.Clamp(authResult.CaptureIntervalSeconds, 10, 3600);
        if (heartbeatSeconds != _options.HeartbeatSeconds || captureSeconds != _options.CaptureIntervalSeconds)
        {
            AgentLog.Info($"Сервер задал интервалы: heartbeat={heartbeatSeconds} c, снимок={captureSeconds} c");
        }

        ConnectedSinceUtc = DateTime.UtcNow;
        SetState(ConnectionState.Connected, $"Подключено к {authResult.ServerName} (с {DateTime.Now:HH:mm:ss})");
        AgentLog.Info($"Подключено к серверу {authResult.ServerName}");
        SendAgentLog("Info", $"Агент подключён к серверу {authResult.ServerName}");

        Task writer = WriteLoopAsync(stream, token);
        Task reader = ReadLoopAsync(stream, token);
        Task heartbeats = HeartbeatLoopAsync(heartbeatSeconds, token);
        Task captures = CaptureLoopAsync(captureSeconds, token);

        await Task.WhenAll(writer, reader, heartbeats, captures).ConfigureAwait(false);
    }

    private async Task WriteLoopAsync(NetworkStream stream, CancellationToken token)
    {
        await foreach (OutgoingFrame frame in _outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            await FrameChannel.WriteRawAsync(stream, frame.Type, frame.Payload, token).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatLoopAsync(int intervalSeconds, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            SystemActivity.SampleCpuLoad();
            Heartbeat heartbeat = SystemActivity.CreateHeartbeat(
                _options.ClientId,
                LastScreenshotUtc,
                _screenLocked,
                _screenshotFailures,
                State.ToString());

            if (TryEnqueue(FrameType.Heartbeat, Json.Serialize(heartbeat)))
            {
                LastHeartbeatUtc = DateTime.UtcNow;
            }
        }
    }

    private async Task CaptureLoopAsync(int intervalSeconds, CancellationToken token)
    {
        _nextCaptureAtUtc = DateTime.UtcNow.AddSeconds(intervalSeconds);
        bool firstIteration = _options.CaptureOnStart;
        bool byCommand = false;
        Task signal = _captureSignal.WaitAsync(token);

        while (!token.IsCancellationRequested)
        {
            if (firstIteration)
            {
                firstIteration = false;
                byCommand = false;
            }
            else
            {
                Task delay = Task.Delay(TimeSpan.FromSeconds(intervalSeconds), token);
                Task completed = await Task.WhenAny(delay, signal).ConfigureAwait(false);
                if (completed == signal)
                {
                    byCommand = true;
                    signal = _captureSignal.WaitAsync(token);
                }
                else
                {
                    byCommand = false;
                }
            }

            _nextCaptureAtUtc = DateTime.UtcNow.AddSeconds(intervalSeconds);
            try
            {
                await CaptureAndSendAsync(byCommand, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _screenshotFailures++;
                AgentLog.Error("Не удалось сделать снимок экрана", ex);
                SendAgentLog("Warning", $"Снимок экрана не удалён: {ex.Message}");
            }
        }
    }

    public async Task CaptureAndSendAsync(bool byCommand, CancellationToken token)
    {
        CaptureResult capture = await Task.Run(
            () => ScreenCapture.Capture(_options.MaxScreenshotWidth, _options.JpegQuality),
            token).ConfigureAwait(false);

        _screenLocked = capture.LooksLocked;

        if (capture.LooksLocked)
        {
            // При заблокированном рабочем столе снимок бесполезен: сообщаем только метаданные.
            AgentLog.Info("Рабочий стол заблокирован — изображение не передаётся");
            if (!TryEnqueue(FrameType.ScreenshotMeta, Json.Serialize(new ScreenshotMeta
            {
                ClientId = _options.ClientId,
                Width = capture.Width,
                Height = capture.Height,
                SizeBytes = 0,
                ScreenLocked = true,
                TriggeredByCommand = byCommand,
                WindowTitle = capture.WindowTitle
            })))
            {
                AgentLog.Warn("Очередь отправки переполнена, метаданные снимка отброшены");
            }

            return;
        }

        var meta = new ScreenshotMeta
        {
            ClientId = _options.ClientId,
            Width = capture.Width,
            Height = capture.Height,
            SizeBytes = capture.Jpeg.Length,
            ScreenLocked = false,
            TriggeredByCommand = byCommand,
            WindowTitle = capture.WindowTitle
        };

        if (!TryEnqueue(FrameType.ScreenshotMeta, Json.Serialize(meta)))
        {
            AgentLog.Warn("Очередь отправки переполнена, метаданные снимка отброшены");
            return;
        }

        if (!TryEnqueue(FrameType.ScreenshotData, capture.Jpeg))
        {
            AgentLog.Warn("Очередь отправки переполнена, изображение снимка отброшено");
            return;
        }

        _screenshotFailures = 0;
        LastScreenshotUtc = DateTime.UtcNow;
        AgentLog.Info($"Снимок экрана {meta.Width}x{meta.Height}, {meta.SizeBytes / 1024} КБ" +
                      (byCommand ? " (по команде с сервера)" : string.Empty));

        _notificationSink("Снимок экрана передан на сервер мониторинга");
        ScreenshotSent?.Invoke();
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Frame? frame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, token).ConfigureAwait(false);
            if (frame is null)
            {
                throw new IOException("Соединение закрыто сервером.");
            }

            if (frame.Value.Type != FrameType.Command)
            {
                continue;
            }

            var command = Json.Deserialize<AgentCommand>(frame.Value.Payload);
            if (command is null)
            {
                continue;
            }

            await HandleCommandAsync(command, token).ConfigureAwait(false);
        }
    }

    private async Task HandleCommandAsync(AgentCommand command, CancellationToken token)
    {
        CommandAck ack = new() { Id = command.Id };
        try
        {
            switch (command.Action)
            {
                case CommandActions.CaptureScreenshot:
                    TriggerCapture();
                    ack.Success = true;
                    ack.Message = "Снимок экрана запрошен";
                    AgentLog.Info("Получена команда: снимок экрана");
                    break;

                case CommandActions.Ping:
                    ack.Success = true;
                    ack.Message = "pong";
                    break;

                case CommandActions.ShowNotification:
                    _notificationSink(string.IsNullOrWhiteSpace(command.Text) ? "Сообщение с сервера мониторинга" : command.Text!);
                    ack.Success = true;
                    ack.Message = "Уведомление показано";
                    break;

                case CommandActions.Shutdown:
                    ack.Success = true;
                    ack.Message = "Агент останавливается";
                    AgentLog.Info("Получена команда остановки агента");
                    _ = Task.Delay(TimeSpan.FromSeconds(1), token).ContinueWith(_ => Environment.Exit(0), TaskScheduler.Default);
                    break;

                default:
                    ack.Success = false;
                    ack.Message = $"Неизвестная команда: {command.Action}";
                    break;
            }
        }
        catch (Exception ex)
        {
            ack.Success = false;
            ack.Message = ex.Message;
        }

        TryEnqueue(FrameType.CommandAck, Json.Serialize(ack));
    }

    private bool TryEnqueue(FrameType type, byte[] payload)
    {
        if (_outgoing.Writer.TryWrite(new OutgoingFrame(type, payload)))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedFrames);
        return false;
    }

    private void SendAgentLog(string level, string message)
    {
        if (!_options.SendLogMessages || State != ConnectionState.Connected)
        {
            return;
        }

        TryEnqueue(FrameType.Log, Json.Serialize(new AgentLogEntry { Level = level, Message = message }));
    }

    private AuthRequest BuildAuthRequest()
    {
        (int width, int height, int monitors) = ScreenCapture.DescribeScreen();
        return new AuthRequest
        {
            ClientId = _options.ClientId,
            MachineName = Environment.MachineName,
            Domain = Environment.UserDomainName,
            UserName = Environment.UserName,
            OsDescription = Environment.OSVersion.VersionString,
            AgentVersion = AgentInfo.Version,
            MacAddress = GetPrimaryMacAddress(),
            SessionId = Process.GetCurrentProcess().SessionId,
            ScreenCount = monitors,
            StartedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            ProtocolVersion = 1,
            Token = _options.Token
        };
    }

    internal static string GetPrimaryMacAddress()
    {
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    string mac = string.Join(":", adapter.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                    if (mac.Length > 0)
                    {
                        return mac;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Игнорируем: MAC-адрес не критичен.
        }

        return string.Empty;
    }

    private void CleanupSession()
    {
        try
        {
            _sessionCts?.Cancel();
        }
        catch (Exception)
        {
            // Игнорируем.
        }

        _sessionCts?.Dispose();
        _sessionCts = null;

        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // Игнорируем.
        }

        _stream = null;

        try
        {
            _client?.Close();
        }
        catch (Exception)
        {
            // Игнорируем.
        }

        _client = null;
        ConnectedSinceUtc = null;
    }

    private void SetState(ConnectionState state, string text)
    {
        State = state;
        StatusText = text;
        StatusChanged?.Invoke(text);
    }

    public async ValueTask DisposeAsync()
    {
        CleanupSession();
        _outgoing.Writer.TryComplete();
        _captureSignal.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private readonly record struct OutgoingFrame(FrameType Type, byte[] Payload);
}

internal static class AgentInfo
{
    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
