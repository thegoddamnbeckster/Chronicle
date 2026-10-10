using System.Collections.Concurrent;

namespace Chronicle.Services.Security
{
    public readonly record struct ThrottleDecision(bool Allowed, TimeSpan RetryAfter)
    {
        public static readonly ThrottleDecision Ok = new(true, TimeSpan.Zero);
    }

    /// <summary>
    /// Slows password guessing and mass account creation. Memory-only like the session store, so
    /// a restart clears every counter (which also gives an admin who is locked out a way back in).
    /// Failures are counted three ways, because each alone has a hole:
    /// <list type="bullet">
    /// <item>per (address, username) - the normal "someone is guessing this account" case;</item>
    /// <item>per address - one machine trying many usernames;</item>
    /// <item>per username across all addresses - an attacker rotating addresses (and, while
    /// forwarded headers are trusted from anyone, spoofing them). Deliberately the most generous
    /// limit, since it is the one an attacker can use to lock the real user out.</item>
    /// </list>
    /// A correct password clears that user's counters. Limits come from <c>app_settings</c>
    /// (<c>auth.login_*</c>, <c>auth.register_*</c>) and apply within about a minute.
    /// </summary>
    public interface ILoginThrottle
    {
        /// <summary>Call BEFORE checking the password; a blocked caller is refused even with the right one.</summary>
        ThrottleDecision CheckLogin(string? address, string? username);
        void RecordLoginFailure(string? address, string? username);
        void RecordLoginSuccess(string? address, string? username);

        ThrottleDecision CheckRegistration(string? address);
        void RecordRegistration(string? address);

        /// <summary>Generic per-address limiter for other unauthenticated entry points (device pairing).</summary>
        ThrottleDecision CheckAction(string action, string? address, int maxPerWindow, TimeSpan window);
        void RecordAction(string action, string? address);
    }

    public sealed class LoginThrottle : ILoginThrottle
    {
        public const string MaxPerAddressAndUserKey = "auth.login_max_failures_per_address_user";
        public const string MaxPerAddressKey        = "auth.login_max_failures_per_address";
        public const string MaxPerUserKey           = "auth.login_max_failures_per_user";
        public const string WindowMinutesKey        = "auth.login_window_minutes";
        public const string RegisterMaxKey          = "auth.register_max_per_address_hour";

        // Stops a flood of random usernames from growing the table without limit.
        private const int MaxTrackedKeys = 100_000;

        private readonly ICachedAppSettings _settings;
        private readonly TimeProvider _clock;
        private readonly ConcurrentDictionary<string, Queue<DateTime>> _events = new();
        private readonly Lock _gate = new();
        private DateTime _lastSweep;

        public LoginThrottle(ICachedAppSettings settings, TimeProvider? clock = null)
        {
            _settings = settings;
            _clock = clock ?? TimeProvider.System;
        }

        private static string Norm(string? v) => (v ?? string.Empty).Trim().ToLowerInvariant();
        private static string AddrUser(string? a, string? u) => $"lf:au:{Norm(a)}|{Norm(u)}";
        private static string Addr(string? a) => $"lf:a:{Norm(a)}";
        private static string User(string? u) => $"lf:u:{Norm(u)}";

        private TimeSpan Window => TimeSpan.FromMinutes(_settings.Snapshot.GetPositive(WindowMinutesKey, 15));

        public ThrottleDecision CheckLogin(string? address, string? username)
        {
            var s = _settings.Snapshot;
            var window = Window;
            return Worst(
                Check(AddrUser(address, username), (int)s.GetPositive(MaxPerAddressAndUserKey, 5), window),
                Check(Addr(address),               (int)s.GetPositive(MaxPerAddressKey, 20), window),
                Check(User(username),              (int)s.GetPositive(MaxPerUserKey, 30), window));
        }

        public void RecordLoginFailure(string? address, string? username)
        {
            Add(AddrUser(address, username));
            Add(Addr(address));
            Add(User(username));
        }

        public void RecordLoginSuccess(string? address, string? username)
        {
            // The (address, user) and user counters describe guessing at THIS account, which the
            // right password just ended. The address counter stays: it may be guessing others.
            _events.TryRemove(AddrUser(address, username), out _);
            _events.TryRemove(User(username), out _);
        }

        public ThrottleDecision CheckRegistration(string? address) =>
            Check($"reg:{Norm(address)}", (int)_settings.Snapshot.GetPositive(RegisterMaxKey, 10), TimeSpan.FromHours(1));

        public void RecordRegistration(string? address) => Add($"reg:{Norm(address)}");

        public ThrottleDecision CheckAction(string action, string? address, int maxPerWindow, TimeSpan window) =>
            Check($"act:{action}:{Norm(address)}", maxPerWindow, window);

        public void RecordAction(string action, string? address) => Add($"act:{action}:{Norm(address)}");

        // ── internals ─────────────────────────────────────────────────────────

        private static ThrottleDecision Worst(params ThrottleDecision[] decisions)
        {
            var blocked = decisions.Where(d => !d.Allowed).ToList();
            // (FirstOrDefault on a struct would hand back a default value whose Allowed is false.)
            return blocked.Count == 0 ? ThrottleDecision.Ok : blocked.OrderByDescending(d => d.RetryAfter).First();
        }

        private ThrottleDecision Check(string key, int max, TimeSpan window)
        {
            if (!_events.TryGetValue(key, out var q)) return ThrottleDecision.Ok;
            var now = _clock.GetUtcNow().UtcDateTime;
            lock (q)
            {
                Prune(q, now, window);
                if (q.Count < max) return ThrottleDecision.Ok;
                // Free again once enough of the oldest events age out of the window.
                var oldestThatMatters = q.ElementAt(q.Count - max);
                var retry = oldestThatMatters + window - now;
                return new ThrottleDecision(false, retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(1));
            }
        }

        private void Add(string key)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            SweepIfDue(now);
            if (_events.Count >= MaxTrackedKeys && !_events.ContainsKey(key)) return;
            var q = _events.GetOrAdd(key, _ => new Queue<DateTime>());
            lock (q) q.Enqueue(now);
        }

        private static void Prune(Queue<DateTime> q, DateTime now, TimeSpan window)
        {
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
        }

        private void SweepIfDue(DateTime now)
        {
            lock (_gate)
            {
                if (now - _lastSweep < TimeSpan.FromMinutes(5)) return;
                _lastSweep = now;
            }
            // The longest window anything here uses is the one hour of registration.
            var longest = TimeSpan.FromHours(1);
            foreach (var (key, q) in _events)
            {
                lock (q)
                {
                    Prune(q, now, longest);
                    if (q.Count == 0) _events.TryRemove(key, out _);
                }
            }
        }
    }
}
