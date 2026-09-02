using Chronicle.API.Helpers;
using Chronicle.Core.Models;
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    // Per-user report (2026-08-31): "Add Media" search showed BOTH the 1987 and 2026 "Masters
    // of the Universe" results as "In Library", and clicking either linked to the same (2026)
    // item -- even though the 1987 result's own ExternalId ("movie:11649") was completely
    // different from the 2026 item's real one ("movie:454639"). Root cause: the search-result
    // library lookup matched a candidate's ExternalId against ANY row with that string,
    // regardless of Source -- a bare ExternalId string is not guaranteed unique across
    // different providers' own ID spaces. LibraryItemResolver requires the row's own Source
    // to match the candidate's Source for both the primary id and every contributing id.
    public class LibraryItemResolverTests
    {
        private static MediaExternalId Row(int mediaItemId, string source, string externalId) =>
            new() { MediaItemId = mediaItemId, Source = source, ExternalId = externalId };

        [Fact]
        public void Resolve_CandidateOwnIdMatchesRowUnderSameSource_ReturnsThatItem()
        {
            var byExternalId = new Dictionary<string, List<MediaExternalId>>(StringComparer.OrdinalIgnoreCase)
            {
                ["movie:454639"] = [Row(mediaItemId: 424572, source: "tmdb", externalId: "movie:454639")],
            };

            var result = LibraryItemResolver.Resolve(byExternalId, "movie:454639", "tmdb", contributing: null);

            result.Should().Be(424572);
        }

        [Fact]
        public void Resolve_IdStringCollidesAcrossDifferentSources_DoesNotMatchTheWrongSource()
        {
            // The exact reported scenario: the 1987 film's own id ("movie:11649") happens to
            // equal the literal string some OTHER source stored against the 2026 item -- must
            // not resolve to it just because the ExternalId string matches.
            var byExternalId = new Dictionary<string, List<MediaExternalId>>(StringComparer.OrdinalIgnoreCase)
            {
                ["movie:11649"] = [Row(mediaItemId: 424572, source: "simkl", externalId: "movie:11649")],
            };

            var result = LibraryItemResolver.Resolve(byExternalId, "movie:11649", "tmdb", contributing: null);

            result.Should().BeNull();
        }

        [Fact]
        public void Resolve_SameIdStringOwnedByBothSources_PicksTheRowMatchingCandidateSource()
        {
            var byExternalId = new Dictionary<string, List<MediaExternalId>>(StringComparer.OrdinalIgnoreCase)
            {
                ["movie:11649"] =
                [
                    Row(mediaItemId: 424572, source: "simkl", externalId: "movie:11649"), // unrelated collision
                    Row(mediaItemId: 999001, source: "tmdb", externalId: "movie:11649"),  // the real 1987 item
                ],
            };

            var result = LibraryItemResolver.Resolve(byExternalId, "movie:11649", "tmdb", contributing: null);

            result.Should().Be(999001);
        }

        [Fact]
        public void Resolve_NoPrimaryMatch_FallsBackToContributingIds()
        {
            var byExternalId = new Dictionary<string, List<MediaExternalId>>(StringComparer.OrdinalIgnoreCase)
            {
                ["imdb:tt0093507"] = [Row(mediaItemId: 555, source: "imdb", externalId: "imdb:tt0093507")],
            };

            var result = LibraryItemResolver.Resolve(
                byExternalId, "movie:11649", "tmdb",
                contributing: [new ContributingExternalId("imdb", "imdb:tt0093507")]);

            result.Should().Be(555);
        }

        [Fact]
        public void Resolve_ContributingIdCollidesAcrossDifferentSources_DoesNotMatchTheWrongSource()
        {
            // Same collision class as the primary-id case, but for a contributing id: a
            // different-source row happens to share the exact string a genuine contributing
            // provider reported. Must not match just because the string lines up.
            var byExternalId = new Dictionary<string, List<MediaExternalId>>(StringComparer.OrdinalIgnoreCase)
            {
                ["imdb:tt0093507"] = [Row(mediaItemId: 424572, source: "simkl", externalId: "imdb:tt0093507")],
            };

            var result = LibraryItemResolver.Resolve(
                byExternalId, "movie:11649", "tmdb",
                contributing: [new ContributingExternalId("imdb", "imdb:tt0093507")]);

            result.Should().BeNull();
        }

        [Fact]
        public void Resolve_SameContributingIdStringOwnedByBothSources_PicksTheRowMatchingItsOwnSource()
        {
            var byExternalId = new Dictionary<string, List<MediaExternalId>>(StringComparer.OrdinalIgnoreCase)
            {
                ["imdb:tt0093507"] =
                [
                    Row(mediaItemId: 424572, source: "simkl", externalId: "imdb:tt0093507"), // unrelated collision
                    Row(mediaItemId: 999001, source: "imdb", externalId: "imdb:tt0093507"),  // the real match
                ],
            };

            var result = LibraryItemResolver.Resolve(
                byExternalId, "movie:11649", "tmdb",
                contributing: [new ContributingExternalId("imdb", "imdb:tt0093507")]);

            result.Should().Be(999001);
        }

        [Fact]
        public void Resolve_NoMatchAnywhere_ReturnsNull()
        {
            var byExternalId = new Dictionary<string, List<MediaExternalId>>(StringComparer.OrdinalIgnoreCase);

            var result = LibraryItemResolver.Resolve(byExternalId, "movie:11649", "tmdb", contributing: null);

            result.Should().BeNull();
        }
    }
}
