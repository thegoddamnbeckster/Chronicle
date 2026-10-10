using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services
{
    /// <summary>
    /// Which family of metadata providers also serves a media type ("anime" -> "tv"), read from
    /// <c>media_types.ProviderFamily</c> (editable on the Media Types page) rather than guessed from the type's name.
    /// Provider matching happens deep in code with no database handle, so the table is held as a small in-memory
    /// snapshot: loaded at startup and reloaded whenever a type is created or edited.
    /// A name the snapshot has never seen (a test, a type created a moment ago) falls back to the same default a
    /// new type is given, so behaviour never silently changes.
    /// </summary>
    public static class MediaTypeFamilies
    {
        private static volatile IReadOnlyDictionary<string, string?> _snapshot =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The family for this type name: the stored value when the type is known (null meaning "none"),
        /// otherwise the default for that name.</summary>
        public static string? Resolve(string? mediaTypeName)
        {
            if (string.IsNullOrWhiteSpace(mediaTypeName)) return null;
            return _snapshot.TryGetValue(mediaTypeName, out var stored) ? stored : ProviderFamilies.DefaultFor(mediaTypeName);
        }

        public const string Movie = "movie";
        public const string Tv = "tv";

        /// <summary>A flat type whose items are each one movie-like file on disk (family "movie"): Kodi's movie path, movie
        /// collections, collection grouping. Decided by the type's provider family, so a renamed or user-made type behaves
        /// like the built-in one.</summary>
        public static bool IsMovieLike(string? mediaTypeName) =>
            string.Equals(Resolve(mediaTypeName), Movie, StringComparison.OrdinalIgnoreCase);

        /// <summary>A hierarchical show type (family "tv") scraped through Kodi's TV path.</summary>
        public static bool IsShowLike(string? mediaTypeName) =>
            string.Equals(Resolve(mediaTypeName), Tv, StringComparison.OrdinalIgnoreCase);

        /// <summary>Belongs to Kodi's video library at all (movie- or show-like).</summary>
        public static bool IsVideoLibraryType(string? mediaTypeName) => IsMovieLike(mediaTypeName) || IsShowLike(mediaTypeName);

        /// <summary>Replaces the snapshot (used by <see cref="RefreshAsync"/> and by tests).</summary>
        public static void Load(IEnumerable<(string Name, string? Family)> types) =>
            _snapshot = types.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Family, StringComparer.OrdinalIgnoreCase);

        public static async Task RefreshAsync(ChronicleDbContext db, CancellationToken ct = default) =>
            Load((await db.MediaTypes.AsNoTracking().Select(t => new { t.Name, t.ProviderFamily }).ToListAsync(ct))
                .Select(t => (t.Name, t.ProviderFamily)));
    }
}
