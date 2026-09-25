using EmployeeMonitoring.Protocol;
using EmployeeMonitoring.Server.Monitoring;

namespace EmployeeMonitoring.Tests;

/// <summary>Тесты состояния агента и реестра на стороне сервера.</summary>
public static class ServerStateTests
{
    private static AuthRequest CreateAuth(string clientId = "client-1", string machine = "PC-1", string user = "ivanov") => new()
    {
        ClientId = clientId,
        MachineName = machine,
        Domain = "corp",
        UserName = user,
        OsDescription = "Windows 11",
        AgentVersion = "1.0.0",
        MacAddress = "AA:BB:CC:DD:EE:FF",
        SessionId = 1,
        ScreenCount = 1,
        ProtocolVersion = 1,
        StartedAtUtc = DateTime.UtcNow.AddMinutes(-10)
    };

    [Test]
    public static void NewSessionIsOfflineUntilConnectionAttached()
    {
        var session = new ClientSession(CreateAuth(), "10.0.0.5");

        Assert.False(session.IsOnline, "без подключения агент не в сети");
        Assert.Equal("10.0.0.5", session.IpAddress);
        Assert.Equal("corp", session.Domain);
        Assert.Equal("ivanov", session.UserName);
        Assert.Equal("PC-1", session.MachineName);
    }

    [Test]
    public static void HeartbeatUpdatesActivityFields()
    {
        var session = new ClientSession(CreateAuth(), "10.0.0.5");
        session.Touch(new Heartbeat
        {
            ClientId = "client-1",
            IdleSeconds = 42,
            ActiveWindowTitle = "Excel — Книга1",
            ActiveProcessName = "EXCEL",
            CpuLoadPercent = 12.5,
            UsedMemoryBytes = 3_000_000_000,
            TotalMemoryBytes = 16_000_000_000,
            UptimeSeconds = 600,
            ScreenLocked = false,
            AgentStatus = "Connected"
        });

        ClientSnapshot snapshot = session.ToSnapshot();

        Assert.Equal(42, snapshot.IdleSeconds);
        Assert.Equal("Excel — Книга1", snapshot.ActiveWindowTitle);
        Assert.Equal("EXCEL", snapshot.ActiveProcessName);
        Assert.Equal(12.5, snapshot.CpuLoadPercent);
        Assert.InRange(snapshot.UsedMemoryBytes, 1, 64_000_000_000);
        Assert.Equal(600, snapshot.UptimeSeconds);
        Assert.NotNull(snapshot.LastHeartbeatUtc, "время heartbeat должно сохраняться");
    }

    [Test]
    public static void ScreenshotIsStoredAndReplaced()
    {
        var session = new ClientSession(CreateAuth(), "10.0.0.5");

        session.StoreScreenshot(new ScreenshotMeta
        {
            CapturedAtUtc = DateTime.UtcNow,
            Width = 1920,
            Height = 1080
        }, [1, 2, 3, 4]);

        ClientSnapshot first = session.ToSnapshot();
        Assert.True(first.HasScreenshot);
        Assert.Equal(1920, first.ScreenshotWidth);
        Assert.BytesEqual([1, 2, 3, 4], session.GetScreenshot()!);

        session.StoreScreenshot(new ScreenshotMeta
        {
            CapturedAtUtc = DateTime.UtcNow.AddSeconds(60),
            Width = 1280,
            Height = 720
        }, [9, 9]);

        ClientSnapshot second = session.ToSnapshot();
        Assert.Equal(1280, second.ScreenshotWidth);
        Assert.BytesEqual([9, 9], session.GetScreenshot()!);
        Assert.True(second.ScreenshotAtUtc > first.ScreenshotAtUtc, "время снимка должно обновляться");
    }

    [Test]
    public static void LockedScreenClearsImageButKeepsMetadata()
    {
        var session = new ClientSession(CreateAuth(), "10.0.0.5");
        session.StoreScreenshot(new ScreenshotMeta { Width = 800, Height = 600 }, [1, 2, 3]);
        session.StoreScreenshot(new ScreenshotMeta
        {
            Width = 800,
            Height = 600,
            ScreenLocked = true
        }, null);

        ClientSnapshot snapshot = session.ToSnapshot();
        Assert.False(snapshot.HasScreenshot, "при блокировке экрана изображение не хранится");
        Assert.True(snapshot.ScreenshotScreenLocked, "признак блокировки сохраняется");
        Assert.Null(session.GetScreenshot());
    }

    [Test]
    public static void CommandToOfflineAgentIsRejected()
    {
        var session = new ClientSession(CreateAuth(), "10.0.0.5");

        bool sent = session.SendCommandAsync(new AgentCommand { Action = CommandActions.CaptureScreenshot }).GetAwaiter().GetResult();

        Assert.False(sent, "команду нельзя отправить агенту без соединения");
    }

    [Test]
    public static void DisconnectWithoutConnectionIsReported()
    {
        var session = new ClientSession(CreateAuth(), "10.0.0.5");

        Assert.False(session.Disconnect("проверка"), "у неподключённого агента нечего разрывать");
    }

    [Test]
    public static void MissingClientIdIsReplaced()
    {
        AuthRequest auth = CreateAuth(clientId: "   ");
        var session = new ClientSession(auth, "10.0.0.5");

        Assert.NotNull(session.ClientId);
        Assert.NotEqual("   ", session.ClientId, "пустой идентификатор должен заменяться");
    }

    [Test]
    public static void DefaultStartTimeIsNormalized()
    {
        var auth = CreateAuth();
        auth.StartedAtUtc = default;
        var session = new ClientSession(auth, "10.0.0.5");

        Assert.Null(session.AgentStartedAtUtc, "нулевое время запуска не должно попадать в отчёт");
    }

    [Test]
    public static void RegistryStoresAndFindsAgents()
    {
        var registry = new ClientRegistry();
        registry.Register(new ClientSession(CreateAuth("c1", "PC-1", "ivanov"), "10.0.0.1"));
        registry.Register(new ClientSession(CreateAuth("c2", "PC-2", "petrov"), "10.0.0.2"));

        Assert.NotNull(registry.Find("c1"));
        Assert.Equal("ivanov", registry.Find("c1")!.UserName);
        Assert.Null(registry.Find("missing"), "неизвестный идентификатор не должен находиться");
    }

    [Test]
    public static void RegistryLookupIsCaseInsensitive()
    {
        var registry = new ClientRegistry();
        registry.Register(new ClientSession(CreateAuth("Client-42"), "10.0.0.1"));

        Assert.NotNull(registry.Find("client-42"), "идентификаторы должны сравниваться без учёта регистра");
    }

    [Test]
    public static void RegistrySnapshotsAreSortedByMachineName()
    {
        var registry = new ClientRegistry();
        registry.Register(new ClientSession(CreateAuth("c1", "PC-Zeta", "a"), "10.0.0.1"));
        registry.Register(new ClientSession(CreateAuth("c2", "PC-Alpha", "b"), "10.0.0.2"));
        registry.Register(new ClientSession(CreateAuth("c3", "PC-Mid", "c"), "10.0.0.3"));

        IReadOnlyList<ClientSnapshot> snapshots = registry.Snapshots();

        Assert.Equal(3, snapshots.Count);
        Assert.Equal("PC-Alpha", snapshots[0].MachineName);
        Assert.Equal("PC-Mid", snapshots[1].MachineName);
        Assert.Equal("PC-Zeta", snapshots[2].MachineName);
    }

    [Test]
    public static void RemovingAgentReturnsTrueOnlyOnce()
    {
        var registry = new ClientRegistry();
        registry.Register(new ClientSession(CreateAuth("c1"), "10.0.0.1"));

        Assert.True(registry.Remove("c1"), "первое удаление должно возвращать true");
        Assert.False(registry.Remove("c1"), "повторное удаление должно возвращать false");
        Assert.Null(registry.Find("c1"));
    }

    [Test]
    public static void RateLimiterAllowsConfiguredBurst()
    {
        var limiter = new CommandRateLimiter(limit: 5);
        int allowed = 0;

        for (int i = 0; i < 20; i++)
        {
            if (limiter.TryAcquire(1000))
            {
                allowed++;
            }
        }

        Assert.Equal(5, allowed, "допустимая пачка команд должна совпадать с лимитом");
    }

    [Test]
    public static void RateLimiterReleasesWindowAfterTime()
    {
        var limiter = new CommandRateLimiter(limit: 3, windowMilliseconds: 1000);
        Assert.True(limiter.TryAcquire(10_000));
        Assert.True(limiter.TryAcquire(10_010));
        Assert.True(limiter.TryAcquire(10_020));
        Assert.False(limiter.TryAcquire(10_030), "четвёртая команда в том же окне должна отклоняться");

        Assert.True(limiter.TryAcquire(12_000), "через секунду окно должно освободиться");
    }

    [Test]
    public static void RateLimiterDefaultsAreSane()
    {
        var limiter = new CommandRateLimiter();
        Assert.Equal(ProtocolConstants.MaxCommandsPerSecond, limiter.Limit);
        Assert.True(limiter.TryAcquire(), "первая команда всегда разрешена");
    }
}
