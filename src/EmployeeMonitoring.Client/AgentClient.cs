using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Runtime.ExceptionServices;
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
    private static readonly TimeSpan SessionTeardownTimeout = TimeSpan.FromSeconds(3);

    private readonly ClientOptions _options;
    private readonly Func<string, bool, bool> _notificationSink;
    private readonly SemaphoreSlim _captureSignal = new(0);
    private readonly Channel<OutgoingFrame> _outgoing;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _sessionCts;
    private readonly object _stateSync = new();

    private int _screenshotFailures;
    private bool _screenLocked;
    private DateTime _nextCaptureAtUtc = DateTime.MinValue;
    private int _droppedFrames;
    private long _lastIoTicks;
    private long _lastScreenshotTicks;

    public AgentClient(ClientOptions options, Func<string, bool, bool> notificationSink)
    {
        _options = options;
        _notificationSink = notificationSink;
        _outgoing = Channel.CreateBounded<OutgoingFrame>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        Volatile.Write(ref _lastIoTicks, DateTime.UtcNow.Ticks);
    }

    public event Action<string>? StatusChanged;

    public event Action? ScreenshotSent;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string StatusText { get; private set; } = "Не запущено";

    /// <summary>Время фактической отправки последнего снимка (а не только его подготовки).</summary>
    public DateTime? LastScreenshotUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastScreenshotTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public DateTime? LastHeartbeatUtc { get; private set; }

    public DateTime? ConnectedSinceUtc { get; private set; }

    public DateTime NextCaptureAtUtc => _nextCaptureAtUtc;

    public int DroppedFrames => Volatile.Read(ref _droppedFrames);

    /// <summary>true, если хотя бы один снимок действительно отправлен серверу.</summary>
    public bool HasSentScreenshot => Interlocked.Read(ref _lastScreenshotTicks) != 0;

    public void TriggerCapture()
    {
        if (_captureSignal.CurrentCount == 0)
        {
            _captureSignal.Release();
        }
    }

    /// <summary>
    /// Основной цикл: подключение, работа сессии и гарантированное восстановление связи.
    /// Внутри цикла нет ни одной точки, способной его прервать, кроме внешней отмены.
    /// </summary>
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
                SetState(ConnectionState.Disconnected, $"Нет связи с сервером ({Short(ex.Message)})");
            }

            TryCleanupSession();
            Volatile.Write(ref _lastIoTicks, 0);

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            DateTime nextAttempt = DateTime.Now.AddSeconds(delaySeconds);
            AgentLog.Info($"Повтор подключения через {delaySeconds} с (следующая попытка в {nextAttempt:HH:mm:ss})");
            SetState(ConnectionState.Disconnected, $"Нет связи с сервером, повтор в {nextAttempt:HH:mm:ss}");

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

        TryCleanupSession();
        SetState(ConnectionState.Disconnected, "Остановлено");
        AgentLog.Info("Цикл связи остановлен");
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        EnableKeepAlive(client);
        _client = client;

        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
                using CancellationTokenRegistration registration = connectCts.Token.Register(() =>
                {
                    try
                    {
                        client.Close();
                    }
                    catch (Exception)
                    {
                        // Отмена подключения.
                    }
                });

                await client.ConnectAsync(_options.ServerHost, _options.ServerPort, connectCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }

        NetworkStream stream = client.GetStream();
        _stream = stream;

        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sessionCts = sessionCts;
        CancellationToken token = sessionCts.Token;

        Volatile.Write(ref _lastIoTicks, DateTime.UtcNow.Ticks);

        await FrameChannel.WriteAsync(stream, FrameType.Auth, BuildAuthRequest(), token).ConfigureAwait(false);
        Touch();

        Frame? authFrame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, token).ConfigureAwait(false)
            ?? throw new IOException("Сервер закрыл соединение до завершения авторизации.");
        Touch();

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
        Task watchdog = WatchdogAsync(heartbeatSeconds, token);

        Task[] loops = [writer, reader, heartbeats, captures, watchdog];

        // Сессия завершается, как только завершится ЛЮБОЙ из циклов: иначе зависшее
        // чтение в сокете заблокировало бы и сторож, и переподключение.
        Task firstFinished = await Task.WhenAny(loops).ConfigureAwait(false);

        Exception? failure = null;
        try
        {
            await firstFinished.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // Сокет закрывается первым — это освобождает поток чтения, если сервер пропал.
            TryCancel(sessionCts);
            try
            {
                stream.Dispose();
            }
            catch (Exception ex)
            {
                AgentLog.Debug($"Ошибка закрытия сокета: {ex.Message}");
            }

            // Зависшие задачи не должны блокировать переподключение.
            await Task.WhenAny(Task.WhenAll(loops), Task.Delay(SessionTeardownTimeout, CancellationToken.None)).ConfigureAwait(false);
            TryCleanupSession();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task WriteLoopAsync(NetworkStream stream, CancellationToken token)
    {
        await foreach (OutgoingFrame frame in _outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            await FrameChannel.WriteRawAsync(stream, frame.Type, frame.Payload, token).ConfigureAwait(false);
            Touch();

            if (frame.OnSent is Action callback)
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    AgentLog.Debug($"Ошибка обработчика отправки: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Сторож сессии: если по сокету долго нет обмена (например, сервер «завис»),
    /// сессия принудительно завершается и цикл переподключения запускает её заново.
    /// </summary>
    private async Task WatchdogAsync(int heartbeatSeconds, CancellationToken token)
    {
        TimeSpan limit = TimeSpan.FromSeconds(Math.Clamp(heartbeatSeconds * 2 + 10, 25, 300));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            long ticks = Volatile.Read(ref _lastIoTicks);
            if (ticks == 0)
            {
                continue;
            }

            TimeSpan silence = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            if (silence > limit)
            {
                throw new TimeoutException($"Нет обмена данными с сервером более {limit.TotalSeconds:0} с");
            }
        }
    }

    private async Task HeartbeatLoopAsync(int intervalSeconds, CancellationToken token)
    {
        SendHeartbeat();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            SendHeartbeat();
        }
    }

    private void SendHeartbeat()
    {
        SystemActivity.SampleCpuLoad();
        Heartbeat heartbeat = SystemActivity.CreateHeartbeat(
            _options.ClientId,
            LastScreenshotUtc,
            _screenLocked,
            _screenshotFailures,
            State.ToString());

        if (TryEnqueue(FrameType.Heartbeat, Json.Serialize(heartbeat), null))
        {
            LastHeartbeatUtc = DateTime.UtcNow;
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

    private async Task CaptureAndSendAsync(bool byCommand, CancellationToken token)
    {
        CaptureResult capture = await Task.Run(
            () => ScreenCapture.Capture(_options.MaxScreenshotWidth, _options.JpegQuality),
            token).ConfigureAwait(false);

        _screenLocked = capture.LooksLocked;

        if (capture.LooksLocked)
        {
            // При заблокированном рабочем столе снимок бесполезен: сообщаем только метаданные.
            AgentLog.Info("Рабочий стол заблокирован — изображение не передаётся");
            TryEnqueue(FrameType.ScreenshotMeta, Json.Serialize(new ScreenshotMeta
            {
                ClientId = _options.ClientId,
                Width = capture.Width,
                Height = capture.Height,
                SizeBytes = 0,
                ScreenLocked = true,
                TriggeredByCommand = byCommand,
                WindowTitle = capture.WindowTitle
            }), null);

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

        if (!TryEnqueue(FrameType.ScreenshotMeta, Json.Serialize(meta), null))
        {
            AgentLog.Warn("Очередь отправки переполнена, метаданные снимка отброшены");
            return;
        }

        void OnSent()
        {
            _screenshotFailures = 0;
            Interlocked.Exchange(ref _lastScreenshotTicks, DateTime.UtcNow.Ticks);
            AgentLog.Info($"Снимок экрана отправлен: {meta.Width}x{meta.Height}, {meta.SizeBytes / 1024} КБ" +
                          (byCommand ? " (по команде с сервера)" : string.Empty));
            _notificationSink("Снимок экрана передан на сервер мониторинга", byCommand);
            ScreenshotSent?.Invoke();
        }

        if (!TryEnqueue(FrameType.ScreenshotData, capture.Jpeg, OnSent))
        {
            AgentLog.Warn("Очередь отправки переполнена, изображение снимка отброшено");
        }
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

            Touch();

            if (frame.Value.Type != FrameType.Command)
            {
                continue;
            }

            var command = Json.Deserialize<AgentCommand>(frame.Value.Payload);
            if (command is not null)
            {
                await HandleCommandAsync(command, token).ConfigureAwait(false);
            }
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
                    _notificationSink(string.IsNullOrWhiteSpace(command.Text) ? "Сообщение с сервера мониторинга" : command.Text!, true);
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

        TryEnqueue(FrameType.CommandAck, Json.Serialize(ack), null);
    }

    private bool TryEnqueue(FrameType type, byte[] payload, Action? onSent)
    {
        if (_outgoing.Writer.TryWrite(new OutgoingFrame(type, payload, onSent)))
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

        TryEnqueue(FrameType.Log, Json.Serialize(new AgentLogEntry { Level = level, Message = message }), null);
    }

    private AuthRequest BuildAuthRequest()
    {
        int monitors = ScreenCapture.DescribeScreen().Count;
        return new AuthRequest
        {
            ClientId = _options.ClientId,
            MachineName = Environment.MachineName,
            Domain = DomainInfo.GetDomainName(),
            UserName = Environment.UserName,
            OsDescription = Environment.OSVersion.VersionString,
            AgentVersion = AgentInfo.Version,
            MacAddress = GetPrimaryMacAddress(),
            SessionId = Process.GetCurrentProcess().SessionId,
            ScreenCount = monitors,
            StartedAtUtc = SystemActivity.GetProcessStartUtc(),
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
            // MAC-адрес не критичен.
        }

        return string.Empty;
    }

    /// <summary>Keepalive позволяет обнаружить исчезнувший сервер за десятки секунд, а не минутами.</summary>
    private static void EnableKeepAlive(TcpClient client)
    {
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception)
        {
            // На части платформ параметры могут быть недоступны — не критично.
        }
    }

    private void Touch() => Volatile.Write(ref _lastIoTicks, DateTime.UtcNow.Ticks);

    private void TryCancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Уже освобождён.
        }
        catch (Exception)
        {
            // Игнорируем.
        }
    }

    private void TryCleanupSession()
    {
        lock (_stateSync)
        {
            TryCancel(_sessionCts);
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
                _client?.Dispose();
            }
            catch (Exception)
            {
                // Игнорируем.
            }

            _client = null;
            ConnectedSinceUtc = null;
        }
    }

    private void SetState(ConnectionState state, string text)
    {
        State = state;
        StatusText = text;
        StatusChanged?.Invoke(text);
    }

    private static string Short(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "нет данных";
        }

        int index = message.IndexOf(':', StringComparison.Ordinal);
        string trimmed = index > 0 ? message[..index] : message;
        return trimmed.Length <= 60 ? trimmed : trimmed[..60];
    }

    public async ValueTask DisposeAsync()
    {
        TryCleanupSession();
        _outgoing.Writer.TryComplete();
        _captureSignal.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private readonly record struct OutgoingFrame(FrameType Type, byte[] Payload, Action? OnSent);
}

internal static class AgentInfo
{
    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
