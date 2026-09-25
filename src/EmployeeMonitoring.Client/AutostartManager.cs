using Microsoft.Win32;
using System.Windows.Forms;

namespace EmployeeMonitoring.Client;

/// <summary>
/// Автозапуск агента при входе пользователя в систему.
/// Используется ключ реестра HKCU\Software\Microsoft\Windows\CurrentVersion\Run,
/// поэтому запись не требует прав администратора.
/// </summary>
internal static class AutostartManager
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "EmployeeMonitoringAgent";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static bool Enable(string executablePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть ключ автозапуска.");

        key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
        return true;
    }

    public static bool Disable()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is null)
        {
            return false;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
        return true;
    }

    public static string GetExecutablePath() => Environment.ProcessPath ?? Application.ExecutablePath;
}
