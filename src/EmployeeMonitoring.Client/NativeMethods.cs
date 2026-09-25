using System.Runtime.InteropServices;
using System.Text;

namespace EmployeeMonitoring.Client;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint CbSize;
        public uint DwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    public static IntPtr ForegroundWindow => GetForegroundWindow();

    public static string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        int length = GetWindowTextLengthW(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 2);
        int copied = GetWindowTextW(hWnd, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString(0, Math.Min(copied, buffer.Length)) : string.Empty;
    }

    public static string GetProcessName(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0)
            {
                return string.Empty;
            }

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Количество секунд бездействия пользователя (нет ввода с клавиатуры/мыши).</summary>
    public static int GetIdleSeconds()
    {
        var info = new LASTINPUTINFO { CbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            return 0;
        }

        // Счётчик миллисекунд 32-битный, поэтому арифметика выполняется по модулю 2^32.
        uint now = unchecked((uint)Environment.TickCount);
        uint delta = unchecked(now - info.DwTime);
        return (int)Math.Min(delta / 1000, int.MaxValue);
    }

    /// <summary>Возвращает (время простоя, время ядра, время пользователя) в 100-наносекундных единицах.</summary>
    public static (ulong Idle, ulong Kernel, ulong User) GetSystemTimes()
    {
        if (!GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
        {
            return (0, 0, 0);
        }

        return (ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static ulong ToUInt64(FILETIME value) => ((ulong)value.High << 32) | value.Low;
}
