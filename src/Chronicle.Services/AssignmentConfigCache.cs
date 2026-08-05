using System.Text.Json;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Services;

public class AssignmentConfigCache(IServiceScopeFactory scopeFactory)
{
    // Generation + data travel together as one immutable object so publishing a reload result
    // is a single atomic CompareExchange rather than a separate "check generation, then write
    // data" pair of statements — see the comment on LoadAsync for why that distinction matters
    // (identical fix to FieldAliasCache, which this class mirrors).
    private sealed record CacheState(long Generation, Dictionary<string, Dictionary<string, List<string>>>? Data);

    private CacheState _state = new(0, null);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<Dictionary<string, List<string>>> GetForTypeAsync(
        string mediaTypeName, int hierarchyLevel, CancellationToken ct = default)
    {
        var config = await LoadAsync(ct);
        var levelKey = hierarchyLevel > 0 ? $"{mediaTypeName}.{hierarchyLevel}" : mediaTypeName;
        if (config.TryGetValue(levelKey, out var byLevel)) return byLevel;
        if (hierarchyLevel > 0 && config.TryGetValue(mediaTypeName, out var byBase)) return byBase;
        return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }

    public void Invalidate()
    {
        CacheState old, updated;
        do
        {
            old = Volatile.Read(ref _state);
            updated = new CacheState(old.Generation + 1, null);
        } while (Interlocked.CompareExchange(ref _state, updated, old) != old);
    }

    internal static Dictionary<string, Dictionary<string, List<string>>> ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<string>>>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException) { return []; }
    }

    // For unit tests only — bypasses DB load
    internal void InjectForTest(Dictionary<string, Dictionary<string, List<string>>> config) =>
        _state = new CacheState(_state.Generation, config);

    private async Task<Dictionary<string, Dictionary<string, List<string>>>> LoadAsync(CancellationToken ct)
    {
        var snapshot = Volatile.Read(ref _state);
        if (snapshot.Data is { } hit) return hit;
        await _lock.WaitAsync(ct);
        try
        {
            snapshot = Volatile.Read(ref _state);
            if (snapshot.Data is { } doubleCheck) return doubleCheck;
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "metadata_assignment.config", ct);
            var loaded = ParseConfig(row?.Value);

            // CompareExchange against the EXACT snapshot object read above (not a separate
            // generation-number check) publishes atomically: Invalidate() always swaps in a
            // brand-new CacheState instance, so if one ran at any point since `snapshot` was
            // read, this CompareExchange's reference comparison fails and the stale result is
            // simply never published. See FieldAliasCache.LoadAsync for the confirmed incident
            // and the full reasoning (identical fix, mirrored here).
            Interlocked.CompareExchange(ref _state, new CacheState(snapshot.Generation, loaded), snapshot);

            return loaded;
        }
        finally { _lock.Release(); }
    }
}
