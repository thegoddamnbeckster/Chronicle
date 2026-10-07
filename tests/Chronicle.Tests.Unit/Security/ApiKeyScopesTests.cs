using Chronicle.Services.Security;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Security
{
    /// <summary>
    /// Pins the scope rules to the routes the real clients call. If a client starts calling a new
    /// route, the matching row here and the rule in ApiKeyScopes must change together; if a rule
    /// is widened by accident, one of the "denied" rows fails.
    /// </summary>
    public class ApiKeyScopesTests
    {
        // ── full ──────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("GET", "/api/v1/users")]
        [InlineData("DELETE", "/api/v1/users/5")]
        [InlineData("POST", "/api/v1/media/5/merge")]
        public void Full_MayDoAnythingItsOwnerMay(string method, string path)
        {
            ApiKeyScopes.IsAllowed("full", method, path).Should().BeTrue();
        }

        // ── device: everything the Kodi add-ons and the ABS scrobbler really call ──

        [Theory]
        [InlineData("POST", "/api/v1/scrobble")]
        [InlineData("GET", "/api/v1/scrobble/active")]
        [InlineData("POST", "/api/v1/scrobble/resume")]
        [InlineData("POST", "/api/v1/scrobble/rating")]
        [InlineData("POST", "/api/v1/scrobble/rating/lookup")]
        [InlineData("GET", "/api/v1/scrobble/summary/12")]
        [InlineData("GET", "/api/v1/scraper/movies/search")]
        [InlineData("GET", "/api/v1/scraper/movies/details")]
        [InlineData("GET", "/api/v1/scraper/movies/details-by-file")]
        [InlineData("GET", "/api/v1/scraper/movies/collections")]
        [InlineData("GET", "/api/v1/scraper/tv/search")]
        [InlineData("GET", "/api/v1/scraper/tv/details")]
        [InlineData("GET", "/api/v1/scraper/tv/episodes")]
        [InlineData("GET", "/api/v1/scraper/tv/episode-details")]
        [InlineData("GET", "/api/v1/scraper/tv/episode-details-by-file")]
        [InlineData("GET", "/api/v1/scraper/tv/resolve-by-external-id")]
        [InlineData("GET", "/api/v1/scraper/scan-active")]
        [InlineData("POST", "/api/v1/scraper/report-kodi-id")]
        [InlineData("GET", "/api/v1/scraper/kodi-scan-signal")]
        [InlineData("POST", "/api/v1/scraper/kodi-scan-signal/ack")]
        [InlineData("GET", "/api/v1/scraper/kodi-refresh-signal")]
        [InlineData("POST", "/api/v1/scraper/devices/kodi/register")]
        [InlineData("GET", "/api/v1/users/me")]
        [InlineData("GET", "/api/v1/lists")]
        [InlineData("GET", "/api/v1/lists/7")]
        [InlineData("PATCH", "/api/v1/library/42")]
        [InlineData("POST", "/api/v1/library/by-media/42/reset-watch-progress")]
        public void Device_MayCallTheRoutesTheKodiAddonsAndScrobblersUse(string method, string path)
        {
            ApiKeyScopes.IsAllowed("device", method, path).Should().BeTrue($"{method} {path} is a real client call");
        }

        [Theory]
        [InlineData("GET", "/api/v1/users")]                       // user administration
        [InlineData("DELETE", "/api/v1/users/5")]
        [InlineData("PUT", "/api/v1/users/5/admin")]
        [InlineData("POST", "/api/v1/tokens")]                      // minting more keys
        [InlineData("GET", "/api/v1/tokens")]
        [InlineData("POST", "/api/v1/auth/device/ABC123/approve")]  // pairing approval
        [InlineData("DELETE", "/api/v1/media/5")]                   // deleting library items
        [InlineData("POST", "/api/v1/media/5/merge")]
        [InlineData("GET", "/api/v1/diagnostics")]
        [InlineData("GET", "/api/v1/plugins")]
        [InlineData("DELETE", "/api/v1/library/42")]                // same path as the allowed PATCH, wrong verb
        [InlineData("POST", "/api/v1/lists")]                       // lists are read-only for a device
        [InlineData("DELETE", "/api/v1/library/all")]
        [InlineData("GET", "/api/v1/library")]
        [InlineData("GET", "/api/v1/scrobbleX")]                    // prefix must end at a path boundary
        [InlineData("GET", "/api/v1/scraperX/movies")]
        public void Device_IsRefusedEverythingElse(string method, string path)
        {
            ApiKeyScopes.IsAllowed("device", method, path).Should().BeFalse($"{method} {path} is outside the device scope");
        }

        // ── bridge: only what the Audiobookshelf metadata provider calls ──────

        [Theory]
        [InlineData("GET", "/api/v1/media/types")]
        [InlineData("GET", "/api/v1/media/search")]
        [InlineData("GET", "/api/v1/media/123")]
        [InlineData("POST", "/api/v1/media")]
        [InlineData("POST", "/api/v1/media/123/refresh")]
        [InlineData("GET", "/api/v1/users/me")]
        public void Bridge_MayCallTheRoutesTheAbsBridgeUses(string method, string path)
        {
            ApiKeyScopes.IsAllowed("bridge", method, path).Should().BeTrue();
        }

        [Theory]
        [InlineData("DELETE", "/api/v1/media/123")]
        [InlineData("PATCH", "/api/v1/media/123")]
        [InlineData("POST", "/api/v1/media/123/merge")]
        [InlineData("POST", "/api/v1/scrobble")]
        [InlineData("GET", "/api/v1/users")]
        [InlineData("GET", "/api/v1/media/123/children")]
        public void Bridge_IsRefusedEverythingElse(string method, string path)
        {
            ApiKeyScopes.IsAllowed("bridge", method, path).Should().BeFalse();
        }

        // ── edge cases ────────────────────────────────────────────────────────

        [Theory]
        [InlineData("/API/V1/SCROBBLE")]
        [InlineData("/api/v1/scrobble/")]
        public void PathMatchingIgnoresCaseAndATrailingSlash(string path)
        {
            ApiKeyScopes.IsAllowed("device", "POST", path).Should().BeTrue();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("admin")]
        [InlineData("FULL")]
        public void UnknownOrMissingScope_MayDoNothing(string? scope)
        {
            ApiKeyScopes.IsAllowed(scope, "GET", "/api/v1/users/me").Should().BeFalse();
            ApiKeyScopes.IsKnown(scope).Should().BeFalse();
        }

        [Fact]
        public void EveryKnownScopeHasADescription()
        {
            foreach (var scope in ApiKeyScopes.All)
                ApiKeyScopes.Describe(scope).Should().NotBeNullOrWhiteSpace().And.NotBe(scope);
        }
    }
}
