using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>One credited title on the person page's "every credit" view. MediaItemId is set when the
/// title is in the library (the card links to it); otherwise the card is display-only.</summary>
public record PersonFullCredit(
    int? MediaItemId, string Name, string? PosterUrl, int? Year, string MediaTypeName,
    string? CharacterName, string Role);

/// <summary>The credits, plus whether the provider filmography could not be obtained (so the list is only
/// what the library itself holds and the page must say so).</summary>
public record PersonFullCreditsResult(List<PersonFullCredit> Credits, bool Incomplete);

/// <summary>
/// Every credit a person has ever had -- not only the titles Chronicle holds. The provider's own
/// filmography (IMetadataProvider.GetPersonCreditsAsync, keyed by the person's stored provider id) is
/// the source. It is stored in person_provider_credits on first fetch and served from there until it is
/// older than <see cref="MaxAge"/> (a failed refresh falls back to the stored copy). Titles already in the
/// library are linked to their library item; library credits the provider does not list are kept too, so
/// this view is always a superset of the default one. No cap on the number of credits.
/// </summary>
public sealed class PersonFullCreditsService(IPluginRegistry registry, ILogger<PersonFullCreditsService> logger)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public async Task<PersonFullCreditsResult> GetAsync(ChronicleDbContext db, int personId, CancellationToken ct)
    {
        var (provided, incomplete) = await GetProviderCreditsAsync(db, personId, ct);
        var result = new List<PersonFullCredit>();

        // Library items matching the provider's title ids -- matched within the provider's own source, and
        // deterministically (lowest id) when duplicates/stubs share an id.
        var libraryByKey = new Dictionary<(string Source, string ExternalId), (int Id, string Name, string? Poster, int? Year, string Type)>();
        foreach (var bySource in provided.GroupBy(c => c.Source))
        {
            var source = bySource.Key;
            foreach (var chunk in bySource.Select(c => c.ExternalId).Distinct().Chunk(500))
            {
                var rows = await db.MediaExternalIds
                    .Where(x => x.Source == source && chunk.Contains(x.ExternalId))
                    .OrderBy(x => x.MediaItem!.IsStub).ThenBy(x => x.MediaItemId)
                    .Select(x => new
                    {
                        x.ExternalId, x.MediaItem!.Id, x.MediaItem.Name, x.MediaItem.PosterUrl, x.MediaItem.Year,
                        Type = x.MediaItem.MediaType!.Name,
                    })
                    .ToListAsync(ct);
                foreach (var r in rows)
                    libraryByKey.TryAdd((source, r.ExternalId), (r.Id, r.Name, r.PosterUrl, r.Year, r.Type));
            }
        }

        var seen = new HashSet<(int, string)>();
        foreach (var c in provided)
        {
            if (libraryByKey.TryGetValue((c.Source, c.ExternalId), out var lib))
            {
                seen.Add((lib.Id, NormalizeRole(c.Role)));
                result.Add(new PersonFullCredit(
                    lib.Id, lib.Name, lib.Poster ?? c.PosterUrl, lib.Year ?? c.Year, lib.Type, c.CharacterName, c.Role));
            }
            else
            {
                result.Add(new PersonFullCredit(
                    null, c.Title, c.PosterUrl, c.Year, c.MediaType == "tv" ? "tv" : "movies", c.CharacterName, c.Role));
            }
        }

        // Library credits the provider did not list (other sources, or a person with no provider id).
        var own = await db.MediaCredits
            .Where(c => c.PersonMediaItemId == personId)
            .Select(c => new
            {
                c.MediaItemId, c.Role, c.CharacterName, c.MediaItem.Name, c.MediaItem.PosterUrl, c.MediaItem.Year,
                Type = c.MediaItem.MediaType!.Name,
            })
            .ToListAsync(ct);
        foreach (var c in own)
        {
            // "Cast" and "Actor" are the same role spelled two ways across sources.
            var role = c.Role == "Cast" ? "Actor" : c.Role;
            if (!seen.Add((c.MediaItemId, NormalizeRole(role)))) continue;
            result.Add(new PersonFullCredit(c.MediaItemId, c.Name, c.PosterUrl, c.Year, c.Type, c.CharacterName, role));
        }

        return new PersonFullCreditsResult(result, incomplete);
    }

    /// <summary>Roles compare ignoring case and surrounding whitespace ("Executive producer" is "Executive Producer").</summary>
    private static string NormalizeRole(string role) => role.Trim().ToLowerInvariant();

    /// <summary>Stored filmography when fresh; otherwise fetched from the person's provider and stored. A
    /// failed fetch with nothing stored is reported as incomplete; with a stale copy stored, the stale copy is used.</summary>
    private async Task<(List<PersonProviderCredit> Credits, bool Incomplete)> GetProviderCreditsAsync(
        ChronicleDbContext db, int personId, CancellationToken ct)
    {
        var stored = await db.PersonProviderCredits.Where(c => c.PersonMediaItemId == personId).ToListAsync(ct);
        if (stored.Count > 0 && DateTime.UtcNow - stored.Max(c => c.FetchedAt) < MaxAge)
            return (stored, false);

        var enrichments = await db.MediaEnrichments
            .Where(e => e.MediaItemId == personId && e.ExternalId != null)
            .Select(e => new { e.PluginId, e.ExternalId })
            .ToListAsync(ct);

        var failed = false;
        foreach (var e in enrichments)
        {
            var provider = registry.GetMetadataProvider(e.PluginId);
            if (provider is null) continue;
            try
            {
                var credits = await provider.GetPersonCreditsAsync(e.ExternalId!, ct);
                if (credits.Count == 0) continue;

                var now = DateTime.UtcNow;
                var fresh = credits.Select(c => new PersonProviderCredit
                {
                    PersonMediaItemId = personId, Source = c.Source, ExternalId = c.ExternalId, MediaType = c.MediaType,
                    Title = c.Title, Year = c.Year, PosterUrl = c.PosterUrl, Role = c.Role,
                    CharacterName = c.CharacterName, FetchedAt = now,
                }).ToList();
                db.PersonProviderCredits.RemoveRange(stored);
                db.PersonProviderCredits.AddRange(fresh);
                await db.SaveChangesAsync(ct);
                return (fresh, false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed = true;
                logger.LogWarning(ex, "Full credits: provider {Plugin} failed for person {PersonId}", e.PluginId, personId);
            }
        }

        // Nothing fresh: a stale stored copy still beats nothing; with none at all, say the list is partial.
        return (stored, failed && stored.Count == 0);
    }

    /// <summary>Groups credits by role (alphabetical), each group newest first. No limit.</summary>
    public static List<(string Role, List<PersonFullCredit> Items)> GroupByRole(IEnumerable<PersonFullCredit> credits) =>
        credits
            .GroupBy(c => c.Role)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.OrderByDescending(c => c.Year ?? int.MinValue).ToList()))
            .ToList();
}
