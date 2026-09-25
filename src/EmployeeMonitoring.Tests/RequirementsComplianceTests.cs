using System.Text.Json;
using EmployeeMonitoring.Protocol;
using EmployeeMonitoring.Server.Monitoring;

namespace EmployeeMonitoring.Tests;

/// <summary>
/// Автоматические проверки соответствия исходным требованиям задания:
/// клиент на C# под Windows, связь с сервером, отсутствие сторонних библиотек,
/// состав данных в панели (домен/компьютер/ip/пользователь, время последней
/// активности) и возможность получить снимок экрана.
/// </summary>
public static class RequirementsComplianceTests
{
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmployeeMonitoring.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static IEnumerable<string> GetProjects()
    {
        string root = FindRepositoryRoot();
        return Directory.Exists(Path.Combine(root, "src"))
            ? Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories).OrderBy(path => path)
            : [];
    }

    /// <summary>Требование: сторонние библиотеки использовать нельзя.</summary>
    [Test]
    public static void SolutionContainsNoThirdPartyPackages()
    {
        string[] projects = GetProjects().ToArray();
        Assert.True(projects.Length >= 4, $"ожидалось не менее 4 проектов, найдено {projects.Length}");

        var offenders = new List<string>();
        foreach (string project in projects)
        {
            string xml = File.ReadAllText(project);
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(xml, "<PackageReference\\s+Include=\"([^\"]+)\""))
            {
                offenders.Add($"{Path.GetFileName(project)} → {match.Groups[1].Value}");
            }
        }

        Assert.Equal(0, offenders.Count, "обнаружены внешние пакеты: " + string.Join(", ", offenders));
    }

    /// <summary>Проверка, что внешние пакеты не подтягиваются и транзитивно (по obj/project.assets.json).</summary>
    [Test]
    public static void NoThirdPartyLibrariesInResolvedGraph()
    {
        string root = FindRepositoryRoot();
        string[] assetsFiles = Directory.Exists(root)
            ? Directory.GetFiles(root, "project.assets.json", SearchOption.AllDirectories)
            : [];

        var offenders = new List<string>();
        foreach (string assetsFile in assetsFiles)
        {
            string content = File.ReadAllText(assetsFile);
            if (!content.Contains("\"libraries\"", StringComparison.Ordinal))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraries))
            {
                continue;
            }

            foreach (JsonProperty library in libraries.EnumerateObject())
            {
                string name = library.Name;
                if (name.StartsWith("EmployeeMonitoring.", StringComparison.Ordinal))
                {
                    continue;
                }

                if (name.Contains("/") && !name.StartsWith("Microsoft.", StringComparison.Ordinal) &&
                    !name.StartsWith("NETStandard.", StringComparison.Ordinal) &&
                    !name.StartsWith("System.", StringComparison.Ordinal) &&
                    !name.StartsWith("runtime.", StringComparison.Ordinal) &&
                    !name.StartsWith("Microsoft.NETCore.", StringComparison.Ordinal) &&
                    !name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) &&
                    !name.StartsWith("Microsoft.WindowsDesktop.", StringComparison.Ordinal) &&
                    !name.StartsWith("Microsoft.NETCore.Platforms", StringComparison.Ordinal))
                {
                    offenders.Add(name);
                }
            }
        }

        Assert.Equal(0, offenders.Count, "во внешних пакетах найдены сторонние библиотеки: " + string.Join(", ", offenders));
    }

    /// <summary>Требование: клиент под Windows, на C#, фоновое приложение (не консоль).</summary>
    [Test]
    public static void ClientIsWindowsDotNetBackgroundApplication()
    {
        string clientProject = GetProjects().First(path => path.Contains("EmployeeMonitoring.Client", StringComparison.Ordinal));
        string xml = File.ReadAllText(clientProject);

        Assert.Contains(xml, "<TargetFramework>net10.0-windows</TargetFramework>", "клиент должен собираться под Windows");
        Assert.Contains(xml, "<OutputType>WinExe</OutputType>", "клиент должен быть фоновым приложением без консольного окна");
        Assert.Contains(xml, "UseWindowsForms", "клиент использует WinForms (значок в трее)");
    }

    /// <summary>Требование: автозапуск при входе пользователя в систему.</summary>
    [Test]
    public static void AutostartIsConfiguredPerUser()
    {
        string autostart = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "EmployeeMonitoring.Client", "AutostartManager.cs"));

        Assert.Contains(autostart, @"Software\Microsoft\Windows\CurrentVersion\Run", "автозапуск выполняется через ключ Run");
        Assert.Contains(autostart, "Registry.CurrentUser", "автозапуск настраивается для текущего пользователя без прав администратора");
    }

    /// <summary>Требование: связь с сервером по протоколу (собственный протокол поверх TCP).</summary>
    [Test]
    public static void ClientServerUseOwnProtocolOverTcp()
    {
        string root = FindRepositoryRoot();
        string gateway = File.ReadAllText(Path.Combine(root, "src", "EmployeeMonitoring.Server", "Monitoring", "AgentGateway.cs"));
        string frame = File.ReadAllText(Path.Combine(root, "src", "EmployeeMonitoring.Protocol", "FrameChannel.cs"));

        Assert.Contains(gateway, "new TcpListener", "сервер принимает подключения агентов по TCP");
        Assert.Contains(frame, "WriteAsync", "протокол реализует отправку кадров");
        Assert.Contains(frame, "ReadAsync", "протокол реализует чтение кадров");
    }

    /// <summary>Требование: веб-интерфейс сервера со списком клиентов.</summary>
    [Test]
    public static void ServerExposesWebDashboardAndClientList()
    {
        string root = FindRepositoryRoot();
        string program = File.ReadAllText(Path.Combine(root, "src", "EmployeeMonitoring.Server", "Program.cs"));

        Assert.Contains(program, "UseStaticFiles", "веб-панель раздаётся как статические файлы");
        Assert.Contains(program, "/api/clients", "есть API списка клиентов");
        Assert.Contains(program, "/screenshot", "есть API получения снимка экрана");
        Assert.Contains(program, "/command", "есть API отправки команды агенту");
        Assert.True(File.Exists(Path.Combine(root, "src", "EmployeeMonitoring.Server", "wwwroot", "index.html")),
            "панель оператора должна быть веб-интерфейсом (index.html)");
    }

    /// <summary>Требование: в списке клиентов домен, компьютер, ip, пользователь.</summary>
    [Test]
    public static void ClientListContainsRequiredRequisites()
    {
        var snapshot = new ClientSnapshot
        {
            ClientId = "c1",
            MachineName = "WS-01",
            Domain = "CORP.LOCAL",
            UserName = "ivanov",
            IpAddress = "10.0.0.15",
            LastActivityUtc = new DateTime(2026, 5, 6, 10, 30, 0, DateTimeKind.Utc),
            LastSeenUtc = new DateTime(2026, 5, 6, 10, 30, 5, DateTimeKind.Utc),
            HasScreenshot = true,
            ScreenshotAtUtc = new DateTime(2026, 5, 6, 10, 29, 0, DateTimeKind.Utc)
        };

        string json = Json.SerializeToString(snapshot);

        Assert.Contains(json, "\"machineName\":\"WS-01\"", "в списке должен быть компьютер");
        Assert.Contains(json, "\"domain\":\"CORP.LOCAL\"", "в списке должен быть домен");
        Assert.Contains(json, "\"ipAddress\":\"10.0.0.15\"", "в списке должен быть IP-адрес");
        Assert.Contains(json, "\"userName\":\"ivanov\"", "в списке должен быть пользователь");
        Assert.Contains(json, "\"lastActivityUtc\"", "в списке должно быть время последней активности");
        Assert.Contains(json, "\"lastSeenUtc\"", "в списке должно быть время последней связи");
        Assert.Contains(json, "\"screenshotAtUtc\"", "в списке должно быть время последнего снимка");
    }

    /// <summary>Требование: время последней активности клиента вычисляется по данным агента.</summary>
    [Test]
    public static void LastActivityIsTakenFromAgentInputTime()
    {
        var session = new ClientSession(new AuthRequest
        {
            ClientId = "c1",
            MachineName = "WS-01",
            Domain = "CORP",
            UserName = "ivanov"
        }, "10.0.0.15");

        Assert.Null(session.ToSnapshot().LastActivityUtc, "до первого heartbeat время активности неизвестно");

        DateTime lastInput = new(2026, 5, 6, 10, 0, 0, DateTimeKind.Utc);
        session.Touch(new Heartbeat
        {
            ClientId = "c1",
            IdleSeconds = 30,
            LastInputAtUtc = lastInput,
            SentAtUtc = lastInput.AddSeconds(30)
        });

        ClientSnapshot snapshot = session.ToSnapshot();
        Assert.NotNull(snapshot.LastActivityUtc);
        Assert.Equal(lastInput, snapshot.LastActivityUtc!.Value, "время активности берётся из момента последнего ввода сотрудника");
    }

    [Test]
    public static void LastActivityFallsBackForLegacyAgents()
    {
        var session = new ClientSession(new AuthRequest { ClientId = "c1", MachineName = "WS", UserName = "u" }, "10.0.0.1");

        // Агент без поля LastInputAtUtc, но с малым бездействием.
        session.Touch(new Heartbeat { ClientId = "c1", IdleSeconds = 5 });
        Assert.NotNull(session.ToSnapshot().LastActivityUtc, "для старых агентов время активности считается текущим при активности");

        DateTime before = session.ToSnapshot().LastActivityUtc!.Value;
        session.Touch(new Heartbeat { ClientId = "c1", IdleSeconds = 600 });
        Assert.Equal(before, session.ToSnapshot().LastActivityUtc!.Value, "при длительном бездействии время активности не меняется");
    }

    /// <summary>Требование: снимок рабочего стола клиента можно получить и передать на сервер.</summary>
    [Test]
    public static void ScreenshotCanBeStoredAndServedByServer()
    {
        var session = new ClientSession(new AuthRequest { ClientId = "c1", MachineName = "WS", UserName = "u" }, "10.0.0.1");
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0x20, 0x30];

        session.StoreScreenshot(new ScreenshotMeta
        {
            CapturedAtUtc = DateTime.UtcNow,
            Width = 1920,
            Height = 1080,
            SizeBytes = jpeg.Length
        }, jpeg);

        ClientSnapshot snapshot = session.ToSnapshot();

        Assert.True(snapshot.HasScreenshot, "сервер должен хранить снимок для выдачи по запросу");
        Assert.Equal(1920, snapshot.ScreenshotWidth);
        Assert.BytesEqual(jpeg, session.GetScreenshot()!);
    }

    /// <summary>Требование: домен в реквизитах клиента не должен дублировать имя компьютера.</summary>
    [Test]
    public static void DomainIsResolvedFromWindowsJoinState()
    {
        string domain = EmployeeMonitoring.Client.DomainInfo.GetDomainName();

        Assert.NotNull(domain, "домен должен определяться");
        Assert.True(domain.Length > 0, "домен не может быть пустым");
        Assert.False(domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && !EmployeeMonitoring.Client.DomainInfo.IsDomainJoined(),
            "если компьютер не в домене, колонка «домен» не должна повторять имя компьютера");
    }
}
