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

    private volatile Dictionary<string, List<string>>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<Dictionary<string, List<string>>> GetAllAsync(CancellationToken ct = default) =>
        await LoadAsync(ct);

    public void Invalidate() => Interlocked.Exchange(ref _cache, null);

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
    internal void InjectForTest(Dictionary<string, List<string>> config) => _cache = config;

    private async Task<Dictionary<string, List<string>>> LoadAsync(CancellationToken ct)
    {
        if (_cache is { } hit) return hit;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } doubleCheck) return doubleCheck;
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
            _cache = ParseConfig(row?.Value);
            return _cache;
        }
        finally { _lock.Release(); }
    }
}
