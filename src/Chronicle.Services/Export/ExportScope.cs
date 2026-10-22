using System.Text.Json;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services.Export;

/// <summary>
/// What an export covers: which media types hold movies, and which hold shows (their episodes are exported). Read from
/// app_settings so a new movie-like or show-like type is a settings change, not a code change; the defaults are what Chronicle
/// ships with. Fan edits are deliberately not a default: they carry the original film's ids, so exporting a watch of one would
/// mark the original film watched. Which id sources identify an item is the export contract's own (<c>ExportKeys.Sources</c>).
/// </summary>
/// <remarks>Keys: <c>export.movie_media_types</c> and <c>export.show_media_types</c> (each a JSON array of media type names).</remarks>
public sealed record ExportScope(IReadOnlySet<string> MovieTypes, IReadOnlySet<string> ShowTypes)
{
    public const string MovieTypesKey = "export.movie_media_types";
    public const string ShowTypesKey = "export.show_media_types";

    private static readonly string[] DefaultMovieTypes = ["movies", "anime_movies"];
    private static readonly string[] DefaultShowTypes = ["tv", "anime"];

    public static async Task<ExportScope> LoadAsync(ChronicleDbContext db, CancellationToken ct = default)
    {
        var settings = await db.AppSettings.AsNoTracking()
            .Where(a => a.Key == MovieTypesKey || a.Key == ShowTypesKey)
            .ToDictionaryAsync(a => a.Key, a => a.Value, ct);
        return new ExportScope(Read(settings, MovieTypesKey, DefaultMovieTypes), Read(settings, ShowTypesKey, DefaultShowTypes));
    }

    /// <summary>The ids of the media types the scope names, in one query.</summary>
    public async Task<(List<int> Movie, List<int> Show)> TypeIdsAsync(ChronicleDbContext db, CancellationToken ct = default)
    {
        var types = await db.MediaTypes.AsNoTracking().Select(t => new { t.Id, t.Name }).ToListAsync(ct);
        return (types.Where(t => MovieTypes.Contains(t.Name)).Select(t => t.Id).ToList(),
                types.Where(t => ShowTypes.Contains(t.Name)).Select(t => t.Id).ToList());
    }

    private static IReadOnlySet<string> Read(Dictionary<string, string> settings, string key, string[] fallback)
    {
        if (settings.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(raw);
                if (list is { Count: > 0 })
                    return new HashSet<string>(list.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException) { /* a malformed setting falls back to the default rather than exporting nothing */ }
        }
        return new HashSet<string>(fallback, StringComparer.OrdinalIgnoreCase);
    }
}
