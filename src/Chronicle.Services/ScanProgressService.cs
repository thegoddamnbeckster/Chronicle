namespace Chronicle.Services;

/// <summary>
/// Thread-safe in-memory tracker for the current (or most recent) directory scan.
/// Registered as a singleton so it is shared between the scoped FileScanService
/// (which writes progress) and the FileScanController progress endpoint (which reads it).
///
/// Concurrency notes:
///   - volatile assignment of the record reference is sufficient for a single-scanner
///     scenario; the progress endpoint reads a consistent snapshot with no torn reads.
///   - A future multi-scan design would need a per-scan ID, but that is YAGNI here.
/// </summary>
public sealed class ScanProgressService
{
    private volatile ScanProgressSnapshot _current = ScanProgressSnapshot.Idle;

    public ScanProgressSnapshot GetSnapshot() => _current;

    internal void Start(int totalFolders) =>
        _current = new ScanProgressSnapshot(true, null, 0, totalFolders, 0);

    internal void UpdateFolder(string currentFolder, int foldersScanned, int filesFound) =>
        _current = new ScanProgressSnapshot(true, currentFolder, foldersScanned, _current.TotalFolders, filesFound);

    internal void Complete() =>
        _current = ScanProgressSnapshot.Idle;
}

/// <summary>Immutable snapshot of scan progress at a point in time.</summary>
public sealed record ScanProgressSnapshot(
    bool IsScanning,
    string? CurrentFolder,
    int FoldersScanned,
    int TotalFolders,
    int FilesFound)
{
    public static readonly ScanProgressSnapshot Idle = new(false, null, 0, 0, 0);
}
