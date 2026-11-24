using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// The rule for anything the user adds by hand: everything beneath it is added with it. An artist brings its albums and their
/// tracks, an author its series and books, a show its seasons and episodes. Which types have anything beneath them is read from
/// the media type itself (<see cref="MediaType.HierarchyLevels"/>), never from a list of names, so a custom type with a hierarchy
/// is covered the same way.
/// </summary>
public interface IDescendantsService
{
    /// <summary>True when items of this type can have anything beneath them.</summary>
    bool HasDescendants(MediaType type);

    /// <summary>Creates whatever is missing beneath the root item. Existing items are never changed or removed. Returns how many were created.</summary>
    Task<int> EnsureDescendantsAsync(int rootItemId, CancellationToken ct = default);
}

public sealed class DescendantsService : IDescendantsService
{
    // A safety bound, not a real-world size: the largest catalogues (a long-running artist, a soap opera) stay well below it.
    private const int MaxItemsPerRoot = 20_000;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> RootLocks = new();

    private readonly ChronicleDbContext _db;
    private readonly IMediaService _media;
    private readonly IPluginRegistry _registry;
    private readonly IShowStructureService _shows;
    private readonly IFileScanService _fileScan;
    private readonly ILogger<DescendantsService> _log;

    public DescendantsService(ChronicleDbContext db, IMediaService media, IPluginRegistry registry,
        IShowStructureService shows, IFileScanService fileScan, ILogger<DescendantsService> log)
    {
        _db = db; _media = media; _registry = registry; _shows = shows; _fileScan = fileScan; _log = log;
    }

    public bool HasDescendants(MediaType type) => type.HierarchyLevels > 1;

    public async Task<int> EnsureDescendantsAsync(int rootItemId, CancellationToken ct = default)
    {
        var root = await _db.MediaItems.Include(m => m.MediaType).FirstOrDefaultAsync(m => m.Id == rootItemId, ct);
        if (root is null || root.ParentId.HasValue || root.HierarchyLevel != 0 || root.MediaType is null
            || !HasDescendants(root.MediaType))
            return 0;

        var gate = RootLocks.GetOrAdd(rootItemId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // An episodic type keeps its own season-by-season episode guide (a provider answers per season, not per show).
            if (MediaTypeRegistry.HasImportShape(root.MediaType.Name, ImportShapes.Episodic))
            {
                await _shows.EnsureEpisodesResolvedAsync(root, ct);
                return 0;
            }

            var created = new List<int>();
            await WalkAsync(root, root.MediaType, created, ct);
            if (created.Count > 0)
            {
                await _fileScan.SeedPendingEnrichmentAsync(created, root.MediaType.Name, ct);
                _log.LogInformation("Descendants of {ItemId} \"{Name}\" ({Type}): created {Count} item(s)",
                    root.Id, root.Name, root.MediaType.Name, created.Count);
            }
            else
            {
                _log.LogInformation("Descendants of {ItemId} \"{Name}\" ({Type}): nothing to create", root.Id, root.Name, root.MediaType.Name);
            }
            return created.Count;
        }
        finally { gate.Release(); }
    }

    private async Task WalkAsync(MediaItem node, MediaType type, List<int> created, CancellationToken ct)
    {
        if (node.HierarchyLevel + 1 >= type.HierarchyLevels || created.Count >= MaxItemsPerRoot) return;

        var (children, pluginId) = await AskProvidersAsync(node, ct);
        if (children.Count == 0) return;

        var childLevel = node.HierarchyLevel + 1;
        var existing = await _db.MediaItems.Where(m => m.ParentId == node.Id).ToListAsync(ct);

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(child.Title) || created.Count >= MaxItemsPerRoot) continue;

            var match = existing.FirstOrDefault(e => child.Number.HasValue && e.Number == child.Number
                            && (child.ExternalId is null || HasSameName(e, child)))
                        ?? existing.FirstOrDefault(e => HasSameName(e, child));

            MediaItem item;
            if (match is not null)
            {
                item = match;
            }
            else
            {
                item = await _media.CreateAsync(new CreateMediaRequest(
                    type.Id, node.Id, child.Title, child.Year, child.Overview, child.PosterUrl, child.RuntimeMinutes,
                    HierarchyLevel: childLevel, Number: child.Number), ct);
                if (child.SeriesPosition.HasValue) item.SeriesPosition = child.SeriesPosition;
                existing.Add(item);
                created.Add(item.Id);
            }

            if (child.ExternalId is { Length: > 0 } && pluginId is not null)
                await AttachExternalIdAsync(item, pluginId, child.ExternalId, ct);

            await _db.SaveChangesAsync(ct);

            if (child.ExternalId is not null)
                await WalkAsync(item, type, created, ct);
        }
    }

    private static bool HasSameName(MediaItem item, ProviderChild child) =>
        MediaItemNormalizer.NormalizeName(item.Name) == MediaItemNormalizer.NormalizeName(child.Title);

    /// <summary>The first provider holding an id for this item that has anything beneath it. Several providers can know the same
    /// item; the one that answers first with a non-empty list wins, as with the episode guide.</summary>
    private async Task<(IReadOnlyList<ProviderChild> Children, string? PluginId)> AskProvidersAsync(MediaItem node, CancellationToken ct)
    {
        var ids = await _db.MediaExternalIds.AsNoTracking().Where(e => e.MediaItemId == node.Id).ToListAsync(ct);
        foreach (var (pluginId, provider, _) in _registry.GetMetadataProviderEntries())
        {
            var source = PluginIdHelper.ToSource(pluginId);
            foreach (var id in ids.Where(i => string.Equals(i.Source, source, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var children = await ProviderCallGuard.CallAsync<IReadOnlyList<ProviderChild>>(
                        t => provider.GetChildrenAsync(id.ExternalId, node.HierarchyLevel, t), pluginId, "GetChildrenAsync",
                        [], msg => _log.LogWarning("{Message}", msg), msg => _log.LogError("{Message}", msg), ct);
                    if (children.Count > 0) return (children, pluginId);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "{PluginId} could not list what is beneath item {ItemId}", pluginId, node.Id);
                }
            }
        }
        return ([], null);
    }

    private async Task AttachExternalIdAsync(MediaItem item, string pluginId, string externalId, CancellationToken ct)
    {
        var source = PluginIdHelper.ToSource(pluginId);
        if (!await _db.MediaExternalIds.AnyAsync(e => e.MediaItemId == item.Id && e.Source == source, ct))
            _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = source, ExternalId = externalId });

        if (!await _db.MediaEnrichments.AnyAsync(e => e.MediaItemId == item.Id && e.PluginId == pluginId, ct))
            _db.MediaEnrichments.Add(new MediaItemEnrichment
            {
                MediaItemId = item.Id, PluginId = pluginId, ExternalId = externalId, Status = EnrichmentStatus.Pending,
            });
    }
}
