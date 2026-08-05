using System.Text.Json;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Services;

/// <summary>
/// Admin-configurable extra alias JSON key names per canonical resolution field (e.g. the
/// canonical "label" field also matching "recordLabel" or "publisher" in some plugin's blob).
/// Mirrors AssignmentConfigCache's caching pattern exactly. The canonical field SET itself
/// stays code-defined (MetadataResolutionService.FieldMap) — this only supplies additional
/// alias names layered on top, so an admin can extend/correct plugin-naming differences
/// without a redeploy.
/// </summary>
public class FieldAliasCache(IServiceScopeFactory scopeFactory)
{
    private const string SettingKey = "metadata_field_aliases.config";

    // What ships out of the box when nothing has been configured yet — not a regression risk,
    // just a seed the admin can freely override or extend afterward.
    internal static readonly Dictionary<string, List<string>> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["label"]    = ["recordLabel", "publisher"],
        ["composer"] = ["composers"],
        ["bpm"]      = ["tempo"],
        ["language"] = ["lang"],
    };

    // Generation + data travel together as one immutable object so publishing a reload result
    // is a single atomic CompareExchange rather than a separate "check generation, then write
    // data" pair of statements — see the comment on LoadAsync for why that distinction matters.
    private sealed record CacheState(long Generation, Dictionary<string, List<string>>? Data);

    private CacheState _state = new(0, null);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<Dictionary<string, List<string>>> GetAllAsync(CancellationToken ct = default) =>
        await LoadAsync(ct);

    public void Invalidate()
    {
        CacheState old, updated;
        do
        {
            old = Volatile.Read(ref _state);
            updated = new CacheState(old.Generation + 1, null);
        } while (Interlocked.CompareExchange(ref _state, updated, old) != old);
    }

    /// <summary>
    /// Null/missing (no row saved yet) falls back to Defaults — a seed, not a regression.
    /// An explicitly-saved empty object ("{}") is respected as-is: the admin cleared every
    /// extra alias on purpose, and that choice isn't silently overridden.
    /// </summary>
    internal static Dictionary<string, List<string>> ParseConfig(string? json)
    {
        if (json is null) return new(Defaults, StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed is not null
                ? new Dictionary<string, List<string>>(parsed, StringComparer.OrdinalIgnoreCase)
                : new(Defaults, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { return new(Defaults, StringComparer.OrdinalIgnoreCase); }
    }

    // For unit tests only — bypasses DB load
    internal void InjectForTest(Dictionary<string, List<string>> config) =>
        _state = new CacheState(_state.Generation, config);

    private async Task<Dictionary<string, List<string>>> LoadAsync(CancellationToken ct)
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
            var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
            var loaded = ParseConfig(row?.Value);

            // Confirmed race (2026-08-05): a writer's Invalidate() can fire while THIS load's DB
            // query is still in flight (started before the write committed). If we published
            // unconditionally, this load's stale result could land in the cache strictly after
            // the writer's Invalidate() and never get evicted again — every future GetAllAsync
            // would then read pre-write data forever, e.g. a PUT that clears an alias silently
            // "not sticking" for the rest of the process's life. CompareExchange against the
            // EXACT snapshot object read above (not just a separate generation-number check)
            // closes this atomically: Invalidate() always swaps in a brand-new CacheState
            // instance, so if one ran at any point since `snapshot` was read — including in the
            // gap between a generation check and a separate write, which a two-field check-then-
            // act version of this fix would still miss — this CompareExchange's reference
            // comparison fails and the stale result is simply never published. A stale load
            // still returns correctly to THIS caller either way; it just isn't cached for the
            // next one.
            Interlocked.CompareExchange(ref _state, new CacheState(snapshot.Generation, loaded), snapshot);

            return loaded;
        }
        finally { _lock.Release(); }
    }
}
