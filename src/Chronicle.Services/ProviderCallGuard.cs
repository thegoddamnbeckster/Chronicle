using System.Diagnostics;

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

    /// <summary>
    /// Runs <paramref name="call"/> under a hard timeout ceiling. Returns
    /// <paramref name="fallbackValue"/> and logs an error if the call doesn't complete in
    /// time. Also logs a warning for any call that completes but took more than 5s, so a
    /// merely-slow (not fully stuck) provider shows up in logs before it becomes a real problem.
    /// </summary>
    public static async Task<T> CallAsync<T>(
        Func<CancellationToken, Task<T>> call,
        string pluginId,
        string operation,
        T fallbackValue,
        Action<string> logWarning,
        Action<string> logError,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
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
            // Any other exception (network error, malformed response, plugin bug) is the
            // caller's problem to interpret -- we only guard against unbounded hangs here.
            logWarning($"Provider {pluginId} {operation} failed after {sw.ElapsedMilliseconds}ms: " +
                       $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
