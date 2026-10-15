using Chronicle.Data;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: an item the user owns a file for is not a provider's
/// placeholder, so its stub flag is cleared.
///
/// The flag marks an item a provider's list created for something the user does not have. The file scanner
/// attached files to such items without clearing it, so 108 items held a file and still said "stub": 90 movies
/// in collections (hidden whenever collection stubs are turned off) and 18 audiobooks. Cleanups that treat a
/// stub as disposable then deleted the file's owner ("Words of Radiance, Part 1" and "Dungeon Crawler Carl"
/// #1, 2026-10-15). The scanner now clears the flag as it attaches a file; this heals the existing items.
/// </summary>
public sealed class StubFlagRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<StubFlagRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var stubs = await db.MediaItems
            .Where(m => m.IsStub && m.MetadataJson != null &&
                        (m.MetadataJson.Contains("fileScanner") || m.MetadataJson.Contains("scraperResolvedFile")))
            .ToListAsync(ct);

        var cleared = 0;
        foreach (var item in stubs.Where(i => FileIdentityJson.HasKnownFile(i.MetadataJson)))
        {
            item.IsStub = false;
            cleared++;
        }
        if (cleared > 0) await db.SaveChangesAsync(ct);

        logger.LogInformation("Stub flag repair: cleared the stub flag on {Count} item(s) that hold a file", cleared);
    }
}
