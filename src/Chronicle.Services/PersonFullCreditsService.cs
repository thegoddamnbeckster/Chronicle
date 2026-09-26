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

/// <summary>
/// Every credit a person has ever had -- not only the titles Chronicle holds. The provider's own
/// filmography (IMetadataProvider.GetPersonCreditsAsync, keyed by the person's stored provider id) is
/// the source, with titles already in the library linked to their library item; library credits the
/// provider does not list are kept too, so the "every credit" view is always a superset of the default
/// one. No cap on the number of credits.
/// </summary>
public sealed class PersonFullCreditsService(IPluginRegistry registry, ILogger<PersonFullCreditsService> logger)
{
    public async Task<List<PersonFullCredit>> GetAsync(ChronicleDbContext db, int personId, CancellationToken ct)
    {
        var result = new List<PersonFullCredit>();

        var enrichments = await db.MediaEnrichments
            .Where(e => e.MediaItemId == personId && e.ExternalId != null)
            .Select(e => new { e.PluginId, e.ExternalId })
            .ToListAsync(ct);

        var provided = new List<ProviderPersonCredit>();
        foreach (var e in enrichments)
        {
            var provider = registry.GetMetadataProvider(e.PluginId);
            if (provider is null) continue;
            try
            {
                var credits = await provider.GetPersonCreditsAsync(e.ExternalId!, ct);
                if (credits.Count > 0) { provided.AddRange(credits); break; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Full credits: provider {Plugin} failed for person {PersonId}", e.PluginId, personId);
            }
        }

        // Library items matching the provider's title ids.
        var ids = provided.Select(c => c.ExternalId).Distinct().ToList();
        var libraryByExternalId = new Dictionary<string, (int Id, string Name, string? Poster, int? Year, string Type)>();
        foreach (var chunk in ids.Chunk(500))
        {
            var rows = await db.MediaExternalIds
                .Where(x => chunk.Contains(x.ExternalId))
                .Select(x => new
                {
                    x.ExternalId, x.MediaItem!.Id, x.MediaItem.Name, x.MediaItem.PosterUrl, x.MediaItem.Year,
                    Type = x.MediaItem.MediaType!.Name,
                })
                .ToListAsync(ct);
            foreach (var r in rows)
                libraryByExternalId.TryAdd(r.ExternalId, (r.Id, r.Name, r.PosterUrl, r.Year, r.Type));
        }

        var seen = new HashSet<(int, string)>();
        foreach (var c in provided)
        {
            if (libraryByExternalId.TryGetValue(c.ExternalId, out var lib))
            {
                seen.Add((lib.Id, c.Role));
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
            if (!seen.Add((c.MediaItemId, role))) continue;
            result.Add(new PersonFullCredit(c.MediaItemId, c.Name, c.PosterUrl, c.Year, c.Type, c.CharacterName, role));
        }

        return result;
    }
}
