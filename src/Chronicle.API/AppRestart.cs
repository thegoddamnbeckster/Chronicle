namespace Chronicle.API;

/// <summary>
/// Asks the host process to restart. Chronicle cannot restart itself: it exits with a distinctive code
/// and relies on whatever runs it to start it again - <c>restart: unless-stopped</c> in Docker, the
/// service recovery setting for the Windows service, or the person who ran the dev script. Exiting is
/// what lets a staged database restore be swapped in, since the live file is locked while running.
/// </summary>
public interface IAppRestart
{
    void Request(string reason);
}

public sealed class AppRestart : IAppRestart
{
    /// <summary>Non-zero so "restart on failure" policies also fire, and distinctive so a log reader can tell it was deliberate.</summary>
    public const int RestartExitCode = 75;

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AppRestart> _log;
    private int _requested;

    public AppRestart(IHostApplicationLifetime lifetime, ILogger<AppRestart> log)
    {
        _lifetime = lifetime;
        _log = log;
    }

    public void Request(string reason)
    {
        if (Interlocked.Exchange(ref _requested, 1) == 1) return;
        _log.LogWarning("Restart requested ({Reason}); exiting with code {Code}", reason, RestartExitCode);
        Environment.ExitCode = RestartExitCode;
        // Give the HTTP response that asked for this a moment to reach the browser first.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            _lifetime.StopApplication();
        });
    }
}
