using System.Text;
using EmployeeMonitoring.Protocol;
using System.Windows.Forms;

namespace EmployeeMonitoring.Client;

internal static class Program
{
    private const string AppFolderName = "EmployeeMonitoring";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var arguments = CommandLineArguments.Parse(args);

            // Служебные ключи работают как у обычной консольной программы: подключаем
            // консоль родительского процесса, иначе вывод WinExe теряется.
            bool needsConsole = arguments.ShowHelp || arguments.InstallAutostart || arguments.UninstallAutostart || arguments.CaptureOnce;
            if (needsConsole)
            {
                ConsoleBridge.AttachToParent();
            }

            Console.OutputEncoding = Encoding.UTF8;
            return Run(arguments);
        }
        catch (Exception ex)
        {
            AgentLog.Error("Критическая ошибка при запуске агента", ex);
            try
            {
                MessageBox.Show(
                    $"Не удалось запустить агент мониторинга.{Environment.NewLine}{Environment.NewLine}{ex}",
                    "Агент мониторинга", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception)
            {
                // Нет возможности показать диалог.
            }

            return 1;
        }
    }

    private static int Run(CommandLineArguments arguments)
    {
        if (arguments.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
        Directory.CreateDirectory(dataDirectory);
        AgentLog.Initialize(Path.Combine(dataDirectory, "logs"));

        string configPath = arguments.ConfigPath ?? ResolveConfigPath(dataDirectory);
        string fallbackConfigPath = Path.Combine(dataDirectory, "appsettings.json");
        bool configExisted = File.Exists(configPath);
        ClientOptions options = ClientOptions.Load(configPath);

        if (!configExisted || string.IsNullOrWhiteSpace(options.ClientId))
        {
            options.ClientId = Guid.NewGuid().ToString("N");
            if (options.TrySave(fallbackConfigPath))
            {
                configPath = options.ConfigPath;
                AgentLog.Info($"Идентификатор агента сохранён в конфигурации: {configPath}");
            }
        }

        if (arguments.ServerHost is not null)
        {
            options.ServerHost = arguments.ServerHost;
        }

        if (arguments.ServerPort is not null)
        {
            options.ServerPort = arguments.ServerPort.Value;
        }

        if (arguments.Token is not null)
        {
            options.Token = arguments.Token;
        }

        if (arguments.NoAutostart)
        {
            options.StartWithWindows = false;
        }

        options.Normalize();

        try
        {
            if (arguments.InstallAutostart)
            {
                AutostartManager.Enable(AutostartManager.GetExecutablePath());
                Console.WriteLine($"Автозапуск включён: {AutostartManager.RunKeyPath}\\{AutostartManager.ValueName}");
                return 0;
            }

            if (arguments.UninstallAutostart)
            {
                bool removed = AutostartManager.Disable();
                Console.WriteLine(removed ? "Автозапуск отключён." : "Автозапуск не был настроен.");
                return 0;
            }

            if (arguments.CaptureOnce)
            {
                return RunSingleCapture(options);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Error("Ошибка выполнения команды", ex);
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }

        if (options.StartWithWindows && !AutostartManager.IsEnabled())
        {
            TryEnableAutostartSilently();
        }

        if (!InstanceLock.TryAcquire(dataDirectory, TimeSpan.FromSeconds(5), out InstanceLock? instanceLock, out string lockMessage) || instanceLock is null)
        {
            MessageBox.Show(
                $"Агент уже запущен для пользователя {Environment.UserName}.{Environment.NewLine}{Environment.NewLine}{lockMessage}",
                "Агент мониторинга", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        using (instanceLock)
        {
            ApplicationConfiguration.Initialize();
            AgentLog.Info($"Агент запущен (pid {Environment.ProcessId}), конфигурация: {configPath}, сервер: {options.ServerHost}:{options.ServerPort}");

            TrayAgent? tray = null;

            var client = new AgentClient(options, (text, urgent) =>
            {
                tray?.ShowNotification(text, urgent);
                return true;
            });

            var trayAgent = new TrayAgent(options, client);
            tray = trayAgent;
            trayAgent.Start();
            Application.Run(trayAgent);
        }

        return 0;
    }

    private static int RunSingleCapture(ClientOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var client = new AgentClient(options, static (_, _) => false);
            Task run = client.RunAsync(cts.Token);

            // Ожидаем именно фактической отправки снимка, а не только его подготовки.
            while (!client.HasSentScreenshot && !cts.IsCancellationRequested)
            {
                Thread.Sleep(200);
            }

            if (!client.HasSentScreenshot)
            {
                Console.Error.WriteLine("Снимок не отправлен: нет связи с сервером или истекло время ожидания.");
                return 2;
            }

            Console.WriteLine("Снимок экрана успешно отправлен на сервер.");
            cts.Cancel();
            run.Wait(TimeSpan.FromSeconds(3));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }
    }

    private static void TryEnableAutostartSilently()
    {
        try
        {
            AutostartManager.Enable(AutostartManager.GetExecutablePath());
            AgentLog.Info("Автозапуск включён автоматически при первом запуске агента");
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"Не удалось включить автозапуск: {ex.Message}");
        }
    }

    private static string ResolveConfigPath(string dataDirectory)
    {
        string portable = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(portable))
        {
            return portable;
        }

        return Path.Combine(dataDirectory, "appsettings.json");
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Агент мониторинга рабочей активности (Windows).

            Использование:
              EmployeeMonitoring.Client.exe [ключи]

            Ключи:
              --server <host>        адрес сервера мониторинга
              --port <number>        порт сервера (по умолчанию 45800)
              --token <value>        общий токен доступа
              --config <path>        путь к файлу конфигурации
              --capture-once         разовый снимок экрана и выход (для планировщика задач)
              --install              включить автозапуск при входе в Windows
              --uninstall            отключить автозапуск
              --no-autostart         запустить без включения автозапуска
              --help                 справка
            """);
    }

    private sealed class CommandLineArguments
    {
        public bool ShowHelp { get; private init; }
        public bool InstallAutostart { get; private init; }
        public bool UninstallAutostart { get; private init; }
        public bool CaptureOnce { get; private init; }
        public bool NoAutostart { get; private init; }
        public string? ServerHost { get; private init; }
        public int? ServerPort { get; private init; }
        public string? Token { get; private init; }
        public string? ConfigPath { get; private init; }

        public static CommandLineArguments Parse(string[] args)
        {
            bool help = false, install = false, uninstall = false, captureOnce = false, noAutostart = false;
            string? host = null, token = null, config = null;
            int? port = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].Trim();
                switch (arg.ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        help = true;
                        break;
                    case "--install":
                        install = true;
                        break;
                    case "--uninstall":
                        uninstall = true;
                        break;
                    case "--capture-once":
                        captureOnce = true;
                        break;
                    case "--no-autostart":
                        noAutostart = true;
                        break;
                    case "--server":
                    case "-s":
                        host = NextValue(args, ref i);
                        break;
                    case "--port":
                    case "-p":
                        if (int.TryParse(NextValue(args, ref i), out int parsedPort))
                        {
                            port = parsedPort;
                        }

                        break;
                    case "--token":
                        token = NextValue(args, ref i);
                        break;
                    case "--config":
                    case "-c":
                        config = NextValue(args, ref i);
                        break;
                }
            }

            return new CommandLineArguments
            {
                ShowHelp = help,
                InstallAutostart = install,
                UninstallAutostart = uninstall,
                CaptureOnce = captureOnce,
                NoAutostart = noAutostart,
                ServerHost = host,
                ServerPort = port,
                Token = token,
                ConfigPath = config
            };
        }

        private static string? NextValue(string[] args, ref int index)
        {
            if (index + 1 < args.Length)
            {
                index++;
                return args[index];
            }

            return null;
        }
    }
}
