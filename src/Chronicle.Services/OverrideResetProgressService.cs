namespace Chronicle.Services;

/// <summary>
/// Thread-safe in-memory tracker for the current (or most recent) bulk image-override reset
/// job — mirrors ScanProgressService's design. Registered as a singleton so it is shared
/// between the writer (a Chronicle.API controller's background Task.Run, unlike
/// ScanProgressService's writer which is a same-assembly scoped service) and the progress
/// endpoint reader. Mutators are public (not internal) for that reason.
/// </summary>
public sealed class OverrideResetProgressService
{
    private volatile OverrideResetSnapshot _current = OverrideResetSnapshot.Idle;

    public OverrideResetSnapshot GetSnapshot() => _current;

    public void Start(string scope) =>
        _current = new OverrideResetSnapshot(true, false, scope, 0, 0, null);

    public void UpdateProgress(int processed, int cleared) =>
        _current = _current with { Processed = processed, Cleared = cleared };

    public void Complete() =>
        _current = _current with { IsRunning = false, IsComplete = true };

    public void Fail(string error) =>
        _current = _current with { IsRunning = false, IsComplete = true, Error = error };
}

/// <summary>Immutable snapshot of a bulk override-reset job at a point in time.</summary>
public sealed record OverrideResetSnapshot(
    bool IsRunning,
    bool IsComplete,
    string? Scope,
    int Processed,
    int Cleared,
    string? Error)
{
    public static readonly OverrideResetSnapshot Idle = new(false, false, null, 0, 0, null);
}
