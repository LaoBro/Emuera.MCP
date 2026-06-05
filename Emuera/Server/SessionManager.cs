using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace MinorShift.Emuera.Server;

internal sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _idleTimeout;

    public SessionManager(TimeSpan? idleTimeout = null)
    {
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(30);
        _cleanupTimer = new Timer(CleanupIdleSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public Session Create(SessionIO io)
    {
        var session = new Session(io);
        if (!_sessions.TryAdd(session.Id, session))
            throw new InvalidOperationException("Session ID collision");
        session.Start();
        return session;
    }

    public Session? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    public bool Remove(string id)
    {
        if (_sessions.TryRemove(id, out var s))
        {
            s.Dispose();
            return true;
        }
        return false;
    }

    private void CleanupIdleSessions(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow - _idleTimeout;
        foreach (var kv in _sessions.Where(kv => kv.Value.LastActivityAt < cutoff).ToList())
        {
            Remove(kv.Key);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var id in _sessions.Keys.ToList())
            Remove(id);
    }
}
