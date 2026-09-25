using System.Net.Sockets;
using System.Threading.Channels;
using EmployeeMonitoring.Protocol;

namespace EmployeeMonitoring.Tests;

/// <summary>
/// Тестовый агент: подключается к шлюзу по TCP, проходит авторизацию,
/// отправляет heartbeat и снимок экрана, принимает команды.
/// </summary>
internal sealed class FakeAgent : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<AgentCommand> _commands = Channel.CreateUnbounded<AgentCommand>();
    private readonly Channel<CommandAck> _acks = Channel.CreateUnbounded<CommandAck>();
    private readonly Task _pump;

    private FakeAgent(TcpClient client, NetworkStream stream, AuthResult authResult, string machine)
    {
        _client = client;
        _stream = stream;
        AuthResult = authResult;
        MachineName = machine;
        _pump = Task.Run(PumpAsync);
    }

    public AuthResult AuthResult { get; }

    public string MachineName { get; }

    public static async Task<FakeAgent> ConnectAsync(
        int port,
        string machine,
        string user,
        string domain = "corp",
        string token = "",
        TimeSpan? timeout = null)
    {
        var client = new TcpClient { NoDelay = true };
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);

        NetworkStream stream = client.GetStream();
        await FrameChannel.WriteAsync(stream, FrameType.Auth, new AuthRequest
        {
            ClientId = $"fake-{machine}",
            MachineName = machine,
            Domain = domain,
            UserName = user,
            OsDescription = "Microsoft Windows NT 10.0 (тест)",
            AgentVersion = "test",
            MacAddress = "02:00:00:00:00:01",
            SessionId = 1,
            ScreenCount = 1,
            StartedAtUtc = DateTime.UtcNow.AddHours(-1),
            ProtocolVersion = 1,
            Token = token
        }, cts.Token).ConfigureAwait(false);

        Frame? frame = await FrameChannel.ReadAsync(stream, ProtocolConstants.MaxPayloadBytes, cts.Token).ConfigureAwait(false)
            ?? throw new IOException("сервер закрыл соединение при авторизации");

        if (frame.Value.Type != FrameType.AuthResult)
        {
            throw new InvalidDataException($"ожидался AuthResult, получено {frame.Value.Type}");
        }

        return new FakeAgent(client, stream, Json.Deserialize<AuthResult>(frame.Value.Payload)!, machine);
    }

    public Task SendHeartbeatAsync(Heartbeat heartbeat, CancellationToken cancellationToken = default) =>
        FrameChannel.WriteAsync(_stream, FrameType.Heartbeat, heartbeat, cancellationToken);

    public async Task SendScreenshotAsync(byte[] jpeg, int width, int height, bool triggeredByCommand = false, CancellationToken cancellationToken = default)
    {
        await FrameChannel.WriteAsync(_stream, FrameType.ScreenshotMeta, new ScreenshotMeta
        {
            ClientId = $"fake-{MachineName}",
            CapturedAtUtc = DateTime.UtcNow,
            Width = width,
            Height = height,
            SizeBytes = jpeg.Length,
            TriggeredByCommand = triggeredByCommand
        }, cancellationToken).ConfigureAwait(false);

        await FrameChannel.WriteRawAsync(_stream, FrameType.ScreenshotData, jpeg, cancellationToken).ConfigureAwait(false);
    }

    public Task SendLogAsync(string message, CancellationToken cancellationToken = default) =>
        FrameChannel.WriteAsync(_stream, FrameType.Log, new AgentLogEntry { Level = "Info", Message = message }, cancellationToken);

    public Task SendAckAsync(AgentCommand command, bool success = true, CancellationToken cancellationToken = default) =>
        FrameChannel.WriteAsync(_stream, FrameType.CommandAck,
            new CommandAck { Id = command.Id, Success = success, Message = success ? "ok" : "ошибка" }, cancellationToken);

    public async Task<AgentCommand> WaitCommandAsync(TimeSpan? timeout = null) =>
        await ReadAsync(_commands, "команду от сервера", timeout).ConfigureAwait(false);

    public async Task<CommandAck> WaitAckAsync(TimeSpan? timeout = null) =>
        await ReadAsync(_acks, "подтверждение команды", timeout).ConfigureAwait(false);

    public bool IsConnected
    {
        get
        {
            try
            {
                return _client.Connected && _pump.IsCompleted == false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Frame? frame = await FrameChannel.ReadAsync(_stream, ProtocolConstants.MaxPayloadBytes, _cts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    return;
                }

                switch (frame.Value.Type)
                {
                    case FrameType.Command:
                        var command = Json.Deserialize<AgentCommand>(frame.Value.Payload);
                        if (command is not null)
                        {
                            await _commands.Writer.WriteAsync(command, _cts.Token).ConfigureAwait(false);
                        }

                        break;

                    case FrameType.CommandAck:
                        var ack = Json.Deserialize<CommandAck>(frame.Value.Payload);
                        if (ack is not null)
                        {
                            await _acks.Writer.WriteAsync(ack, _cts.Token).ConfigureAwait(false);
                        }

                        break;
                }
            }
        }
        catch (Exception)
        {
            // Соединение закрыто — тест узнает об этом по таймауту ожидания.
        }
    }

    private static async Task<T> ReadAsync<T>(Channel<T> channel, string what, TimeSpan? timeout)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            return await channel.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new AssertionException($"агент не получил {what} за отведённое время");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Игнорируем.
        }

        _commands.Writer.TryComplete();
        _acks.Writer.TryComplete();

        try
        {
            _stream.Dispose();
            _client.Dispose();
        }
        catch (Exception)
        {
            // Игнорируем.
        }

        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Игнорируем.
        }

        _cts.Dispose();
    }
}
