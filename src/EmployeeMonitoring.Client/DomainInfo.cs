using Microsoft.Win32;

namespace EmployeeMonitoring.Client;

/// <summary>
/// Определяет домен или рабочую группу, в которой состоит компьютер, — для колонки
/// «домен» в панели оператора. Данные берутся из системного реестра
/// (HKLM\SYSTEM\CurrentControlSet\Control\ComputerName), поэтому дополнительных
/// вызовов WinAPI не требуется.
/// </summary>
internal static class DomainInfo
{
    private const string ComputerNameKey = @"SYSTEM\CurrentControlSet\Control\ComputerName";

    private static string? _domain;
    private static bool _resolved;

    /// <summary>
    /// Имя домена в верхнем регистре; для рабочей группы — её имя; для автономного
    /// компьютера — «локальный вход», чтобы колонка не дублировала имя компьютера.
    /// </summary>
    public static string GetDomainName()
    {
        if (_resolved && _domain is not null)
        {
            return _domain;
        }

        string value = Resolve();
        _domain = value;
        _resolved = true;
        return value;
    }

    /// <summary>true, если компьютер введён в домен.</summary>
    public static bool IsDomainJoined() => ReadValue("DnsDomainName") is not null;

    private static string Resolve()
    {
        try
        {
            string? domain = ReadValue("DnsDomainName");
            if (!string.IsNullOrWhiteSpace(domain))
            {
                return domain.ToUpperInvariant();
            }

            string? workgroup = ReadValue("WorkgroupName");
            if (!string.IsNullOrWhiteSpace(workgroup))
            {
                return workgroup.ToUpperInvariant();
            }
        }
        catch (Exception)
        {
            // Реестр недоступен — используем запасной вариант.
        }

        return Fallback();
    }

    private static string? ReadValue(string name)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{ComputerNameKey}\{name}");
        return key?.GetValue(null) as string;
    }

    /// <summary>Запасной вариант: имя домена пользователя из окружения.</summary>
    private static string Fallback()
    {
        string domain = Environment.UserDomainName;
        if (string.IsNullOrWhiteSpace(domain))
        {
            return "неизвестно";
        }

        // На автономном компьютере домен пользователя совпадает с именем компьютера.
        return string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            ? "локальный вход"
            : domain.ToUpperInvariant();
    }
}
