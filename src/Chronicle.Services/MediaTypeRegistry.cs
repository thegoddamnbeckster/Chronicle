using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services
{
    /// <summary>What Chronicle needs to know about one media type, read from <c>media_types</c>.</summary>
    public sealed record MediaTypeInfo(
        int Id, string Name, string? Family, string? ScanStrategy, bool IsPersonType, bool IsDefaultType, bool IsFamilyDefault,
        bool CastAreActors, string[] Aliases, string? ImportShape = null);

    /// <summary>
    /// Everything Chronicle knows about media types comes from the <c>media_types</c> table (edited on the Media Types page,
    /// declared by plugins); no type is named in code. Code asks this registry instead: which type is the people type, which
    /// family of providers serves a type, what a client's "film" or "tv_episode" means, which type is the default.
    ///
    /// Provider matching happens deep in code with no database handle, so the table is held as a small in-memory snapshot:
    /// loaded at startup and reloaded whenever a type is created or edited. A name the snapshot has never seen answers
    /// "nothing known" (no family, not the people type), never a guess from the spelling of the name.
    /// </summary>
    public static class MediaTypeRegistry
    {
        private sealed record Snapshot(
            IReadOnlyDictionary<string, MediaTypeInfo> ByName, IReadOnlyDictionary<string, MediaTypeInfo> ByAlias,
            IReadOnlyDictionary<int, MediaTypeInfo> ById);

        private static volatile Snapshot _snapshot = Build([]);

        // A test can give its own flow of execution its own types without touching what other tests running beside it see.
        private static readonly AsyncLocal<Snapshot?> _scoped = new();
        private static Snapshot Current => _scoped.Value ?? _snapshot;

        private static Snapshot Build(IEnumerable<MediaTypeInfo> types)
        {
            var list = types.ToList();
            var byName = new Dictionary<string, MediaTypeInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in list) byName.TryAdd(t.Name, t);
            var byAlias = new Dictionary<string, MediaTypeInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in list)
                foreach (var alias in t.Aliases)
                    if (!byName.ContainsKey(alias)) byAlias.TryAdd(alias, t);   // a real type's own name always beats an alias
            return new Snapshot(byName, byAlias, list.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First()));
        }

        /// <summary>Replaces the snapshot (used by <see cref="RefreshAsync"/>).</summary>
        public static void Load(IEnumerable<MediaTypeInfo> types) => _snapshot = Build(types);

        /// <summary>Replaces the snapshot for the current flow of execution only (tests run in parallel; each keeps its own types).</summary>
        public static void LoadForCurrentContext(IEnumerable<MediaTypeInfo> types) => _scoped.Value = Build(types);

        public static async Task RefreshAsync(ChronicleDbContext db, CancellationToken ct = default) =>
            Load((await db.MediaTypes.AsNoTracking().ToListAsync(ct)).Select(ToInfo));

        public static MediaTypeInfo ToInfo(MediaType t) => new(
            t.Id, t.Name, t.ProviderFamily, t.ScanStrategy, t.IsPersonType, t.IsDefaultType, t.IsFamilyDefault, t.CastAreActors,
            SplitAliases(t.Aliases), t.ImportShape);

        public static string[] SplitAliases(string? stored) =>
            string.IsNullOrWhiteSpace(stored)
                ? []
                : stored.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(a => a.ToLowerInvariant()).Distinct().ToArray();

        // ── lookups ────────────────────────────────────────────────────────────────

        public static MediaTypeInfo? Find(string? mediaTypeName) =>
            string.IsNullOrWhiteSpace(mediaTypeName) ? null : Current.ByName.GetValueOrDefault(mediaTypeName);

        public static MediaTypeInfo? FindById(int id) => Current.ById.GetValueOrDefault(id);

        /// <summary>The type a client's name stands for: the type of that exact name, else the type that lists the name as an
        /// alias ("film", "tv_episode"). Null when neither exists.</summary>
        public static MediaTypeInfo? FindByNameOrAlias(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return Current.ByName.GetValueOrDefault(name) ?? Current.ByAlias.GetValueOrDefault(name);
        }

        /// <summary>The internal type name a client's name stands for (lowercased and unchanged when it is no known name or alias).</summary>
        public static string Canonical(string name) => FindByNameOrAlias(name)?.Name ?? name.ToLowerInvariant();

        /// <summary>The family of providers that serves this type ("tv", "movie", "music"), or null.</summary>
        public static string? Family(string? mediaTypeName) => Find(mediaTypeName)?.Family;

        /// <summary>The type's name as providers speak it: a family's default type goes by the family name ("movies" is
        /// "movie" to a provider), every other type by its own name.</summary>
        public static string ProviderName(string mediaTypeName)
        {
            var t = Find(mediaTypeName);
            return t is { IsFamilyDefault: true, Family: { Length: > 0 } f } ? f.ToLowerInvariant() : mediaTypeName.ToLowerInvariant();
        }

        /// <summary>The provider-vocabulary name of the type new items get when a client does not say what they are.</summary>
        public static string DefaultProviderName => DefaultType is { } d ? ProviderName(d.Name) : string.Empty;

        /// <summary>Every database type name a provider's declared name may refer to (the declared name itself, and the types
        /// that go by it), lowercase.</summary>
        public static IEnumerable<string> DatabaseNamesFor(string providerName)
        {
            var lower = providerName.ToLowerInvariant();
            return Current.ByName.Values
                .Where(t => ProviderName(t.Name) == lower)
                .Select(t => t.Name.ToLowerInvariant())
                .Append(lower)
                .Distinct();
        }

        /// <summary>True when a provider that declares <paramref name="declaredName"/> serves an item of type <paramref name="itemTypeName"/>.</summary>
        public static bool ProviderDeclaresType(string declaredName, string itemTypeName) =>
            string.Equals(ProviderName(declaredName), ProviderName(itemTypeName), StringComparison.OrdinalIgnoreCase);

        // ── roles ──────────────────────────────────────────────────────────────────

        public static bool IsPersonType(string? mediaTypeName) => Find(mediaTypeName)?.IsPersonType == true;
        public static bool IsPersonTypeId(int mediaTypeId) => FindById(mediaTypeId)?.IsPersonType == true;
        public static MediaTypeInfo? PersonType => Current.ById.Values.FirstOrDefault(t => t.IsPersonType);
        public static MediaTypeInfo? DefaultType => Current.ById.Values.FirstOrDefault(t => t.IsDefaultType);
        public static MediaTypeInfo? FamilyDefault(string family) =>
            Current.ById.Values.FirstOrDefault(t => t.IsFamilyDefault && string.Equals(t.Family, family, StringComparison.OrdinalIgnoreCase));

        /// <summary>True when the type is scanned with the given strategy (<see cref="ScanStrategies"/>).</summary>
        public static bool UsesScanStrategy(string? mediaTypeName, string strategy) =>
            string.Equals(Find(mediaTypeName)?.ScanStrategy, strategy, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the type files imported plays with the given shape (<see cref="ImportShapes"/>).</summary>
        public static bool HasImportShape(string? mediaTypeName, string shape) =>
            string.Equals(Find(mediaTypeName)?.ImportShape, shape, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when a cast entry on this type is an actor playing a character (see <see cref="MediaType.CastAreActors"/>).</summary>
        public static bool CastAreActors(string? mediaTypeName) => Find(mediaTypeName)?.CastAreActors == true;

        // ── provider families (Kodi and provider vocabulary) ─────────────────────────

        /// <summary>A type whose items are each one movie-like file on disk (family "movie").</summary>
        public static bool IsMovieLike(string? mediaTypeName) =>
            string.Equals(Family(mediaTypeName), ProviderFamilies.Movie, StringComparison.OrdinalIgnoreCase);

        /// <summary>A hierarchical show type (family "tv").</summary>
        public static bool IsShowLike(string? mediaTypeName) =>
            string.Equals(Family(mediaTypeName), ProviderFamilies.Tv, StringComparison.OrdinalIgnoreCase);

        /// <summary>Belongs to Kodi's video library at all (movie- or show-like).</summary>
        public static bool IsVideoLibraryType(string? mediaTypeName) => IsMovieLike(mediaTypeName) || IsShowLike(mediaTypeName);

        /// <summary>The names of every type in the given provider family (for queries).</summary>
        public static string[] NamesInFamily(string family) =>
            Current.ById.Values.Where(t => string.Equals(t.Family, family, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name).ToArray();

        public static int[] IdsInFamily(string family) =>
            Current.ById.Values.Where(t => string.Equals(t.Family, family, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Id).ToArray();
    }
}
