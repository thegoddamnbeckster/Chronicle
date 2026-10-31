using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: an item whose stored poster is a vector image (SVG) is
/// resolved again. Kodi cannot draw an SVG, and the resolver now skips one in favour of the next provider's
/// image (MetadataResolutionService.IsVectorImageUrl), so an item resolved before that rule (The Voice, whose
/// poster was Wikipedia's logo while TVmaze and Simkl each had a real poster) picks up its proper poster
/// here, from data already stored -- no provider is called. People are skipped: their picture comes from the
/// headshot table, not the provider walk.
/// </summary>
public sealed class VectorPosterRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<VectorPosterRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var resolution = scope.ServiceProvider.GetRequiredService<IMetadataResolutionService>();

        var candidates = await db.MediaItems
            .Include(m => m.MediaType)
            .Where(m => m.PosterUrl != null && m.PosterUrl.ToLower().Contains(".svg"))
            .ToListAsync(ct);

        var fixedCount = 0;
        foreach (var item in candidates)
        {
            if (!MetadataResolutionService.IsVectorImageUrl(item.PosterUrl)) continue;
            if (item.MediaType?.IsPersonType == true) continue;

            var before = item.PosterUrl;
            await resolution.ResolveAsync(item, db, ct);
            if (item.PosterUrl != before) fixedCount++;
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Vector poster repair: re-resolved {Count} item(s) with an SVG poster, {Fixed} now have a usable one",
            candidates.Count, fixedCount);
    }
}
