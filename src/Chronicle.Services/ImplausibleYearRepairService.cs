using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: an item whose Year is impossible (65535, 0, -1 ...)
/// gets no year instead. See <see cref="ReleaseYear"/> for how such a year reaches Chronicle.
/// </summary>
public sealed class ImplausibleYearRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<ImplausibleYearRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var latest = ReleaseYear.Latest;
        // Rare rows -- loaded and saved through the change tracker (not a bulk update) so the item's
        // normalized-name bookkeeping and every other write hook still runs.
        var items = await db.MediaItems
            .Where(m => m.Year != null && (m.Year < ReleaseYear.Earliest || m.Year > latest))
            .ToListAsync(ct);
        foreach (var item in items) item.Year = null;
        await db.SaveChangesAsync(ct);
        var fixedCount = items.Count;

        logger.LogInformation("Implausible year repair: cleared the year on {Count} item(s)", fixedCount);
    }
}
