using System.Diagnostics;
using System.Net;

namespace Chronicle.Services;

/// <summary>
/// Centralizes the hard timeout ceiling for every outbound call into a plugin provider
/// (SearchAsync, GetByIdAsync, HealthCheckAsync, etc). This is Chronicle's own guarantee,
/// deliberately independent of whether any given plugin's HttpClient — or anything else it
/// does internally — has a timeout configured. Confirmed directly (2026-07-29 overnight)
/// that 8 of 10 installed plugins constructed their HttpClient with no explicit Timeout at
/// all (defaulting to the BCL's 100s), and the resulting hang propagated all the way up
/// through an un-timed-out lock wait, silently freezing Chronicle's entire background
/// scheduler for 18+ hours while /api/health kept reporting healthy (that endpoint never
/// touches the database or any provider, so it stayed green the whole time).
///
/// Chronicle can't require every plugin — including third-party ones nobody here will ever
/// audit — to behave correctly, so the ceiling has to live here, not in any plugin. Every
/// call site that invokes a plugin provider directly should route through this rather than
/// awaiting the provider call bare.
///
/// .NET cancellation is cooperative: CancelAfter() guarantees the CALLER stops waiting and
/// moves on within the timeout, which is what actually prevents one bad plugin from cascading
/// into a system-wide freeze. It does not forcibly kill a plugin's own background work if that
/// plugin ignores the token entirely — there is no way to do that from managed code without
/// process/AppDomain isolation, which is a bigger architectural change than this addresses.
///
/// Takes plain string-consuming delegates for warning/error logging rather than a concrete
/// logger type: Chronicle.Services mixes Microsoft.Extensions.Logging (DI-injected ILogger&lt;T&gt;)
/// and Serilog (static Log.ForContext&lt;T&gt;()) across different classes, and this needs to work
/// from both without forcing a framework choice on the caller.
/// </summary>
public static class ProviderCallGuard
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);

    // Short and bounded, not a SIMKL-style multi-hour cutoff -- same philosophy TmdbClient's
    // own single 429 retry already uses (see that class's own doc). A momentary DNS/connection
    // blip resolves within seconds; anything still failing after this many tries is a real
    // outage, which MetadataEnrichmentService's own "provider unavailable, stop this pass"
    // handling is what should take over from there, not an ever-longer local retry loop.
    private const int MaxTransientRetries = 2;
    private static readonly TimeSpan TransientRetryBaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs <paramref name="call"/> under a hard timeout ceiling. Returns
    /// <paramref name="fallbackValue"/> and logs an error if the call doesn't complete in
    /// time. Also logs a warning for any call that completes but took more than 5s, so a
    /// merely-slow (not fully stuck) provider shows up in logs before it becomes a real problem.
    ///
    /// Retries up to <see cref="MaxTransientRetries"/> times, with a short growing delay
    /// (2s, 4s), on a TRANSIENT network failure -- see <see cref="IsTransientNetworkFailure"/>.
    /// Root-caused live (2026-09-14/15): a brief DNS resolution failure reaching
    /// api.themoviedb.org ("No such host is known") -- not a real TMDB rate limit, nothing in
    /// TMDB's own 429 handling ever fired -- aborted MetadataEnrichmentService's entire batch
    /// pass immediately and left it waiting for the next scheduled/manual trigger, on a queue
    /// large enough that this happened repeatedly, over multiple separate days, without ever
    /// draining. Every plugin call in Chronicle routes through this one method (see this class's
    /// own doc), so retrying HERE — rather than inside each individual plugin's own HTTP client
    /// — gives every provider (TMDB, MusicBrainz, Wikipedia, Hardcover, TVMaze, FanartTV, SIMKL,
    /// ...) the same resilience to a momentary network hiccup in one place, instead of needing
    /// the identical fix copy-pasted into N separate plugin repos. A genuinely sustained outage
    /// still exhausts these retries and propagates exactly as before -- this only absorbs the
    /// brief-blip case, it does not mask a real one.
    ///
    /// <paramref name="retryDelayOverride"/> exists purely so tests don't have to sit through
    /// real multi-second sleeps to exercise the retry path -- production callers never pass it.
    /// </summary>
    public static async Task<T> CallAsync<T>(
        Func<CancellationToken, Task<T>> call,
        string pluginId,
        string operation,
        T fallbackValue,
        Action<string> logWarning,
        Action<string> logError,
        CancellationToken ct,
        TimeSpan? timeout = null,
        TimeSpan? retryDelayOverride = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var retryBaseDelay = retryDelayOverride ?? TransientRetryBaseDelay;

        for (var attempt = 0; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(effectiveTimeout);
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await call(cts.Token);
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    logWarning($"Provider {pluginId} {operation} took {sw.ElapsedMilliseconds}ms (slow, but completed)");
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own CancelAfter fired, not the caller's own cancellation -- a genuine timeout.
                logError($"Provider {pluginId} {operation} did not complete within " +
                         $"{effectiveTimeout.TotalSeconds}s — treating as no result");
                return fallbackValue;
            }
            catch (Exception ex)
            {
                if (attempt < MaxTransientRetries && IsTransientNetworkFailure(ex))
                {
                    var delay = retryBaseDelay * (attempt + 1);
                    logWarning($"Provider {pluginId} {operation} hit a transient network failure " +
                               $"({ex.GetType().Name}: {ex.Message}) after {sw.ElapsedMilliseconds}ms — " +
                               $"retrying in {delay.TotalSeconds:0}s ({attempt + 1}/{MaxTransientRetries})");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                // Any other exception (network error exhausted its retries, malformed response,
                // plugin bug) is the caller's problem to interpret -- we only guard against
                // unbounded hangs and momentary network blips here.
                logWarning($"Provider {pluginId} {operation} failed after {sw.ElapsedMilliseconds}ms: " +
                           $"{ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// True for a network-level failure worth a short local retry: no HTTP response was ever
    /// received (StatusCode is null -- DNS failure, connection refused, TLS handshake failure)
    /// or the server itself reported a classically-transient status (429/502/503/504). NEVER
    /// true for a real response like 404/401/403/400 -- those are meaningful answers a retry
    /// can't change, and MetadataEnrichmentService's own classification (e.g. 404 → NotFound)
    /// depends on seeing them exactly once, not after this method has already silently retried
    /// and possibly changed the outcome underneath it.
    /// </summary>
    private static bool IsTransientNetworkFailure(Exception ex) =>
        ex is HttpRequestException hre &&
        (hre.StatusCode is null
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout);
}
