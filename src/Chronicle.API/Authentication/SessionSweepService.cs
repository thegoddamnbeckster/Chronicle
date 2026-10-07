using Chronicle.Services.Security;

namespace Chronicle.API.Authentication;

/// <summary>Periodically drops expired sessions so abandoned ones do not accumulate in memory.</summary>
public sealed class SessionSweepService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private readonly ISessionStore _sessions;
    private readonly ILogger<SessionSweepService> _log;

    public SessionSweepService(ISessionStore sessions, ILogger<SessionSweepService> log)
    {
        _sessions = sessions;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var removed = _sessions.Sweep();
                if (removed > 0)
                    _log.LogInformation("Session sweep removed {Count} expired session(s); {Live} live", removed, _sessions.Count);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
