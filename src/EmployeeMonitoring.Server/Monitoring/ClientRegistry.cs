using System.Collections.Concurrent;

namespace EmployeeMonitoring.Server.Monitoring;

/// <summary>Реестр известных агентов. Записи сохраняются и после отключения, чтобы оператор видел историю.</summary>
public sealed class ClientRegistry
{
    private readonly ConcurrentDictionary<string, ClientSession> _clients = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _clients.Count;

    public void Register(ClientSession session) => _clients[session.ClientId] = session;

    public ClientSession? Find(string clientId) =>
        _clients.TryGetValue(clientId, out ClientSession? session) ? session : null;

    public bool Remove(string clientId) => _clients.TryRemove(clientId, out _);

    public IReadOnlyList<ClientSnapshot> Snapshots() =>
        _clients.Values
            .Select(session => session.ToSnapshot())
            .OrderBy(snapshot => snapshot.MachineName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(snapshot => snapshot.UserName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
