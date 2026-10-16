namespace Chronicle.Services;

/// <summary>One plugin's enrichment row counts, by status.</summary>
public sealed record EnrichmentCounts(string PluginId, int Pending, int Completed, int Failed, int Exhausted, int NotFound, int Skipped, int AuthFailed);

/// <summary>
/// The per-plugin status counts behind the Enrichment page, kept for a few seconds. Producing them means walking all of
/// media_enrichment (1.5M rows, ~0.3 s even with a covering index) and the page polls every few seconds from every open tab,
/// so without this the server spends a large share of its time recounting numbers that barely moved. Registered as a singleton;
/// the services that change statuses in bulk call <see cref="Invalidate"/> so an admin never sees stale counts after pressing Reset.
/// </summary>
public sealed class EnrichmentStatsCache(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private IReadOnlyList<EnrichmentCounts>? _rows;
    private DateTimeOffset _loadedAt;
    private Task<IReadOnlyList<EnrichmentCounts>>? _inFlight;

    public void Invalidate()
    {
        lock (_gate) { _rows = null; _inFlight = null; }
    }

    /// <summary>The cached counts, or the result of <paramref name="load"/> (shared between concurrent callers).</summary>
    public Task<IReadOnlyList<EnrichmentCounts>> GetAsync(Func<Task<IReadOnlyList<EnrichmentCounts>>> load)
    {
        lock (_gate)
        {
            if (_rows is not null && clock.GetUtcNow() - _loadedAt < Lifetime)
                return Task.FromResult(_rows);
            if (_inFlight is not null)
                return _inFlight;

            Task<IReadOnlyList<EnrichmentCounts>>? mine = null;
            mine = Task.Run(async () =>
            {
                try
                {
                    var rows = await load();
                    lock (_gate)
                    {
                        // An Invalidate() while this load ran means the numbers may already be out of date: hand them to the
                        // callers that were waiting, but do not keep them.
                        if (ReferenceEquals(_inFlight, mine)) { _rows = rows; _loadedAt = clock.GetUtcNow(); }
                    }
                    return rows;
                }
                finally
                {
                    lock (_gate) { if (ReferenceEquals(_inFlight, mine)) _inFlight = null; }
                }
            });
            _inFlight = mine;
            return mine;
        }
    }
}
