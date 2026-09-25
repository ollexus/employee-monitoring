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

    public static bool IsEnabled() => !string.IsNullOrWhiteSpace(GetRegisteredCommand());

    /// <summary>Команда, записанная в автозапуск, либо null, если записи нет.</summary>
    public static string? GetRegisteredCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public static bool Enable(string executablePath)
    {
        SetRegisteredCommand($"\"{executablePath}\"");
        return true;
    }

    public static bool Disable()
    {
        if (IsEnabled())
        {
            SetRegisteredCommand(null);
            return true;
        }

        return false;
    }

    /// <summary>Записывает команду автозапуска; null — удалить запись.</summary>
    public static void SetRegisteredCommand(string? command)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть ключ автозапуска.");

        if (string.IsNullOrEmpty(command))
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
    }

    public static string GetExecutablePath() => Environment.ProcessPath ?? Application.ExecutablePath;
}
