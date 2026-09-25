using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using EmployeeMonitoring.Protocol;

namespace EmployeeMonitoring.Tests;

/// <summary>
/// Демонстрационный режим: несколько виртуальных рабочих мест подключаются к запущенному
/// серверу и передают состояние и снимки экрана. Позволяет увидеть панель в работе
/// без установки агента на другие компьютеры.
///
/// Запуск: dotnet run --project src/EmployeeMonitoring.Tests -- --demo [host] [port]
/// </summary>
internal static class DemoMode
{
    private sealed record Station(string Machine, string User, string Domain, Color Accent);

    private static readonly Station[] Stations =
    [
        new("WS-01-BUKHOV", "ivanov", "corp.local", Color.FromArgb(47, 123, 224)),
        new("WS-02-TVER", "petrova", "corp.local", Color.FromArgb(31, 163, 120)),
        new("WS-03-KAZAN", "sidorov", "corp.local", Color.FromArgb(196, 122, 32)),
        new("WS-04-SPB", "kuznetsova", "corp.local", Color.FromArgb(150, 62, 180))
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        string host = args.Length > 1 ? args[1] : "127.0.0.1";
        int port = args.Length > 2 && int.TryParse(args[2], out int parsed) ? parsed : ProtocolConstants.DefaultAgentPort;

        Console.WriteLine($"Демо-агенты подключаются к {host}:{port}. Для остановки нажмите Ctrl+C.");
        Console.WriteLine($"Откройте панель: http://localhost:5080/");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        var clients = new List<Task>();
        foreach (Station station in Stations)
        {
            clients.Add(RunStationAsync(host, port, station, cts.Token));
        }

        try
        {
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Демо-агенты остановлены.");
        }

        return 0;
    }

    private static async Task RunStationAsync(string host, int port, Station station, CancellationToken token)
    {
        string machine = station.Machine;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await using FakeAgent agent = await ConnectAsync(host, port, station, token).ConfigureAwait(false);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));

                while (!token.IsCancellationRequested)
                {
                    await agent.SendHeartbeatAsync(new Heartbeat
                    {
                        ClientId = $"fake-{machine}",
                        SentAtUtc = DateTime.UtcNow,
                        IdleSeconds = Random.Shared.Next(0, 90),
                        ActiveWindowTitle = PickWindow(),
                        ActiveProcessName = PickProcess(),
                        CpuLoadPercent = Math.Round(Random.Shared.NextDouble() * 70, 1),
                        UsedMemoryBytes = Random.Shared.NextInt64(2L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024),
                        TotalMemoryBytes = 16L * 1024 * 1024 * 1024,
                        UptimeSeconds = Random.Shared.NextInt64(900, 40000),
                        AgentStatus = "Connected"
                    }, token).ConfigureAwait(false);

                    await agent.SendScreenshotAsync(CreateImage(station), 1280, 720, cancellationToken: token).ConfigureAwait(false);

                    if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{machine}: {ex.Message} — повтор через 5 с");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static async Task<FakeAgent> ConnectAsync(string host, int port, Station station, CancellationToken token)
    {
        FakeAgent agent = await FakeAgent.ConnectAsync(port, station.Machine, station.User, station.Domain)
            .WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);

        if (!agent.AuthResult.Success)
        {
            await agent.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"сервер отклонил подключение: {agent.AuthResult.Message}");
        }

        Console.WriteLine($"{station.Machine} подключён (сессия #{agent.AuthResult.CaptureIntervalSeconds}с между снимками)");
        return agent;
    }

    private static byte[] CreateImage(Station station)
    {
        using var bitmap = new Bitmap(1280, 720);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var background = new LinearGradientBrush(
                new Rectangle(0, 0, 1280, 720),
                Color.FromArgb(24, 34, 50),
                Color.FromArgb(12, 18, 28),
                LinearGradientMode.Vertical);
            g.FillRectangle(background, 0, 0, 1280, 720);

            using var header = new SolidBrush(station.Accent);
            g.FillRectangle(header, 0, 0, 1280, 8);

            using var titleFont = new Font("Segoe UI", 44, FontStyle.Bold);
            using var captionFont = new Font("Segoe UI", 20, FontStyle.Regular);
            using var white = new SolidBrush(Color.White);
            using var gray = new SolidBrush(Color.FromArgb(150, 165, 189));

            g.DrawString(station.Machine, titleFont, white, 60, 70);
            g.DrawString($"{station.Domain}\\{station.User}", captionFont, gray, 64, 140);

            Random random = Random.Shared;
            for (int i = 0; i < 14; i++)
            {
                int width = random.Next(60, 460);
                int height = random.Next(18, 34);
                int x = 60 + (i % 4) * 300;
                int y = 240 + (i / 4) * 90;
                using var block = new SolidBrush(Color.FromArgb(40, 60, 90, 150));
                g.FillRectangle(block, x, y, width, height);
                using var bar = new SolidBrush(Color.FromArgb(120, station.Accent));
                g.FillRectangle(bar, x, y, width, 4);
            }

            g.DrawString($"Демонстрационный снимок · {DateTime.Now:HH:mm:ss}", captionFont, gray, 60, 640);
        }

        using var buffer = new MemoryStream();
        ImageCodecInfo? codec = null;
        foreach (ImageCodecInfo candidate in ImageCodecInfo.GetImageEncoders())
        {
            if (candidate.FormatID == ImageFormat.Jpeg.Guid)
            {
                codec = candidate;
                break;
            }
        }

        if (codec is null)
        {
            throw new InvalidOperationException("JPEG-кодек недоступен в системе.");
        }

        var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 70L);
        bitmap.Save(buffer, codec, parameters);
        return buffer.ToArray();
    }

    private static readonly string[] Windows =
    [
        "Excel — Бюджет 2026.xlsx",
        "1С:Предприятие — Договоры",
        "Visual Studio Code — AgentClient.cs",
        "Google Chrome — ТЗ.docx",
        "Outlook — Новая почта",
        "Autodesk Revit — Проект А"
    ];

    private static readonly string[] Processes =
    ["EXCEL", "1cv8", "Code", "chrome", "OUTLOOK", "Revit"];

    private static string PickWindow() => Windows[Random.Shared.Next(Windows.Length)];

    private static string PickProcess() => Processes[Random.Shared.Next(Processes.Length)];
}
