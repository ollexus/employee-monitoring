namespace EmployeeMonitoring.Server.Monitoring;

/// <summary>
/// Ограничитель частоты команд, отправляемых одному агенту (скользящее окно в 1 секунду).
/// Защищает канал и агента от лавины команд при ошибках в панели или API.
/// </summary>
public sealed class CommandRateLimiter
{
    private readonly long[] _timestamps;
    private readonly int _limit;
    private readonly int _windowMilliseconds;
    private int _head;

    public CommandRateLimiter(int limit = Protocol.ProtocolConstants.MaxCommandsPerSecond, int windowMilliseconds = 1000)
    {
        _limit = Math.Max(1, limit);
        _windowMilliseconds = Math.Max(100, windowMilliseconds);
        _timestamps = new long[_limit];
    }

    public int Limit => _limit;

    /// <summary>Разрешает ли отправку очередной команды. Повторные вызовы в том же окне могут вернуть false.</summary>
    public bool TryAcquire(long timestampMilliseconds)
    {
        long windowStart = timestampMilliseconds - _windowMilliseconds;
        int used = 0;

        foreach (long value in _timestamps)
        {
            if (value > windowStart)
            {
                used++;
            }
        }

        if (used >= _limit)
        {
            return false;
        }

        _timestamps[_head] = timestampMilliseconds;
        _head = (_head + 1) % _timestamps.Length;
        return true;
    }

    public bool TryAcquire() => TryAcquire(Environment.TickCount64);
}
