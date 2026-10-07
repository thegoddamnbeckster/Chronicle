using System.Text.RegularExpressions;

namespace Chronicle.Services.Security
{
    /// <summary>
    /// What an API key may do. A key used to carry its owner's full privileges, so a leaked Kodi
    /// key owned by an admin could delete users. Scopes narrow a key to the handful of routes its
    /// client really calls, and a scoped key is never treated as an administrator.
    ///
    /// The scopes are defined here, in code, on purpose: they are a security boundary, and one
    /// that an admin could edit from the UI is one a compromised admin session could widen. Each
    /// rule below was taken from the routes the real clients call (Chronicle_Scraper,
    /// Chronicle_Scrobbler, Chronicle_Rating, the Audiobookshelf scrobbler and metadata bridge);
    /// ApiKeyScopesTests pins them.
    /// </summary>
    public static class ApiKeyScopes
    {
        public const string Full = "full";
        public const string Device = "device";
        public const string Bridge = "bridge";

        /// <summary>A key paired from a Kodi box / the scrobbling services.</summary>
        public static readonly string[] All = [Full, Device, Bridge];

        private sealed record Rule(string? Method, Regex Path);

        private static Rule Any(string path) => new(null, Make(path));
        private static Rule Get(string path) => new("GET", Make(path));
        private static Rule Post(string path) => new("POST", Make(path));
        private static Rule Patch(string path) => new("PATCH", Make(path));
        private static Regex Make(string path) =>
            new("^" + path + "/?$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, Rule[]> Rules = new(StringComparer.Ordinal)
        {
            [Device] =
            [
                Any(@"/api/v1/scrobble(/.*)?"),                       // watching/listening events, ratings, resume
                Any(@"/api/v1/scraper(/.*)?"),                        // the Kodi scraper add-ons
                Get(@"/api/v1/users/me"),                             // "who am I" identity check
                Get(@"/api/v1/lists"), Get(@"/api/v1/lists/\d+"),     // list browsing from the scrobbler
                Patch(@"/api/v1/library/\d+"),                        // rating/status from Kodi
                Post(@"/api/v1/library/by-media/\d+/reset-watch-progress"),
            ],
            [Bridge] =
            [
                Get(@"/api/v1/users/me"),
                Get(@"/api/v1/media/types"),
                Get(@"/api/v1/media/search"),
                Get(@"/api/v1/media/\d+"),
                Post(@"/api/v1/media"),
                Post(@"/api/v1/media/\d+/refresh"),
            ],
        };

        public static bool IsKnown(string? scope) => scope is not null && All.Contains(scope, StringComparer.Ordinal);

        /// <summary>True when a key with this scope may make the request. <c>full</c> may do anything
        /// its owner may; an unknown scope may do nothing.</summary>
        public static bool IsAllowed(string? scope, string method, string path)
        {
            if (scope == Full) return true;
            if (scope is null || !Rules.TryGetValue(scope, out var rules)) return false;
            return rules.Any(r =>
                (r.Method is null || string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase))
                && r.Path.IsMatch(path));
        }

        public static string Describe(string scope) => scope switch
        {
            Full   => "Full access - everything the owning account can do. Use only where nothing narrower works.",
            Device => "Kodi / scrobblers - scrobbling, the Kodi scraper, ratings and lists. Cannot administer anything.",
            Bridge => "Audiobookshelf metadata bridge - search, create and refresh media items only.",
            _      => scope,
        };
    }
}
