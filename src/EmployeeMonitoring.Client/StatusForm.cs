using System.Drawing;
using System.Windows.Forms;

namespace EmployeeMonitoring.Client;

/// <summary>Небольшое окно со сводкой состояния агента.</summary>
internal sealed class StatusForm : Form
{
    private readonly ClientOptions _options;
    private readonly AgentClient _client;
    private readonly Dictionary<string, Label> _values = new();

    public StatusForm(ClientOptions options, AgentClient client)
    {
        _options = options;
        _client = client;

        Text = "Агент мониторинга рабочей активности";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(460, 340);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 10,
            Padding = new Padding(12),
            AutoSize = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, "Сервер", $"{options.ServerHost}:{options.ServerPort}");
        AddRow(layout, "Состояние", "—");
        AddRow(layout, "В сети с", "—");
        AddRow(layout, "Последний снимок", "—");
        AddRow(layout, "Следующий снимок", "—");
        AddRow(layout, "Активное окно", "—");
        AddRow(layout, "Бездействие", "—");
        AddRow(layout, "Загрузка CPU", "—");
        AddRow(layout, "Версия агента", AgentInfo.Version);
        AddRow(layout, "Идентификатор", options.ClientId);

        Controls.Add(layout);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(12, 0, 12, 8)
        };

        var captureButton = new Button { Text = "Сделать снимок", AutoSize = true };
        captureButton.Click += (_, _) => _client.TriggerCapture();
        buttons.Controls.Add(captureButton);

        var hideButton = new Button { Text = "Свернуть в трей", AutoSize = true };
        hideButton.Click += (_, _) => Close();
        buttons.Controls.Add(hideButton);

        Controls.Add(buttons);

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            Padding = new Padding(12, 4, 12, 4),
            ForeColor = Color.DimGray,
            Text = "Агент работает в фоне. Снимки экрана отправляются на сервер мониторинга " +
                   "по расписанию или по команде оператора. Конфигурация: " + options.ConfigPath
        };
        Controls.Add(hint);

        UpdateState();
    }

    private void AddRow(TableLayoutPanel layout, string caption, string value)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = caption,
            AutoSize = true,
            Margin = new Padding(0, 4, 8, 4),
            TextAlign = ContentAlignment.TopLeft
        };

        var field = new Label
        {
            Text = value,
            AutoSize = true,
            MaximumSize = new Size(280, 0),
            Margin = new Padding(0, 4, 0, 4)
        };

        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(field, 1, row);
        _values[caption] = field;
    }

    public void UpdateState()
    {
        if (IsDisposed)
        {
            return;
        }

        SetValue("Состояние", _client.StatusText);
        SetValue("В сети с", _client.ConnectedSinceUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "—");
        SetValue("Последний снимок", _client.LastScreenshotUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—");
        SetValue("Следующий снимок", _client.NextCaptureAtUtc > DateTime.MinValue
            ? _client.NextCaptureAtUtc.ToLocalTime().ToString("HH:mm:ss")
            : "—");
        SetValue("Активное окно", Truncate(SystemActivity.ActiveWindowTitle, 60));
        SetValue("Бездействие", $"{SystemActivity.IdleSeconds} с");
        SetValue("Загрузка CPU", $"{SystemActivity.CpuLoadPercent:0.#} %");
    }

    private void SetValue(string key, string value)
    {
        if (_values.TryGetValue(key, out Label? label) && !label.IsDisposed)
        {
            label.Text = value;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) ? "—" : value.Length <= maxLength ? value : value[..maxLength] + "…";
}
