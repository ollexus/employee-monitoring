using System.Runtime.InteropServices;

namespace EmployeeMonitoring.Client;

/// <summary>
/// Подключение к консоли родительского процесса.
///
/// Агент собран как WinExe (GUI-подсистема), поэтому при запуске из PowerShell или
/// cmd.exe консоль к нему не подключается, и <see cref="Console"/>-вывод теряется.
/// Для служебных ключей (--help, --install, --uninstall, --capture-once) консоль
/// подключается явно — тогда сообщения видны в терминале как у обычной программы.
/// </summary>
internal static class ConsoleBridge
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    private static bool _attached;

    /// <summary>Подключает консоль родителя, если её ещё нет. Вызывать только для консольных ключей.</summary>
    public static void AttachToParent()
    {
        if (_attached)
        {
            return;
        }

        try
        {
            if (IsRedirected())
            {
                _attached = true;
                return;
            }

            // AllocConsole намеренно не вызывается: у агента нет своих окон,
            // мигающий чёрный консольный экран хуже, чем тишина в терминале.
            if (!AttachConsole(AttachParentProcess))
            {
                return;
            }

            // После AttachConsole стандартные потоки нужно перепривязать к дескрипторам консоли.
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
            _attached = true;
        }
        catch (Exception)
        {
            // Если подключиться не удалось, сообщения останутся в журнале агента.
        }
    }

    private static bool IsRedirected() => GetStdHandle(StdOutputHandle) != IntPtr.Zero;
}
