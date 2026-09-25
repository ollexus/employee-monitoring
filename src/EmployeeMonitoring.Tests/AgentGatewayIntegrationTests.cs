using System.Net;
using System.Net.Sockets;
using EmployeeMonitoring.Protocol;
using EmployeeMonitoring.Server.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmployeeMonitoring.Tests;

/// <summary>
/// Сквозные тесты шлюза агентов на реальном TCP-сокете:
/// несколько рабочих мест, heartbeat, снимок экрана, команды, отключение, токены.
/// </summary>
public static class AgentGatewayIntegrationTests
{
    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<AgentGateway> StartGatewayAsync(ClientRegistry registry, AgentOptions options)
    {
        var gateway = new AgentGateway(registry, options, NullLogger<AgentGateway>.Instance);
        await gateway.StartAsync(CancellationToken.None).ConfigureAwait(false);

        // Даём шлюзу время занять порт.
        for (int i = 0; i < 50; i++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync("127.0.0.1", options.Port, CancellationToken.None).ConfigureAwait(false);
                probe.Dispose();
                break;
            }
            catch (SocketException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        return gateway;
    }

    private static AgentOptions CreateOptions(int port, bool requireToken = false, string token = "") => new()
    {
        Port = port,
        HeartbeatSeconds = 5,
        CaptureIntervalSeconds = 10,
        RequireToken = requireToken,
        Token = token,
        HandshakeTimeoutSeconds = 5,
        OfflineAfterSeconds = 10
    };

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return condition();
    }

    [Test]
    public static async Task ServerCollectsDataFromSeveralWorkstations()
    {
        int port = GetFreePort();
        var registry = new ClientRegistry();
        AgentGateway gateway = await StartGatewayAsync(registry, CreateOptions(port));

        try
        {
            await using FakeAgent first = await FakeAgent.ConnectAsync(port, "PC-IVANOV", "ivanov");
            await using FakeAgent second = await FakeAgent.ConnectAsync(port, "PC-PETROV", "petrov", domain: "corp.local");
            await using FakeAgent third = await FakeAgent.ConnectAsync(port, "PC-SIDOROV", "sidorov");

            Assert.True(await WaitUntilAsync(() => registry.Snapshots().Count(s => s.IsOnline) == 3),
                "все три агента должны появиться в реестре как подключённые");

            IReadOnlyList<ClientSnapshot> snapshots = registry.Snapshots();

            Assert.Equal(3, snapshots.Count, "сервер должен видеть все рабочие места");
            Assert.Equal(3, gateway.ActiveConnections);

            ClientSnapshot ivanov = snapshots.Single(s => s.MachineName == "PC-IVANOV");
            Assert.Equal("ivanov", ivanov.UserName);
            Assert.Equal("corp", ivanov.Domain);
            Assert.Equal("127.0.0.1", ivanov.IpAddress);
            Assert.True(ivanov.IsOnline);

            ClientSnapshot petrov = snapshots.Single(s => s.MachineName == "PC-PETROV");
            Assert.Equal("corp.local", petrov.Domain, "домен должен передаваться от агента");

            // Heartbeat с активностью
            await second.SendHeartbeatAsync(new Heartbeat
            {
                ClientId = "fake-PC-PETROV",
                SentAtUtc = DateTime.UtcNow,
                IdleSeconds = 5,
                ActiveWindowTitle = "1С:Предприятие",
                ActiveProcessName = "1cv8",
                CpuLoadPercent = 33.3,
                UsedMemoryBytes = 8_000_000_000,
                TotalMemoryBytes = 32_000_000_000,
                UptimeSeconds = 3600
            });

            Assert.True(await WaitUntilAsync(() =>
            {
                ClientSnapshot snapshot = registry.Snapshots().Single(s => s.MachineName == "PC-PETROV");
                return snapshot.IdleSeconds == 5;
            }), "данные heartbeat должны сохраняться в реестре");

            ClientSnapshot withActivity = registry.Snapshots().Single(s => s.MachineName == "PC-PETROV");
            Assert.Equal("1С:Предприятие", withActivity.ActiveWindowTitle);
            Assert.Equal(33.3, withActivity.CpuLoadPercent);
            Assert.Equal(3600, withActivity.UptimeSeconds);
            Assert.NotNull(withActivity.LastSeenUtc);

            // Снимок экрана
            byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5];
            await third.SendScreenshotAsync(jpeg, 1920, 1080);

            Assert.True(await WaitUntilAsync(() => registry.Snapshots().Any(s => s.HasScreenshot)),
                "снимок экрана должен появиться в реестре");

            ClientSnapshot withShot = registry.Snapshots().Single(s => s.MachineName == "PC-SIDOROV");
            Assert.Equal(1920, withShot.ScreenshotWidth);
            Assert.Equal(1080, withShot.ScreenshotHeight);
            Assert.BytesEqual(jpeg, registry.Find(withShot.ClientId)!.GetScreenshot()!);
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Test]
    public static async Task OperatorCanRequestScreenshotOnDemand()
    {
        int port = GetFreePort();
        var registry = new ClientRegistry();
        AgentGateway gateway = await StartGatewayAsync(registry, CreateOptions(port));

        try
        {
            await using FakeAgent agent = await FakeAgent.ConnectAsync(port, "PC-CMD", "operator");
            Assert.True(await WaitUntilAsync(() => registry.Snapshots().Any(s => s.IsOnline)));

            string clientId = registry.Snapshots().Single(s => s.IsOnline).ClientId;
            ClientSession session = registry.Find(clientId)!;

            bool queued = await session.SendCommandAsync(new AgentCommand { Action = CommandActions.CaptureScreenshot });
            Assert.True(queued, "команда должна ставиться в очередь подключённого агента");

            AgentCommand command = await agent.WaitCommandAsync();
            Assert.Equal(CommandActions.CaptureScreenshot, command.Action);
            Assert.NotNull(command.Id);

            byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9];
            await agent.SendScreenshotAsync(jpeg, 1280, 720, triggeredByCommand: true);
            await agent.SendAckAsync(command);

            Assert.True(await WaitUntilAsync(() => registry.Snapshots().Any(s => s.HasScreenshot)));
            ClientSnapshot snapshot = registry.Snapshots().Single(s => s.ClientId == clientId);
            Assert.Equal(1280, snapshot.ScreenshotWidth);
            Assert.BytesEqual(jpeg, session.GetScreenshot()!);
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Test]
    public static async Task DisconnectedAgentIsMarkedOffline()
    {
        int port = GetFreePort();
        var registry = new ClientRegistry();
        AgentGateway gateway = await StartGatewayAsync(registry, CreateOptions(port));

        try
        {
            FakeAgent agent = await FakeAgent.ConnectAsync(port, "PC-LEAVE", "leaver");
            Assert.True(await WaitUntilAsync(() => registry.Snapshots().Any(s => s.IsOnline)));

            await agent.DisposeAsync();

            Assert.True(await WaitUntilAsync(() => registry.Snapshots().All(s => !s.IsOnline)),
                "после разрыва соединения агент должен считаться отключённым");
            Assert.Equal(0, gateway.ActiveConnections);

            ClientSnapshot snapshot = registry.Snapshots().Single();
            Assert.NotNull(snapshot.DisconnectedAtUtc, "время отключения должно сохраняться для истории");
            Assert.False(await snapshotIsSendable(registry, snapshot.ClientId), "команды отключённому агенту не отправляются");
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Test]
    public static async Task TokenIsValidatedOnConnect()
    {
        int port = GetFreePort();
        var registry = new ClientRegistry();
        AgentGateway gateway = await StartGatewayAsync(registry, CreateOptions(port, requireToken: true, token: "секретный-токен"));

        try
        {
            await using FakeAgent wrongToken = await FakeAgent.ConnectAsync(port, "PC-BAD", "bad", token: "неверный");
            Assert.False(wrongToken.AuthResult.Success, "неверный токен должен отклоняться");
            Assert.Contains(wrongToken.AuthResult.Message, "токен");

            await using FakeAgent correct = await FakeAgent.ConnectAsync(port, "PC-GOOD", "good", token: "секретный-токен");
            Assert.True(correct.AuthResult.Success, "верный токен должен приниматься");
            Assert.Equal(5, correct.AuthResult.HeartbeatSeconds, "сервер сообщает свои интервалы");
            Assert.Equal(10, correct.AuthResult.CaptureIntervalSeconds);
            Assert.NotNull(registry.Find($"fake-PC-GOOD"));
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Test]
    public static async Task AgentReconnectAfterServerRestartReplacesSession()
    {
        int port = GetFreePort();
        var registry = new ClientRegistry();
        AgentGateway gateway = await StartGatewayAsync(registry, CreateOptions(port));

        try
        {
            await using (FakeAgent agent = await FakeAgent.ConnectAsync(port, "PC-REPEAT", "repeat"))
            {
                Assert.True(await WaitUntilAsync(() => registry.Snapshots().Any(s => s.IsOnline)));
            }

            // Агент переподключается после обрыва: в реестре не должно быть дублей.
            await using FakeAgent again = await FakeAgent.ConnectAsync(port, "PC-REPEAT", "repeat");
            Assert.True(await WaitUntilAsync(() => registry.Snapshots().Count == 1),
                "повторное подключение не должно создавать вторую запись");
            Assert.True(await WaitUntilAsync(() => registry.Snapshots()[0].IsOnline));
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Test]
    public static async Task AgentLogMessagesAreAccepted()
    {
        int port = GetFreePort();
        var registry = new ClientRegistry();
        AgentGateway gateway = await StartGatewayAsync(registry, CreateOptions(port));

        try
        {
            await using FakeAgent agent = await FakeAgent.ConnectAsync(port, "PC-LOG", "logger");
            Assert.True(await WaitUntilAsync(() => registry.Snapshots().Any(s => s.IsOnline)));

            await agent.SendLogAsync("снимок не удалён: тест");
            await Task.Delay(200).ConfigureAwait(false);

            // Сообщение не должно приводить к обрыву соединения.
            Assert.True(registry.Snapshots().Single().IsOnline, "служебные сообщения агента не должны рвать сессию");
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<bool> snapshotIsSendable(ClientRegistry registry, string clientId)
    {
        ClientSession? session = registry.Find(clientId);
        if (session is null)
        {
            return false;
        }

        return await session.SendCommandAsync(new AgentCommand { Action = CommandActions.Ping }).ConfigureAwait(false);
    }
}
