using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Resolves a credit (cast/crew entry from a plugin's MediaMetadata, or a legacy Trakt import
/// credit) onto a real "people"-type MediaItem, creating a stub person the first time anyone is
/// credited under that name/external id. See docs/plans/2026-08-28-people-section-design.md
/// Section 3 for the full design; this is a direct implementation of its 7-step algorithm.
///
/// Known limitation, stated plainly (per the design doc): name-only resolution (steps 2-3, when
/// no ExternalPersonId is available) carries a real, accepted common-name collision risk -- two
/// different real people with the same name and no external ID on either credit will merge into
/// one person item. Mitigated, not eliminated, by preferring ID-based resolution wherever a
/// plugin supplies one.
/// </summary>
public class PersonResolutionService(
    IPluginRegistry pluginRegistry,
    ILogger<PersonResolutionService> logger) : IPersonResolutionService
{
    public async Task ResolveAndRecordCreditAsync(
        ChronicleDbContext db,
        int titleMediaItemId,
        string personName,
        string? externalPersonId,
        string source,
        string? profileImageUrl,
        string role,
        string? characterName,
        int? billingOrder,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personName))
            return;

        var person = await ResolvePersonOnlyAsync(db, personName, externalPersonId, source, ct);

        // Step 5: accumulate the headshot, if supplied. INSERT-only, never overwritten (see
        // PersonHeadshot's own doc) -- a duplicate (person, url) pair is simply skipped rather
        // than erroring, since re-discovering the same photo on re-enrichment is routine, not
        // exceptional.
        if (!string.IsNullOrWhiteSpace(profileImageUrl))
        {
            var alreadySeen = await db.PersonHeadshots.AnyAsync(
                h => h.PersonMediaItemId == person.Id && h.Url == profileImageUrl, ct);
            if (!alreadySeen)
            {
                db.PersonHeadshots.Add(new PersonHeadshot
                {
                    PersonMediaItemId = person.Id,
                    Url               = profileImageUrl,
                    Source            = source,
                });
            }
        }

        // Step 6: write the credit row itself -- one row per call; the caller owns clearing any
        // prior rows for (titleMediaItemId, source) before looping, same delete-and-reinsert
        // pattern SyncOrchestrationService.FetchAndStoreCreditsAsync already uses.
        db.MediaCredits.Add(new MediaCredit
        {
            MediaItemId       = titleMediaItemId,
            PersonName        = personName,
            Role              = role,
            CharacterName     = characterName,
            BillingOrder      = billingOrder,
            Source            = source,
            ExternalPersonId  = externalPersonId,
            PersonMediaItemId = person.Id,
        });
    }

    /// <summary>Steps 1-4 of the design: id lookup, then name lookup, then create-stub, then
    /// record a newly-supplied external id. Step 7 (seed enrichment rows for a brand-new stub)
    /// happens here too, right after creation -- same "new item -> pending enrichment rows for
    /// every type-compatible plugin" mechanism Add Media/type-change already use (see
    /// MediaService.MoveItemsToTypeAsync for the sibling implementation this mirrors), just
    /// invoked from this new call site.</summary>
    public async Task<MediaItem> ResolvePersonOnlyAsync(
        ChronicleDbContext db, string personName, string? externalPersonId, string source, CancellationToken ct = default)
    {
        // Step 1: external id lookup, scoped to people-type items only -- an id collision with
        // some other type's external id (astronomically unlikely given source+id are namespaced
        // per plugin, but cheap to guard explicitly) must never resolve onto a non-person item.
        MediaItem? person = null;
        if (!string.IsNullOrWhiteSpace(externalPersonId))
        {
            person = await db.MediaExternalIds
                .Include(x => x.MediaItem)
                .Where(x => x.Source == source && x.ExternalId == externalPersonId)
                .Select(x => x.MediaItem)
                .FirstOrDefaultAsync(m => m != null && m.MediaType!.Name == "people", ct);
        }

        var peopleTypeId = await GetPeopleMediaTypeIdAsync(db, ct);
        var normalized = MediaItemNormalizer.NormalizeName(personName);

        // Step 2: name lookup, scoped to people-type items.
        person ??= await db.MediaItems.FirstOrDefaultAsync(
            m => m.MediaTypeId == peopleTypeId && m.NormalizedName == normalized, ct);

        // Step 3: create a new stub.
        if (person is null)
        {
            person = new MediaItem
            {
                MediaTypeId    = peopleTypeId,
                Name           = personName,
                NormalizedName = normalized,
                HierarchyLevel = 0,
                IsStub         = true,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };
            db.MediaItems.Add(person);
            await db.SaveChangesAsync(ct); // need the id before seeding enrichment rows / recording the external id

            await SeedEnrichmentRowsAsync(db, person.Id, ct);

            logger.LogInformation(
                "PersonResolutionService: created new person stub {PersonId} \"{Name}\" (first credited via {Source})",
                person.Id, personName, source);
        }

        // Step 4: record the external id, if new.
        if (!string.IsNullOrWhiteSpace(externalPersonId))
        {
            var alreadyRecorded = await db.MediaExternalIds.AnyAsync(
                x => x.MediaItemId == person.Id && x.Source == source && x.ExternalId == externalPersonId, ct);
            if (!alreadyRecorded)
            {
                db.MediaExternalIds.Add(new MediaExternalId
                {
                    MediaItemId = person.Id,
                    Source      = source,
                    ExternalId  = externalPersonId,
                });
            }
        }

        return person;
    }

    private static async Task<int> GetPeopleMediaTypeIdAsync(ChronicleDbContext db, CancellationToken ct)
    {
        var id = await db.MediaTypes.Where(t => t.Name == "people").Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
        if (id is null)
            throw new InvalidOperationException(
                "No 'people' media type is registered -- the Wikipedia plugin (the type's canonical " +
                "registrant) must be installed and have run PluginHostService's media-type sync at least once.");
        return id.Value;
    }

    private async Task SeedEnrichmentRowsAsync(ChronicleDbContext db, int personMediaItemId, CancellationToken ct)
    {
        var toAdd = new List<MediaItemEnrichment>();
        foreach (var (pluginId, provider, _) in pluginRegistry.GetMetadataProviderEntries())
        {
            var supported = provider.GetSupportedMediaTypes()
                .Any(t => string.Equals(t.MediaTypeName, "people", StringComparison.OrdinalIgnoreCase));
            if (!supported) continue;

            toAdd.Add(new MediaItemEnrichment
            {
                MediaItemId = personMediaItemId,
                PluginId    = pluginId,
                Status      = EnrichmentStatus.Pending,
                MaxRetries  = 3,
            });
        }

        if (toAdd.Count > 0)
        {
            db.MediaEnrichments.AddRange(toAdd);
            await db.SaveChangesAsync(ct);
        }
    }
}
