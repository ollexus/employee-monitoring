using System.Diagnostics;
using System.Runtime.InteropServices;
using EmployeeMonitoring.Protocol;

namespace EmployeeMonitoring.Client;

internal static class SystemActivity
{
    private static readonly object SyncRoot = new();
    private static DateTime? _processStartUtc;
    private static ulong _lastIdle;
    private static ulong _lastKernel;
    private static ulong _lastUser;
    private static double _lastCpuLoad;
    private static DateTime _lastSampleUtc = DateTime.MinValue;

    public static int IdleSeconds => NativeMethods.GetIdleSeconds();

    public static string ActiveWindowTitle => NativeMethods.GetWindowTitle(NativeMethods.ForegroundWindow);

    public static string ActiveProcessName => NativeMethods.GetProcessName(NativeMethods.ForegroundWindow);

    public static double CpuLoadPercent
    {
        get
        {
            lock (SyncRoot)
            {
                return _lastCpuLoad;
            }
        }
    }

    public static (long Used, long Total) GetMemory()
    {
        var info = new MemoryStatusEx();
        NativeMemoryStatus.GetMemoryStatusEx(info);
        return ((long)info.TotalPhys - (long)info.AvailablePhys, (long)info.TotalPhys);
    }

    public static long GetUptimeSeconds() => (long)(DateTime.UtcNow - GetProcessStartUtc()).TotalSeconds;

    /// <summary>Время запуска процесса агента (для отчёта об uptime на сервере).</summary>
    public static DateTime GetProcessStartUtc()
    {
        if (_processStartUtc is not null)
        {
            return _processStartUtc.Value;
        }

        DateTime start;
        try
        {
            using Process process = Process.GetCurrentProcess();
            start = process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            start = DateTime.UtcNow;
        }

        _processStartUtc = start;
        return start;
    }

    /// <summary>Обновляет счётчик загрузки CPU на основе разницы значений GetSystemTimes.</summary>
    public static double SampleCpuLoad()
    {
        (ulong idle, ulong kernel, ulong user) = NativeMethods.GetSystemTimes();
        double load;

        lock (SyncRoot)
        {
            if (_lastSampleUtc != DateTime.MinValue)
            {
                ulong idleDelta = idle - _lastIdle;
                ulong totalDelta = (kernel - _lastKernel) + (user - _lastUser);
                if (totalDelta > 0)
                {
                    load = Math.Clamp(100d - idleDelta * 100d / totalDelta, 0d, 100d);
                    _lastCpuLoad = double.IsNaN(_lastCpuLoad) ? load : (_lastCpuLoad * 0.6d + load * 0.4d);
                }
                else
                {
                    load = _lastCpuLoad;
                }
            }
            else
            {
                load = _lastCpuLoad;
            }

            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            _lastSampleUtc = DateTime.UtcNow;
        }

        return load;
    }

    public static Heartbeat CreateHeartbeat(string clientId, DateTime? lastScreenshotUtc, bool screenLocked, int failures, string status)
    {
        (long used, long total) = GetMemory();
        return new Heartbeat
        {
            ClientId = clientId,
            SentAtUtc = DateTime.UtcNow,
            IdleSeconds = IdleSeconds,
            ActiveWindowTitle = Trim(ActiveWindowTitle, 400),
            ActiveProcessName = Trim(ActiveProcessName, 120),
            CpuLoadPercent = Math.Round(CpuLoadPercent, 1),
            UsedMemoryBytes = used,
            TotalMemoryBytes = total,
            UptimeSeconds = GetUptimeSeconds(),
            LastScreenshotAtUtc = lastScreenshotUtc,
            ScreenLocked = screenLocked,
            ScreenshotFailures = failures,
            AgentStatus = status
        };
    }

    private static string Trim(string value, int maxLength) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maxLength ? value : value[..maxLength];
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal sealed class MemoryStatusEx
{
    public uint DwLength;
    public uint DwMemoryLoad;
    public ulong TotalPhys;
    public ulong AvailablePhys;
    public ulong TotalPageFile;
    public ulong AvailablePageFile;
    public ulong TotalVirtual;
    public ulong AvailableVirtual;
    public ulong AvailableExtendedVirtual;

    public MemoryStatusEx() => DwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
}

internal static class NativeMemoryStatus
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    public static void GetMemoryStatusEx(MemoryStatusEx buffer) => GlobalMemoryStatusEx(buffer);
}
