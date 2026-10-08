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

        /// <summary>Replaces the snapshot (used by <see cref="RefreshAsync"/> and by tests).</summary>
        public static void Load(IEnumerable<(string Name, string? Family)> types) =>
            _snapshot = types.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Family, StringComparer.OrdinalIgnoreCase);

        public static async Task RefreshAsync(ChronicleDbContext db, CancellationToken ct = default) =>
            Load((await db.MediaTypes.AsNoTracking().Select(t => new { t.Name, t.ProviderFamily }).ToListAsync(ct))
                .Select(t => (t.Name, t.ProviderFamily)));
    }
}
