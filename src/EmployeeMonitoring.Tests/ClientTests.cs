using EmployeeMonitoring.Client;
using EmployeeMonitoring.Protocol;

namespace EmployeeMonitoring.Tests;

/// <summary>Тесты агента: захват экрана, сбор активности, конфигурация, автозапуск.</summary>
public static class ClientTests
{
    private static string CreateTempDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"em-tests-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Игнорируем.
        }
    }

    [Test]
    public static void CaptureProducesJpegWithinWidthLimit()
    {
        if (!Environment.UserInteractive)
        {
            SkipException.Because("нет интерактивного рабочего стола");
        }

        CaptureResult capture = ScreenCapture.Capture(maxWidth: 640, jpegQuality: 50);

        Assert.True(capture.Jpeg.Length > 100, "снимок должен содержать данные");
        Assert.Equal((byte)0xFF, capture.Jpeg[0], "JPEG начинается с FF D8");
        Assert.Equal((byte)0xD8, capture.Jpeg[1]);
        Assert.True(capture.Width <= 640, $"ширина {capture.Width} должна уменьшаться до 640");
        Assert.InRange(capture.Width, 1, 640);
        Assert.InRange(capture.Height, 1, 10000);
    }

    [Test]
    public static void CaptureWithoutScalingKeepsScreenSize()
    {
        if (!Environment.UserInteractive)
        {
            SkipException.Because("нет интерактивного рабочего стола");
        }

        (int virtualWidth, int virtualHeight, int monitors) = ScreenCapture.DescribeScreen();
        Assert.True(virtualWidth > 0, "виртуальный экран должен иметь ширину");
        Assert.True(monitors >= 1, "должен быть хотя бы один монитор");

        CaptureResult capture = ScreenCapture.Capture(maxWidth: 0, jpegQuality: 40);
        Assert.InRange(capture.Width, 1, 10000);
        Assert.False(capture.LooksLocked, "на активном рабочем столе снимок не должен считаться заблокированным");
    }

    [Test]
    public static void QualitySettingChangesPayloadSize()
    {
        if (!Environment.UserInteractive)
        {
            SkipException.Because("нет интерактивного рабочего стола");
        }

        CaptureResult low = ScreenCapture.Capture(maxWidth: 800, jpegQuality: 20);
        CaptureResult high = ScreenCapture.Capture(maxWidth: 800, jpegQuality: 95);

        Assert.True(low.Jpeg.Length < high.Jpeg.Length,
            $"JPEG качества 20 ({low.Jpeg.Length}) должен быть меньше, чем качества 95 ({high.Jpeg.Length})");
    }

    [Test]
    public static void IdleSecondsAreReported()
    {
        int idle = SystemActivity.IdleSeconds;
        Assert.InRange(idle, 0, 7 * 24 * 3600);
    }

    [Test]
    public static void ActiveWindowIsDetected()
    {
        string title = SystemActivity.ActiveWindowTitle;
        string process = SystemActivity.ActiveProcessName;

        Assert.NotNull(title, "заголовок активного окна не должен быть null");
        Assert.InRange(title.Length, 0, 1000);
        Assert.InRange(process.Length, 0, 200);
    }

    [Test]
    public static void MemoryAndUptimeAreSane()
    {
        (long used, long total) = SystemActivity.GetMemory();

        Assert.True(total > 0, "общий объём памяти должен быть известен");
        Assert.True(used >= 0, "использованная память не может быть отрицательной");
        Assert.True(used <= total, "использованная память не может превышать общий объём");
        Assert.InRange(SystemActivity.GetUptimeSeconds(), 0, 365L * 24 * 3600);
    }

    [Test]
    public static void CpuLoadIsSampled()
    {
        double first = SystemActivity.SampleCpuLoad();
        Thread.Sleep(300);
        double second = SystemActivity.SampleCpuLoad();

        Assert.InRange((long)first, 0, 100);
        Assert.InRange((long)second, 0, 100);
    }

    [Test]
    public static void HeartbeatContainsAllRequiredFields()
    {
        Heartbeat heartbeat = SystemActivity.CreateHeartbeat(
            "client-1", DateTime.UtcNow.AddMinutes(-1), false, 0, "Connected");

        Assert.Equal("client-1", heartbeat.ClientId);
        Assert.True(heartbeat.IdleSeconds >= 0);
        Assert.InRange(heartbeat.UsedMemoryBytes, 0, long.MaxValue);
        Assert.True(heartbeat.TotalMemoryBytes > 0);
        Assert.Equal("Connected", heartbeat.AgentStatus);
        Assert.False(heartbeat.ScreenLocked);
        Assert.NotNull(heartbeat.LastScreenshotAtUtc);
        Assert.True(Math.Abs(heartbeat.CpuLoadPercent) <= 100);
    }

    [Test]
    public static void ProcessStartTimeIsStable()
    {
        DateTime first = SystemActivity.GetProcessStartUtc();
        DateTime second = SystemActivity.GetProcessStartUtc();

        Assert.Equal(first, second, "время запуска процесса должно кэшироваться");
        Assert.True(first <= DateTime.UtcNow, "время запуска не может быть в будущем");
    }

    [Test]
    public static void OptionsAreCreatedWithSaneDefaults()
    {
        string directory = CreateTempDirectory("options");
        try
        {
            string path = Path.Combine(directory, "appsettings.json");
            ClientOptions options = ClientOptions.Load(path);

            Assert.Equal("127.0.0.1", options.ServerHost);
            Assert.Equal(ProtocolConstants.DefaultAgentPort, options.ServerPort);
            Assert.True(options.HeartbeatSeconds >= 5);
            Assert.InRange(options.CaptureIntervalSeconds, 10, 3600);
            Assert.Equal(path, options.ConfigPath);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Test]
    public static void OptionsSurviveSaveAndLoad()
    {
        string directory = CreateTempDirectory("options-save");
        try
        {
            string path = Path.Combine(directory, "appsettings.json");
            var original = new ClientOptions
            {
                ServerHost = "monitoring.corp.local",
                ServerPort = 15000,
                Token = "токен",
                ClientId = "abc-123",
                CaptureIntervalSeconds = 45,
                MaxScreenshotWidth = 1920,
                JpegQuality = 75,
                StartWithWindows = false
            };
            original.ConfigPath = path;
            original.Save();

            ClientOptions loaded = ClientOptions.Load(path);

            Assert.Equal("monitoring.corp.local", loaded.ServerHost);
            Assert.Equal(15000, loaded.ServerPort);
            Assert.Equal("токен", loaded.Token);
            Assert.Equal("abc-123", loaded.ClientId);
            Assert.Equal(45, loaded.CaptureIntervalSeconds);
            Assert.Equal(1920, loaded.MaxScreenshotWidth);
            Assert.Equal(75, loaded.JpegQuality);
            Assert.False(loaded.StartWithWindows);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Test]
    public static void InvalidValuesAreClampedToSafeRange()
    {
        var options = new ClientOptions
        {
            ServerHost = "  ",
            ServerPort = 70000,
            HeartbeatSeconds = 1,
            CaptureIntervalSeconds = 1,
            MaxScreenshotWidth = 10,
            JpegQuality = 500,
            ConnectTimeoutSeconds = 0,
            ReconnectMinSeconds = 100,
            ReconnectMaxSeconds = 5
        };

        options.Normalize();

        Assert.Equal("127.0.0.1", options.ServerHost);
        Assert.Equal(ProtocolConstants.DefaultAgentPort, options.ServerPort);
        Assert.Equal(5, options.HeartbeatSeconds);
        Assert.InRange(options.CaptureIntervalSeconds, 10, 3600);
        Assert.Equal(320, options.MaxScreenshotWidth);
        Assert.Equal(100, options.JpegQuality);
        Assert.Equal(3, options.ConnectTimeoutSeconds);
        Assert.True(options.ReconnectMaxSeconds >= options.ReconnectMinSeconds, "верхняя пауза не должна быть меньше нижней");
    }

    [Test]
    public static void CorruptedConfigDoesNotCrashAgent()
    {
        string directory = CreateTempDirectory("options-bad");
        try
        {
            string path = Path.Combine(directory, "appsettings.json");
            File.WriteAllText(path, "{ это не json ]");

            ClientOptions options = ClientOptions.Load(path);

            Assert.Equal("127.0.0.1", options.ServerHost, "при повреждённом файле применяются значения по умолчанию");
            Assert.NotNull(options.ClientId);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Test]
    public static void TrySaveFallsBackWhenTargetIsNotWritable()
    {
        string directory = CreateTempDirectory("options-fallback");
        string fallbackDirectory = CreateTempDirectory("options-fallback-target");
        try
        {
            // Вместо каталога по пути конфигурации лежит файл: создать каталог невозможно.
            string blocker = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(blocker, "x");

            string readOnlyPath = Path.Combine(blocker, "appsettings.json");
            string fallbackPath = Path.Combine(fallbackDirectory, "appsettings.json");

            var options = new ClientOptions { ClientId = "fallback-1", ConfigPath = readOnlyPath };

            bool saved = options.TrySave(fallbackPath);

            Assert.True(saved, "TrySave должен сохранить конфигурацию по запасному пути");
            Assert.Equal(fallbackPath, options.ConfigPath, "при ошибке записи используется запасной путь");
            Assert.Equal("fallback-1", ClientOptions.Load(fallbackPath).ClientId);
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteDirectory(fallbackDirectory);
        }
    }

    [Test]
    public static void AutostartEntryCanBeEnabledAndDisabled()
    {
        // Исходное состояние пользователя восстанавливается в finally: тест не должен
        // оставлять запись, указывающую на исполняемый файл тестов.
        string? original = AutostartManager.GetRegisteredCommand();
        string executable = AutostartManager.GetExecutablePath();

        try
        {
            Assert.True(AutostartManager.Enable(executable), "автозапуск должен включаться");
            Assert.True(AutostartManager.IsEnabled(), "после включения запись реестра должна существовать");
            Assert.Contains(AutostartManager.GetRegisteredCommand()!, executable, "в реестре должен быть путь к агенту");

            Assert.True(AutostartManager.Disable(), "автозапуск должен отключаться");
            Assert.False(AutostartManager.IsEnabled(), "после отключения записи быть не должно");
            Assert.False(AutostartManager.Disable(), "повторное отключение возвращает false");
        }
        finally
        {
            AutostartManager.SetRegisteredCommand(original);
        }
    }

    [Test]
    public static void AutostartCommandCanBeWrittenAndRemoved()
    {
        string? original = AutostartManager.GetRegisteredCommand();
        try
        {
            AutostartManager.SetRegisteredCommand("\"C:\\Program Files\\Test\\agent.exe\"");
            Assert.Equal("\"C:\\Program Files\\Test\\agent.exe\"", AutostartManager.GetRegisteredCommand());

            AutostartManager.SetRegisteredCommand(null);
            Assert.Null(AutostartManager.GetRegisteredCommand(), "запись должна удаляться");
        }
        finally
        {
            AutostartManager.SetRegisteredCommand(original);
        }
    }

    [Test]
    public static void MacAddressIsDetectedOrEmpty()
    {
        string mac = AgentClient.GetPrimaryMacAddress();
        Assert.True(mac.Length == 0 || mac.Contains(':'), "MAC-адрес должен быть пустым или в формате AA:BB:CC:DD:EE:FF");
    }

    [Test]
    public static void AgentVersionIsReported()
    {
        Assert.True(AgentInfo.Version.Length > 0, "версия агента должна быть известна");
        Assert.Contains(AgentInfo.Version, ".");
    }
}
