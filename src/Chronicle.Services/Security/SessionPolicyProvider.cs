namespace Chronicle.Services.Security
{
    /// <summary>
    /// Session expiry rules from <c>app_settings</c> (<c>auth.session_idle_hours</c>,
    /// <c>auth.session_max_days</c>), read through <see cref="ICachedAppSettings"/> so the
    /// per-request check never touches the database. A missing, unparsable or non-positive
    /// value falls back to the default rather than disabling expiry.
    /// </summary>
    public sealed class SessionPolicyProvider : ISessionPolicyProvider
    {
        public const string IdleHoursKey = "auth.session_idle_hours";
        public const string MaxDaysKey   = "auth.session_max_days";

        private readonly ICachedAppSettings _settings;

        public SessionPolicyProvider(ICachedAppSettings settings) => _settings = settings;

        public SessionPolicy Current => Parse(_settings.Snapshot);

        /// <summary>Pure parsing of the two settings (invariant culture, bounds-checked).</summary>
        public static SessionPolicy Parse(IReadOnlyDictionary<string, string> rows) => new(
            TimeSpan.FromHours(rows.GetPositive(IdleHoursKey, SessionPolicy.Default.IdleTimeout.TotalHours)),
            TimeSpan.FromDays(rows.GetPositive(MaxDaysKey, SessionPolicy.Default.MaxLifetime.TotalDays)));
    }
}
