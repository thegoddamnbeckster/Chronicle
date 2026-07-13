using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Drains TagMismatchRematchQueue and re-runs metadata matching for each queued item —
/// the automatic side of "a contribution's tags disagreed with what Chronicle had stored."
/// Registered as an IScheduledTask so it gets the existing Background Tasks UI/toggle for
/// free (see BackgroundTasksController), rather than inventing new settings UI.
///
/// Manual re-match doesn't go through this task at all — it's just the existing
/// POST /api/v1/media/{id}/refresh, always available regardless of this task's toggle.
/// </summary>
public sealed class TagMismatchRematchTask(
    TagMismatchRematchQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<TagMismatchRematchTask> logger) : IScheduledTask
{
    public string TaskId      => "tag_mismatch_rematch";
    public string DisplayName => "Tag Mismatch Re-match";
    public string Description => "Re-runs metadata matching for items whose file tags no longer agree with Chronicle's resolved metadata.";
    public string DefaultCron => "*/1 * * * *"; // just drains a queue — cheap to check every minute

    public async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var mediaItemId in queue.DrainAll())
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var enrichment = scope.ServiceProvider.GetRequiredService<IMetadataEnrichmentService>();
                await enrichment.EnrichItemAsync(
                    mediaItemId,
                    new EnrichmentOptions(EnrichmentMode.Force, Cascade: true),
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Tag-mismatch re-match failed for item {Id}", mediaItemId);
            }
            finally
            {
                queue.MarkProcessed(mediaItemId);
            }
        }
    }
}
