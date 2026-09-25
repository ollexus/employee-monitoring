namespace EmployeeMonitoring.Client;

/// <summary>
/// Гарантирует, что агент работает в единственном экземпляре на пользователя.
/// Используется блокировка файла: дескриптор освобождается операционной системой
/// при завершении процесса, в том числе аварийном.
/// </summary>
internal sealed class InstanceLock : IDisposable
{
    private FileStream? _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    public static bool TryAcquire(string directory, TimeSpan timeout, out InstanceLock? instanceLock, out string message)
    {
        instanceLock = null;
        message = string.Empty;

        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "agent.lock");
            DateTime deadline = DateTime.UtcNow + timeout;

            while (true)
            {
                try
                {
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    byte[] payload = System.Text.Encoding.UTF8.GetBytes(
                        $"pid={Environment.ProcessId}; started={DateTime.Now:O}; user={Environment.UserName}{Environment.NewLine}");
                    stream.SetLength(0);
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                    instanceLock = new InstanceLock(stream);
                    return true;
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(300);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // Игнорируем: освобождение дескриптора не критично.
        }
    }
}
