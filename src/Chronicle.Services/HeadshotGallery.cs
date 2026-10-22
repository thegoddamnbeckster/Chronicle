using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

/// <summary>One picture in a person's Photos gallery, however many stored rows (size variants) back it.</summary>
public sealed record GalleryPhoto(PersonHeadshot Row, bool IsCurrent, bool IsHidden);

/// <summary>
/// The person Photos gallery: collapsing stored headshot rows into distinct pictures, and hiding / restoring a picture.
///
/// Hiding is a flag on the stored rows, not a delete. Headshot rows are insert-only on purpose (re-enrichment
/// re-discovers every provider photo and skips what it has already seen), so deleting a wrong photo - TMDB's own
/// gallery for Stanislav Yanevski contains a photo of an unrelated older man - would only get it re-inserted at the
/// next refresh. A hidden row stays, so it is still "already seen", and it is never shown or chosen as the current
/// photo.
/// </summary>
public static class HeadshotGallery
{
    /// <summary>
    /// One entry per distinct picture. <paramref name="newestFirst"/> must be ordered newest-discovered first; the
    /// representative row is the one currently shown on the person (so its own URL is what the picker pins), else the
    /// newest. A picture is hidden when any of its rows is (hiding marks every row of the picture).
    /// </summary>
    public static List<GalleryPhoto> Collapse(IEnumerable<PersonHeadshot> newestFirst, string? currentPosterUrl)
    {
        var currentKey = HeadshotImageKey.Of(currentPosterUrl);
        var groups = new List<(string Key, List<PersonHeadshot> Rows)>();
        var byKey = new Dictionary<string, List<PersonHeadshot>>();
        foreach (var row in newestFirst)
        {
            var key = HeadshotImageKey.Of(row.Url);
            if (!byKey.TryGetValue(key, out var rows))
            {
                byKey[key] = rows = [];
                groups.Add((key, rows));
            }
            rows.Add(row);
        }

        return groups.Select(g =>
        {
            var isCurrent = currentKey.Length > 0 && g.Key == currentKey;
            var representative = (isCurrent
                ? g.Rows.FirstOrDefault(r => string.Equals(r.Url, currentPosterUrl, StringComparison.Ordinal))
                : null) ?? g.Rows[0];
            return new GalleryPhoto(representative, isCurrent, g.Rows.Any(r => r.IsHidden));
        }).ToList();
    }

    public enum HideOutcome { Hidden, NotFound, IsCurrent }

    /// <summary>
    /// Hides the picture behind <paramref name="headshotId"/> (all of its size variants). The photo currently shown on
    /// the person cannot be hidden - pick another first - so a person is never left pointing at a hidden image.
    /// </summary>
    public static async Task<HideOutcome> HideAsync(
        ChronicleDbContext db, int personId, int headshotId, string? currentPosterUrl, CancellationToken ct = default)
    {
        var rows = await db.PersonHeadshots.Where(h => h.PersonMediaItemId == personId).ToListAsync(ct);
        var target = rows.FirstOrDefault(h => h.Id == headshotId);
        if (target is null) return HideOutcome.NotFound;

        var key = HeadshotImageKey.Of(target.Url);
        if (key.Length > 0 && key == HeadshotImageKey.Of(currentPosterUrl)) return HideOutcome.IsCurrent;

        foreach (var row in rows.Where(h => HeadshotImageKey.Of(h.Url) == key)) row.IsHidden = true;
        await db.SaveChangesAsync(ct);
        return HideOutcome.Hidden;
    }

    /// <summary>Makes every hidden picture of the person visible again; returns how many rows changed.</summary>
    public static async Task<int> RestoreAllAsync(ChronicleDbContext db, int personId, CancellationToken ct = default)
    {
        var hidden = await db.PersonHeadshots.Where(h => h.PersonMediaItemId == personId && h.IsHidden).ToListAsync(ct);
        foreach (var row in hidden) row.IsHidden = false;
        if (hidden.Count > 0) await db.SaveChangesAsync(ct);
        return hidden.Count;
    }
}
