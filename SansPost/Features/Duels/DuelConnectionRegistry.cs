using System.Security.Cryptography;

namespace SansPost.Features.Duels
{
    // Kto jest przy stole gry: UserId → połączenia DuelHub (karta, druga karta, ponowne połączenie). Użytkownik jest online,
    // dopóki ma choć jedno połączenie — zamknięcie jednej karty nie wylogowuje go ze stołu. Jedna instancja aplikacji
    // (bez backplane); jedna blokada wystarcza — operacje są krótkie, bez I/O.
    // Handle — losowy, nieprzezroczysty identyfikator gracza dla innych klientów (zamiast UserId); znika, gdy gracz odchodzi.
    public sealed class DuelConnectionRegistry
    {
        private sealed record Entry(string Alias, string Handle, HashSet<string> Connections);

        private readonly object _gate = new();
        private readonly Dictionary<int, Entry> _users = new();
        private readonly Dictionary<string, int> _connections = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _handles = new(StringComparer.Ordinal);

        // true = pierwsze połączenie użytkownika (właśnie pojawił się przy stole).
        public bool Add(int userId, string alias, string connectionId)
        {
            lock (_gate)
            {
                if (_connections.ContainsKey(connectionId))
                    return false;
                _connections[connectionId] = userId;
                if (_users.TryGetValue(userId, out var entry))
                {
                    entry.Connections.Add(connectionId);
                    return false;
                }

                var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
                _users[userId] = new Entry(alias, handle, new HashSet<string>(StringComparer.Ordinal) { connectionId });
                _handles[handle] = userId;
                return true;
            }
        }

        // UserId, jeśli to było ostatnie połączenie użytkownika (odszedł od stołu); inaczej null.
        public int? Remove(string connectionId)
        {
            lock (_gate)
            {
                if (!_connections.Remove(connectionId, out var userId) || !_users.TryGetValue(userId, out var entry))
                    return null;
                entry.Connections.Remove(connectionId);
                if (entry.Connections.Count > 0)
                    return null;
                _users.Remove(userId);
                _handles.Remove(entry.Handle);
                return userId;
            }
        }

        public bool IsOnline(int userId)
        {
            lock (_gate)
                return _users.ContainsKey(userId);
        }

        public int? UserOf(string? handle)
        {
            if (string.IsNullOrEmpty(handle))
                return null;
            lock (_gate)
                return _handles.TryGetValue(handle, out var userId) ? userId : null;
        }

        public string? AliasOf(int userId)
        {
            lock (_gate)
                return _users.TryGetValue(userId, out var entry) ? entry.Alias : null;
        }

        public IReadOnlyList<(int UserId, string Alias, string Handle)> Online()
        {
            lock (_gate)
                return _users.Select(u => (u.Key, u.Value.Alias, u.Value.Handle)).ToList();
        }

        public int UserCount
        {
            get { lock (_gate) return _users.Count; }
        }

        public int ConnectionCount
        {
            get { lock (_gate) return _connections.Count; }
        }
    }
}
