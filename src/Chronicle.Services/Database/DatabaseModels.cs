namespace Chronicle.Services.Database
{
    /// <summary>What kind of file a backup zip is, from its name.</summary>
    public static class BackupKinds
    {
        /// <summary>Taken by the nightly task.</summary>
        public const string Scheduled = "scheduled";
        /// <summary>Taken by a person pressing "Back up now".</summary>
        public const string Manual = "manual";
        /// <summary>Taken automatically just before a restore replaced the live database.</summary>
        public const string PreRestore = "pre-restore";
        /// <summary>Uploaded by a person; kept until they delete it.</summary>
        public const string Uploaded = "uploaded";
    }

    public sealed record BackupManifest(
        int Format,
        DateTime CreatedAtUtc,
        string AppVersion,
        string Provider,
        string? LatestMigration,
        string DbFile,
        long DbBytes,
        string Sha256,
        IReadOnlyDictionary<string, long> RowCounts);

    public sealed record BackupInfo(
        string FileName,
        string Kind,
        long SizeBytes,
        DateTime CreatedAtUtc,
        string? LatestMigration,
        long? DatabaseBytes,
        IReadOnlyDictionary<string, long>? RowCounts);

    public sealed record BackupValidation(bool Valid, string? Error, BackupManifest? Manifest);

    public sealed record TableSize(string Name, long Bytes, long? Rows);

    public sealed record DatabaseStatus(
        bool Supported,
        string Provider,
        string? Note,
        string? DatabaseFile,
        long DatabaseBytes,
        long WalBytes,
        long PageSize,
        long PageCount,
        long FreePages,
        long ReclaimableBytes,
        long FreeDiskBytes,
        string? LatestMigration,
        string? BackupDirectory,
        int BackupCount,
        int RetainCount,
        DateTime? LastBackupAtUtc,
        long WarnSizeBytes,
        bool OverWarnSize,
        IReadOnlyList<TableSize> LargestTables);

    public sealed record MaintenanceResult(string Summary, TimeSpan Elapsed, long BytesBefore, long BytesAfter);

    /// <summary>A database operation was refused or failed in a way the caller can show the user.</summary>
    public class DatabaseAdminException : Exception
    {
        public string Code { get; }
        public DatabaseAdminException(string code, string message) : base(message) => Code = code;
    }
}
