using Chronicle.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Data;

/// <summary>
/// The starting media types a new Chronicle database is given, and the one place their names are written down. They are
/// ordinary rows afterwards: every rule that depends on them (which type holds people, which is the default, what a client's
/// "film" means) is read from the row's flags and aliases, so renaming or editing one on the Media Types page changes behaviour
/// with no code involved.
/// </summary>
public static class BuiltInMediaTypes
{
    private static readonly DateTime Seeded = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>TV, movies and music: seeded by the model (see ChronicleDbContext).</summary>
    public static MediaType[] Seed() =>
    [
        new MediaType
        {
            Id = 1, Name = "tv", DisplayName = "TV Shows", Description = "Television series, seasons, and episodes",
            HierarchyLevels = 3, HierarchyLabels = "Show,Season,Episode", InteractionVerb = "watched", ProgressUnit = "minutes",
            ProviderFamily = ProviderFamilies.Tv, IsFamilyDefault = true, CastAreActors = true,
            Aliases = "tv_show,tv_episode,show,episode", ImportShape = ImportShapes.Episodic,
            IsBuiltIn = true, IsActive = true, CreatedAt = Seeded,
        },
        new MediaType
        {
            Id = 2, Name = "movies", DisplayName = "Movies", Description = "Feature films and short films",
            HierarchyLevels = 1, HierarchyLabels = "Movie", InteractionVerb = "watched", ProgressUnit = "minutes",
            ProviderFamily = ProviderFamilies.Movie, IsFamilyDefault = true, IsDefaultType = true, CastAreActors = true,
            Aliases = "movie,film",
            IsBuiltIn = true, IsActive = true, CreatedAt = Seeded,
        },
        new MediaType
        {
            Id = 3, Name = "music", DisplayName = "Music", Description = "Artists, albums, and tracks",
            HierarchyLevels = 3, HierarchyLabels = "Artist,Album,Track", InteractionVerb = "listened", ProgressUnit = "tracks",
            ProviderFamily = ProviderFamilies.Music, IsFamilyDefault = true, CastHeading = "Band Members",
            Aliases = "track,song", ImportShape = ImportShapes.ArtistAlbumTrack,
            IsBuiltIn = true, IsActive = true, CreatedAt = Seeded,
        },
    ];

    /// <summary>
    /// The reference type whose items are the people credited on other media. No plugin owns registering it (credits come from
    /// whichever provider is enriching the title), so it is created here the first time anyone is credited. A type already
    /// marked as the people type is used as it is; an unmarked type that carries the starting name is adopted.
    /// </summary>
    public static async Task<int> EnsurePersonTypeAsync(ChronicleDbContext db, CancellationToken ct = default)
    {
        var id = await db.MediaTypes.Where(t => t.IsPersonType).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
        if (id is not null) return id.Value;

        var existing = await db.MediaTypes.FirstOrDefaultAsync(t => t.Name == PersonTypeName, ct);
        if (existing is not null)
        {
            existing.IsPersonType = true;
            existing.IsTrackable = false;
            await db.SaveChangesAsync(ct);
            return existing.Id;
        }

        var created = new MediaType
        {
            Name = PersonTypeName, DisplayName = "People", HierarchyLevels = 1, InteractionVerb = "viewed", ProgressUnit = "percent",
            IsBuiltIn = true, IsActive = true, IsTrackable = false, IsPersonType = true, CreatedAt = DateTime.UtcNow,
        };
        db.MediaTypes.Add(created);
        try
        {
            await db.SaveChangesAsync(ct);
            return created.Id;
        }
        catch (DbUpdateException)
        {
            // Lost a race with another concurrent credit resolution creating the same row: use whichever one won.
            db.ChangeTracker.Clear();
            return await db.MediaTypes.Where(t => t.Name == PersonTypeName || t.IsPersonType)
                .OrderByDescending(t => t.IsPersonType).Select(t => t.Id).FirstAsync(ct);
        }
    }

    /// <summary>The starting name of the people type (see <see cref="EnsurePersonTypeAsync"/>).</summary>
    public const string PersonTypeName = "people";
}
