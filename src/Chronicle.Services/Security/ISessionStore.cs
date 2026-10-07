namespace Chronicle.Services.Security
{
    /// <summary>One signed-in browser session. Never carries the key itself.</summary>
    public sealed record SessionInfo(
        Guid SessionId,
        int UserId,
        DateTime CreatedAt,
        DateTime LastSeenAt,
        DateTime AbsoluteExpiresAt,
        string? UserAgent,
        string? RemoteIp);

    /// <summary>Why a presented session key was not accepted.</summary>
    public enum SessionRejection { None, Unknown, IdleExpired, LifetimeExpired }

    public sealed record SessionValidation(SessionInfo? Session, SessionRejection Rejection)
    {
        public bool IsValid => Session is not null;
    }

    /// <summary>Expiry rules for new and existing sessions.</summary>
    public sealed record SessionPolicy(TimeSpan IdleTimeout, TimeSpan MaxLifetime)
    {
        public static readonly SessionPolicy Default = new(TimeSpan.FromHours(24), TimeSpan.FromDays(30));
    }

    public interface ISessionPolicyProvider
    {
        SessionPolicy Current { get; }
    }

    /// <summary>
    /// Server-side registry of live web sessions. Memory-only on purpose: nothing is persisted,
    /// so any restart of the API (deploy, crash, container restart, database restore) drops
    /// every session and everyone signs in again. See docs/plans/2026-10-07-session-keys-design.md.
    /// </summary>
    public interface ISessionStore
    {
        /// <summary>Creates a session for an already-authenticated user and returns the raw key.
        /// The key is shown once; only its hash is kept.</summary>
        (string Key, SessionInfo Session) Create(int userId, string? userAgent, string? remoteIp);

        /// <summary>Looks the key up. When <paramref name="touch"/> is true a valid session's
        /// idle clock restarts; background polling passes false so an abandoned tab can expire.</summary>
        SessionValidation Validate(string key, bool touch);

        SessionInfo? Get(Guid sessionId);
        IReadOnlyList<SessionInfo> ListForUser(int userId);

        bool Revoke(Guid sessionId);
        bool RevokeByKey(string key);

        /// <summary>Ends all of a user's sessions, optionally sparing one. Returns how many ended.</summary>
        int RevokeAllForUser(int userId, Guid? exceptSessionId = null);

        /// <summary>Drops expired entries. Returns how many were removed.</summary>
        int Sweep();

        int Count { get; }
    }
}
