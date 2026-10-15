using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Runs the passes of a task that is several jobs in one: a failing pass is logged and never stops the ones after it, but the
/// task as a whole still FAILS at the end, so the scheduler records the failure and raises the "task failed" notification that
/// the separate tasks used to raise. (Swallowing every pass failure made a task that broke every night look successful.)
/// </summary>
internal sealed class PassFailures(ILogger logger, string taskName)
{
    private readonly List<Exception> _failures = [];

    public async Task RunAsync(string pass, Func<CancellationToken, Task> run, CancellationToken ct)
    {
        try
        {
            await run(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Task}: the {Pass} pass failed; continuing with the next", taskName, pass);
            _failures.Add(ex);
        }
    }

    /// <summary>Throws when any pass failed: the failure itself when there was one, else all of them together.</summary>
    public void ThrowIfAny()
    {
        if (_failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failures[0]).Throw();
        if (_failures.Count > 1) throw new AggregateException($"{taskName}: {_failures.Count} passes failed", _failures);
    }
}
