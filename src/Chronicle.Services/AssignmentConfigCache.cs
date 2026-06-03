using System.Text.Json;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Services;

public class AssignmentConfigCache(IServiceScopeFactory scopeFactory)
{
    private volatile Dictionary<string, Dictionary<string, List<string>>>? _cache;
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

    public void Invalidate() => Interlocked.Exchange(ref _cache, null);

    internal static Dictionary<string, Dictionary<string, List<string>>> ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<string>>>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch { return []; }
    }

    // For unit tests only — bypasses DB load
    internal void InjectForTest(Dictionary<string, Dictionary<string, List<string>>> config) =>
        _cache = config;

    private async Task<Dictionary<string, Dictionary<string, List<string>>>> LoadAsync(CancellationToken ct)
    {
        if (_cache is { } hit) return hit;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } doubleCheck) return doubleCheck;
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "metadata_assignment.config", ct);
            _cache = ParseConfig(row?.Value);
            return _cache;
        }
        finally { _lock.Release(); }
    }
}
