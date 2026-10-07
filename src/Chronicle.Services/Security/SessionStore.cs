using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Chronicle.Services.Security
{
    public sealed class SessionStore : ISessionStore
    {
        /// <summary>Prefix that marks a session key, so it is recognisable in a secret scanner and
        /// can never be mistaken for an API key (<c>chr_live_</c>).</summary>
        public const string KeyPrefix = "chr_sess_";

        /// <summary>Live sessions allowed per user; creating one more ends the least recently seen.</summary>
        public const int MaxSessionsPerUser = 20;

        private sealed class Entry
        {
            public required Guid SessionId { get; init; }
            public required string KeyHash { get; init; }
            public required int UserId { get; init; }
            public required DateTime CreatedAt { get; init; }
            public required DateTime AbsoluteExpiresAt { get; init; }
            public required string? UserAgent { get; init; }
            public required string? RemoteIp { get; init; }
            public DateTime LastSeenAt { get; set; }

            public SessionInfo ToInfo() =>
                new(SessionId, UserId, CreatedAt, LastSeenAt, AbsoluteExpiresAt, UserAgent, RemoteIp);
        }

        private readonly ConcurrentDictionary<string, Entry> _byHash = new(StringComparer.Ordinal);
        private readonly ISessionPolicyProvider _policy;
        private readonly TimeProvider _clock;
        private readonly Lock _writeLock = new();

        public SessionStore(ISessionPolicyProvider policy, TimeProvider? clock = null)
        {
            _policy = policy;
            _clock = clock ?? TimeProvider.System;
        }

        public int Count => _byHash.Count;

        public (string Key, SessionInfo Session) Create(int userId, string? userAgent, string? remoteIp)
        {
            var key = KeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
            var now = _clock.GetUtcNow().UtcDateTime;
            var policy = _policy.Current;

            var entry = new Entry
            {
                SessionId = Guid.NewGuid(),
                KeyHash = Hash(key),
                UserId = userId,
                CreatedAt = now,
                LastSeenAt = now,
                AbsoluteExpiresAt = now + policy.MaxLifetime,
                UserAgent = Truncate(userAgent, 200),
                RemoteIp = Truncate(remoteIp, 64),
            };

            lock (_writeLock)
            {
                var mine = _byHash.Values.Where(e => e.UserId == userId).OrderBy(e => e.LastSeenAt).ToList();
                // Make room for the one being added.
                for (var i = 0; i <= mine.Count - MaxSessionsPerUser; i++)
                    _byHash.TryRemove(mine[i].KeyHash, out _);
                _byHash[entry.KeyHash] = entry;
            }

            return (key, entry.ToInfo());
        }

        public SessionValidation Validate(string key, bool touch)
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                return new SessionValidation(null, SessionRejection.Unknown);

            var hash = Hash(key);
            if (!_byHash.TryGetValue(hash, out var entry))
                return new SessionValidation(null, SessionRejection.Unknown);

            var now = _clock.GetUtcNow().UtcDateTime;
            var policy = _policy.Current;

            if (now >= entry.AbsoluteExpiresAt)
            {
                _byHash.TryRemove(hash, out _);
                return new SessionValidation(null, SessionRejection.LifetimeExpired);
            }
            if (now - entry.LastSeenAt >= policy.IdleTimeout)
            {
                _byHash.TryRemove(hash, out _);
                return new SessionValidation(null, SessionRejection.IdleExpired);
            }

            if (touch) entry.LastSeenAt = now;
            return new SessionValidation(entry.ToInfo(), SessionRejection.None);
        }

        public SessionInfo? Get(Guid sessionId) =>
            _byHash.Values.FirstOrDefault(e => e.SessionId == sessionId)?.ToInfo();

        public IReadOnlyList<SessionInfo> ListForUser(int userId) =>
            _byHash.Values.Where(e => e.UserId == userId)
                .OrderByDescending(e => e.LastSeenAt)
                .Select(e => e.ToInfo())
                .ToList();

        public bool Revoke(Guid sessionId)
        {
            var entry = _byHash.Values.FirstOrDefault(e => e.SessionId == sessionId);
            return entry is not null && _byHash.TryRemove(entry.KeyHash, out _);
        }

        public bool RevokeByKey(string key) =>
            !string.IsNullOrEmpty(key) && _byHash.TryRemove(Hash(key), out _);

        public int RevokeAllForUser(int userId, Guid? exceptSessionId = null)
        {
            var removed = 0;
            foreach (var e in _byHash.Values.Where(e => e.UserId == userId && e.SessionId != exceptSessionId))
                if (_byHash.TryRemove(e.KeyHash, out _)) removed++;
            return removed;
        }

        public int Sweep()
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var idle = _policy.Current.IdleTimeout;
            var removed = 0;
            foreach (var e in _byHash.Values.Where(e => now >= e.AbsoluteExpiresAt || now - e.LastSeenAt >= idle))
                if (_byHash.TryRemove(e.KeyHash, out _)) removed++;
            return removed;
        }

        /// <summary>SHA-256 is enough: the key carries 256 bits of entropy, so there is nothing to
        /// brute-force and a slow hash would only cost every request.</summary>
        internal static string Hash(string key) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

        /// <summary>Short, non-reversible tag for correlating log lines about one key without logging it.</summary>
        public static string LogTag(string key) => Hash(key)[..8].ToLowerInvariant();

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string? Truncate(string? value, int max) =>
            value is null ? null : (value.Length <= max ? value : value[..max]);
    }
}
