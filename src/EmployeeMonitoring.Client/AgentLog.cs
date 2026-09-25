using System.Text;

namespace EmployeeMonitoring.Client;

internal enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

internal static class AgentLog
{
    private static readonly object SyncRoot = new();
    private static string _directory = string.Empty;
    private static bool _echoToDebug;

    public static string CurrentFile { get; private set; } = string.Empty;

    public static void Initialize(string directory, bool echoToDebug = true)
    {
        _directory = directory;
        _echoToDebug = echoToDebug;
        Directory.CreateDirectory(_directory);
        CurrentFile = Path.Combine(_directory, $"agent-{DateTime.Now:yyyyMMdd}.log");
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    private static void Write(LogLevel level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level,-7}] {message}";

        if (_echoToDebug)
        {
            System.Diagnostics.Debug.WriteLine(line);
        }

        if (string.IsNullOrEmpty(_directory))
        {
            return;
        }

        try
        {
            lock (SyncRoot)
            {
                string file = Path.Combine(_directory, $"agent-{DateTime.Now:yyyyMMdd}.log");
                if (!string.Equals(file, CurrentFile, StringComparison.OrdinalIgnoreCase))
                {
                    CurrentFile = file;
                }

                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Не удалось записать в журнал: {ex.Message}");
        }
    }
}
