namespace Chronicle.Services;

/// <summary>
/// Snapshot of the current (or most recently completed) group import operation.
/// </summary>
public sealed class ImportProgressState
{
    public bool IsRunning { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public string? CurrentItemName { get; set; }
    public string? Error { get; set; }
    public bool IsComplete { get; set; }
    public ImportProgressResult? Result { get; set; }
}

/// <summary>Mirrors ImportSummaryDto so the progress endpoint can deliver the final result.</summary>
public sealed class ImportProgressResult
{
    public int Imported { get; set; }
    public int Failed { get; set; }
    public List<string> Failures { get; set; } = [];
    public int Duplicates { get; set; }
}

/// <summary>
/// Thread-safe in-memory tracker for the current (or most recent) import-groups operation.
/// Registered as a singleton so the scoped FileScanService (writer) and the
/// FileScanController import-progress endpoint (reader) share the same in-memory state.
///
/// Concurrency: volatile reference assignment on the record gives consistent snapshots
/// without torn reads for the single-importer scenario modelled here.
/// </summary>
public sealed class ImportProgressService
{
    private volatile ImportProgressState _state = new() { IsRunning = false };

    public ImportProgressState GetState() => _state;

    /// <summary>Reset to idle / clear previous result.</summary>
    public void Reset() =>
        _state = new ImportProgressState { IsRunning = false };

    /// <summary>Signal that an import has started with <paramref name="total"/> root groups.</summary>
    public void Start(int total) =>
        _state = new ImportProgressState
        {
            IsRunning = true,
            Total = total,
            Processed = 0,
            CurrentItemName = null,
        };

    /// <summary>Called once per root group, before processing that group.</summary>
    public void Update(int processed, int total, string? currentItemName) =>
        _state = new ImportProgressState
        {
            IsRunning = true,
            Total = total,
            Processed = processed,
            CurrentItemName = currentItemName,
        };

    /// <summary>Signal successful completion and attach the final result.</summary>
    public void Complete(ImportProgressResult result) =>
        _state = new ImportProgressState
        {
            IsRunning = false,
            IsComplete = true,
            Total = result.Imported + result.Failed + result.Duplicates,
            Processed = result.Imported + result.Failed + result.Duplicates,
            Result = result,
        };

    /// <summary>Signal that the import failed with an unhandled exception.</summary>
    public void Fail(string error) =>
        _state = new ImportProgressState
        {
            IsRunning = false,
            IsComplete = true,
            Error = error,
        };
}
