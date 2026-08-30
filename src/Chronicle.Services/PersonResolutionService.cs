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
    IMetadataResolutionService resolutionService,
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
        // exceptional. Checks BOTH the database and this DbContext's own not-yet-saved Added
        // entries -- a person credited under both Cast and Crew in the same enrichment result
        // (e.g. an actor who's also Executive Producer, the exact "Anson Mount" case the design
        // doc itself uses as an example) can have the identical ProfileImageUrl resolved twice
        // within one batch, before the caller's single SaveChangesAsync at the end of the
        // cast+crew loop -- a DB-only check can't see the first (still-pending) insert, so both
        // get added and the unique index on (person_media_item_id, url) throws at save time.
        // Confirmed live (2026-08-30): a real Force-refresh against TMDB hit exactly this.
        if (!string.IsNullOrWhiteSpace(profileImageUrl))
        {
            var alreadyPending = db.ChangeTracker.Entries<PersonHeadshot>().Any(e =>
                e.State == EntityState.Added &&
                e.Entity.PersonMediaItemId == person.Id && e.Entity.Url == profileImageUrl);
            var alreadySeen = alreadyPending || await db.PersonHeadshots.AnyAsync(
                h => h.PersonMediaItemId == person.Id && h.Url == profileImageUrl, ct);
            if (!alreadySeen)
            {
                db.PersonHeadshots.Add(new PersonHeadshot
                {
                    PersonMediaItemId = person.Id,
                    Url               = profileImageUrl,
                    Source            = source,
                });

                // A brand-new headshot doesn't do anything on its own -- MetadataResolutionService.
                // ResolveAsync is what actually promotes a person_headshots row onto the person's
                // own PosterUrl column (Section 1.5's special-cased poster_url resolution for
                // "people"-type items). Nothing else re-resolves the PERSON item when a headshot
                // arrives via this "credit on someone else's title" feed path -- the enrichment
                // pass currently running is for the TITLE (a movie/show), not this person, so
                // without this call a person could accumulate headshots indefinitely and never
                // actually get a PosterUrl. Confirmed live (2026-08-30): a real TMDB force-refresh
                // recorded the headshot correctly but the person's own PosterUrl stayed null.
                // Needs MediaType loaded -- ResolveAsync branches on item.MediaType?.Name, and
                // neither lookup path above (external-id join, name match) or the fresh-stub
                // branch populates that navigation.
                if (person.MediaType is null)
                    await db.Entry(person).Reference(p => p.MediaType).LoadAsync(ct);
                // Flush the headshot insert first -- ResolveAsync's poster_url resolution reads
                // person_headshots back via a real query (GetLatestHeadshotUrlAsync), which (like
                // the duplicate checks above) only sees committed rows, not this same batch's
                // still-pending Add.
                await db.SaveChangesAsync(ct);
                await resolutionService.ResolveAsync(person, db, ct);
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

        // Step 4: record the external id, if new. Same not-yet-saved-entries check as the
        // headshot one above, for the same reason (a person resolved twice in one batch,
        // before the caller's single end-of-loop SaveChangesAsync).
        if (!string.IsNullOrWhiteSpace(externalPersonId))
        {
            var alreadyPending = db.ChangeTracker.Entries<MediaExternalId>().Any(e =>
                e.State == EntityState.Added &&
                e.Entity.MediaItemId == person.Id && e.Entity.Source == source && e.Entity.ExternalId == externalPersonId);
            var alreadyRecorded = alreadyPending || await db.MediaExternalIds.AnyAsync(
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
