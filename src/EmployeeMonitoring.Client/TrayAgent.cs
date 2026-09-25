using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace EmployeeMonitoring.Client;

/// <summary>
/// Фоновый режим работы агента: значок в области уведомлений, контекстное меню и окно состояния.
/// </summary>
internal sealed class TrayAgent : ApplicationContext
{
    private readonly ClientOptions _options;
    private readonly AgentClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly Form _marshaller = new() { Visible = false, Opacity = 0 };
    private readonly ToolStripMenuItem _autostartItem;
    private StatusForm? _statusForm;
    private Task? _runTask;
    private DateTime? _lastNotificationUtc;

    public TrayAgent(ClientOptions options, AgentClient client)
    {
        _options = options;
        _client = client;

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Сделать снимок сейчас", null, (_, _) => _client.TriggerCapture());
        _menu.Items.Add("Состояние агента…", null, (_, _) => ShowStatusForm());
        _menu.Items.Add(new ToolStripSeparator());

        _autostartItem = new ToolStripMenuItem("Автозапуск при входе в Windows")
        {
            Checked = AutostartManager.IsEnabled(),
            CheckOnClick = true
        };
        _autostartItem.CheckedChanged += OnAutostartChanged;
        _menu.Items.Add(_autostartItem);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Открыть папку с данными", null, (_, _) => OpenDataFolder());
        _menu.Items.Add("Открыть конфигурацию", null, (_, _) => OpenConfigFile());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Выход", null, (_, _) => RequestExit());

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Агент мониторинга: запуск…",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatusForm();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _uiTimer.Tick += (_, _) => RefreshTooltip();

        _client.StatusChanged += text => RunOnUi(() => _notifyIcon.Text = Truncate(text));
        _client.ScreenshotSent += () => RunOnUi(() => _statusForm?.UpdateState());

        _notifyIcon.Text = Truncate(_client.StatusText);
    }

    public void ShowNotification(string text, bool urgent)
    {
        if (!_options.ShowTrayNotifications)
        {
            return;
        }

        // Уведомления о снимках по расписанию приглушаются, чтобы не заваливать
        // пользователя всплывающими сообщениями. События по команде оператора
        // и явные сообщения сервера показываются сразу.
        if (!urgent)
        {
            TimeSpan throttle = TimeSpan.FromMinutes(_options.NotificationThrottleMinutes);
            if (throttle > TimeSpan.Zero && _lastNotificationUtc is not null && DateTime.UtcNow - _lastNotificationUtc.Value < throttle)
            {
                return;
            }
        }

        _lastNotificationUtc = DateTime.UtcNow;
        RunOnUi(() => Notify("Мониторинг рабочей активности", text));
    }

    public void Start()
    {
        _uiTimer.Start();
        _runTask = Task.Run(() => _client.RunAsync(_cts.Token));
        _ = _runTask.ContinueWith(
            task => AgentLog.Error("Цикл связи аварийно завершён", task.Exception),
            TaskScheduler.Default);
    }

    private void OnAutostartChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_autostartItem.Checked)
            {
                AutostartManager.Enable(AutostartManager.GetExecutablePath());
                AgentLog.Info("Автозапуск включён");
                Notify("Автозапуск включён", "Агент будет запущен при входе в Windows.");
            }
            else
            {
                AutostartManager.Disable();
                AgentLog.Info("Автозапуск отключён");
            }
        }
        catch (Exception ex)
        {
            AgentLog.Error("Не удалось изменить параметр автозапуска", ex);
            MessageBox.Show($"Не удалось изменить параметр автозапуска: {ex.Message}", "Агент мониторинга",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _autostartItem.Checked = AutostartManager.IsEnabled();
        }
    }

    private void ShowStatusForm()
    {
        if (_statusForm is not null && !_statusForm.IsDisposed)
        {
            _statusForm.UpdateState();
            _statusForm.Activate();
            return;
        }

        _statusForm = new StatusForm(_options, _client);
        _statusForm.FormClosed += (_, _) => _statusForm = null;
        _statusForm.Show();
        _statusForm.UpdateState();
    }

    private void RefreshTooltip()
    {
        string status = _client.StatusText;
        string next = _client.NextCaptureAtUtc > DateTime.MinValue
            ? _client.NextCaptureAtUtc.ToLocalTime().ToString("HH:mm:ss")
            : "—";
        string text = $"Агент: {status} | Снимок: {_client.LastScreenshotUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "нет"} | след. {next}";
        _notifyIcon.Text = Truncate(text);
        _statusForm?.UpdateState();
    }

    private void Notify(string title, string text)    {
        if (!_options.ShowTrayNotifications)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(4000);
    }

    private void RequestExit()
    {
        try
        {
            _cts.Cancel();
        }
        catch (Exception)
        {
            // Игнорируем.
        }

        _uiTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _marshaller.Dispose();
        _statusForm?.Close();
        AgentLog.Info("Агент остановлен пользователем");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RunOnUi(Action action)
    {
        if (_marshaller.IsDisposed)
        {
            return;
        }

        try
        {
            if (_marshaller.InvokeRequired)
            {
                _marshaller.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (Exception)
        {
            // Окно уже закрыто — игнорируем.
        }
    }

    private void OpenDataFolder()
    {
        string directory = Path.GetDirectoryName(_options.ConfigPath) ?? AppContext.BaseDirectory;
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть папку: {ex.Message}", "Агент мониторинга",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenConfigFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_options.ConfigPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть конфигурацию: {ex.Message}", "Агент мониторинга",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 127 ? value : value[..127];

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var body = new SolidBrush(Color.FromArgb(35, 120, 215));
            using var frame = new Pen(Color.FromArgb(15, 60, 120), 2f);
            using var stand = new SolidBrush(Color.FromArgb(60, 60, 70));

            g.FillRectangle(body, 3, 5, 26, 18);
            g.DrawRectangle(frame, 3, 5, 26, 18);
            g.FillRectangle(stand, 12, 23, 8, 3);
            g.FillRectangle(stand, 8, 26, 16, 2);

            using var chart = new Pen(Color.FromArgb(120, 235, 130), 2f);
            g.DrawLines(chart,
            [
                new PointF(8, 18), new PointF(13, 12), new PointF(18, 15),
                new PointF(24, 9)
            ]);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
