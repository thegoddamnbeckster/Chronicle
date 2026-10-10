using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services.Database
{
    public interface IDatabaseAdminService
    {
        Task<DatabaseStatus> GetStatusAsync(CancellationToken ct = default);

        /// <summary>Snapshots the live database into a zip next to it. <paramref name="kind"/> is one of <see cref="BackupKinds"/>.</summary>
        Task<BackupInfo> CreateBackupAsync(string kind, CancellationToken ct = default);

        IReadOnlyList<BackupInfo> ListBackups();

        /// <summary>Full path of a backup in the backup folder. Throws NOT_FOUND for an unknown or unsafe name.</summary>
        string GetBackupPath(string fileName);

        bool DeleteBackup(string fileName);

        /// <summary>Stores an uploaded zip, validates it, and keeps it as an "uploaded" backup. An invalid file is deleted.</summary>
        Task<BackupInfo> SaveUploadAsync(Stream body, CancellationToken ct = default);

        Task<BackupValidation> ValidateBackupAsync(string fileName, CancellationToken ct = default);

        /// <summary>Validates, takes a safety backup of the live database, and stages the backup so the
        /// next startup swaps it in. The caller then restarts the application.</summary>
        Task StageRestoreAsync(string fileName, CancellationToken ct = default);

        Task<MaintenanceResult> RunMaintenanceAsync(bool heavy, CancellationToken ct = default);
    }

    /// <summary>
    /// Backups, restore staging, maintenance and size reporting for the SQLite database.
    ///
    /// Everything this writes lives in a <c>backups</c> folder NEXT TO the database file (in Docker that is
    /// inside the data volume) and SQLite's scratch files are pointed at a <c>temp</c> folder there too -
    /// nothing goes to %TEMP% or ProgramData. A backup is a consistent snapshot taken with
    /// <c>VACUUM INTO</c> while the application keeps running, zipped with a manifest (version, latest
    /// migration, SHA-256, row counts) so it can be checked before it is trusted.
    ///
    /// Only SQLite is supported; for PostgreSQL every operation reports that plainly rather than
    /// pretending (use pg_dump).
    /// </summary>
    public sealed class DatabaseAdminService : IDatabaseAdminService
    {
        public const string DbEntryName = "chronicle.db";
        public const string ManifestEntryName = "manifest.json";
        public const string RetainKey = "backup.retain";
        public const string WarnSizeMbKey = "db.warn_size_mb";
        public const int DefaultRetain = 10;
        public const int PreRestoreRetain = 3;
        public const long DefaultWarnSizeMb = 5 * 1024;

        private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,150}\.zip$", RegexOptions.Compiled);
        private static readonly string[] CountedTables = ["users", "media_items", "interaction_events", "user_libraries"];
        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

        private readonly IServiceScopeFactory _scopes;
        private readonly TimeProvider _clock;
        private readonly ILogger _log = Log.ForContext<DatabaseAdminService>();
        private readonly SemaphoreSlim _busy = new(1, 1);
        private readonly Func<string, long> _freeDisk;

        /// <param name="freeDisk">Free bytes on the drive holding a path. Replaceable so tests can simulate a full disk.</param>
        public DatabaseAdminService(IServiceScopeFactory scopes, TimeProvider? clock = null, Func<string, long>? freeDisk = null)
        {
            _scopes = scopes;
            _clock = clock ?? TimeProvider.System;
            _freeDisk = freeDisk ?? FreeDiskOf;
        }

        // ── where things are ──────────────────────────────────────────────────

        private sealed record Location(bool Supported, string Provider, string? Note, string? DbFile)
        {
            public string? Dir => DbFile is null ? null : Path.GetDirectoryName(DbFile);
            public string? BackupDir => Dir is null ? null : Path.Combine(Dir, "backups");
            public string? TempDir => Dir is null ? null : Path.Combine(Dir, "temp");
            public string? PendingFile => DbFile is null ? null : DbFile + PendingRestore.Suffix;
        }

        private Location Locate(ChronicleDbContext db)
        {
            var provider = db.Database.ProviderName ?? "unknown";
            if (!provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                return new Location(false, provider,
                    "Backups from the app are available for SQLite only. For PostgreSQL use pg_dump on the database server.", null);

            var cs = db.Database.GetConnectionString();
            if (string.IsNullOrWhiteSpace(cs))
                return new Location(false, provider, "The database connection string is not available.", null);

            var file = new SqliteConnectionStringBuilder(cs).DataSource;
            if (string.IsNullOrWhiteSpace(file) || file == ":memory:" || file.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
                return new Location(false, provider, "This is an in-memory database; there is no file to back up.", null);

            return new Location(true, provider, null, Path.GetFullPath(file));
        }

        private Location RequireSupported()
        {
            using var scope = _scopes.CreateScope();
            var loc = Locate(scope.ServiceProvider.GetRequiredService<ChronicleDbContext>());
            if (!loc.Supported) throw new DatabaseAdminException("UNSUPPORTED", loc.Note ?? "Not supported for this database.");
            return loc;
        }

        private async Task<IDisposable> AcquireAsync()
        {
            if (!await _busy.WaitAsync(TimeSpan.Zero))
                throw new DatabaseAdminException("BUSY", "Another database operation is already running. Try again when it finishes.");
            return new Releaser(_busy);
        }

        private sealed class Releaser(SemaphoreSlim s) : IDisposable
        {
            private int _done;
            public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) s.Release(); }
        }

        // ── settings ──────────────────────────────────────────────────────────

        private async Task<double> GetSettingAsync(ChronicleDbContext db, string key, double fallback)
        {
            var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
            return row is not null
                   && double.TryParse(row.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                   && v > 0 && v < 1_000_000_000
                ? v : fallback;
        }

        // ── status ────────────────────────────────────────────────────────────

        public async Task<DatabaseStatus> GetStatusAsync(CancellationToken ct = default)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var loc = Locate(db);
            var warnMb = (long)await GetSettingAsync(db, WarnSizeMbKey, DefaultWarnSizeMb);
            var retain = (int)await GetSettingAsync(db, RetainKey, DefaultRetain);

            if (!loc.Supported)
                return new DatabaseStatus(false, loc.Provider, loc.Note, null, 0, 0, 0, 0, 0, 0, 0, null, null, 0, retain, null,
                    warnMb * 1024 * 1024, false, []);

            long dbBytes = FileLength(loc.DbFile!);
            long walBytes = FileLength(loc.DbFile + "-wal");

            long pageSize = 0, pageCount = 0, freePages = 0;
            var tables = new List<TableSize>();
            string? latest = null;

            await db.Database.OpenConnectionAsync(ct);
            try
            {
                var conn = db.Database.GetDbConnection();
                pageSize = await ScalarLongAsync(conn, "PRAGMA page_size", ct);
                pageCount = await ScalarLongAsync(conn, "PRAGMA page_count", ct);
                freePages = await ScalarLongAsync(conn, "PRAGMA freelist_count", ct);
                latest = await ScalarStringAsync(conn, "SELECT MigrationId FROM \"__EFMigrationsHistory\" ORDER BY MigrationId DESC LIMIT 1", ct);
                try
                {
                    // dbstat is compiled into the bundled SQLite; if it ever is not, the sizes simply are not shown.
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT name, SUM(pgsize) AS bytes FROM dbstat WHERE aggregate = 0 GROUP BY name ORDER BY bytes DESC LIMIT 12";
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        tables.Add(new TableSize(reader.GetString(0), reader.GetInt64(1), null));
                }
                catch (Exception ex) { _log.Debug(ex, "dbstat unavailable; table sizes not reported"); }
            }
            finally { await db.Database.CloseConnectionAsync(); }

            var backups = ListBackups();
            var regular = backups.Where(b => b.Kind is BackupKinds.Scheduled or BackupKinds.Manual).ToList();

            return new DatabaseStatus(
                Supported: true, Provider: loc.Provider, Note: null, DatabaseFile: loc.DbFile,
                DatabaseBytes: dbBytes, WalBytes: walBytes, PageSize: pageSize, PageCount: pageCount, FreePages: freePages,
                ReclaimableBytes: freePages * pageSize, FreeDiskBytes: _freeDisk(loc.Dir!), LatestMigration: latest,
                BackupDirectory: loc.BackupDir, BackupCount: backups.Count, RetainCount: retain,
                LastBackupAtUtc: regular.Count == 0 ? null : regular.Max(b => b.CreatedAtUtc),
                WarnSizeBytes: warnMb * 1024 * 1024, OverWarnSize: dbBytes + walBytes > warnMb * 1024 * 1024,
                LargestTables: tables);
        }

        // ── backup ────────────────────────────────────────────────────────────

        public async Task<BackupInfo> CreateBackupAsync(string kind, CancellationToken ct = default)
        {
            if (kind is not (BackupKinds.Scheduled or BackupKinds.Manual or BackupKinds.PreRestore))
                throw new ArgumentException($"Unknown backup kind '{kind}'.", nameof(kind));

            var loc = RequireSupported();
            using var _ = await AcquireAsync();
            return await CreateBackupCoreAsync(loc, kind, ct);
        }

        private async Task<BackupInfo> CreateBackupCoreAsync(Location loc, string kind, CancellationToken ct)
        {
            Directory.CreateDirectory(loc.BackupDir!);
            var stamp = _clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss");
            var baseName = kind == BackupKinds.PreRestore ? $"pre-restore-{stamp}" : $"chronicle-{stamp}-{kind}";
            for (var n = 2; File.Exists(Path.Combine(loc.BackupDir!, baseName + ".zip")); n++)
                baseName = (kind == BackupKinds.PreRestore ? $"pre-restore-{stamp}" : $"chronicle-{stamp}-{kind}") + $"-{n}";

            var snapshot = Path.Combine(loc.BackupDir!, baseName + ".snapshot.tmp");
            var partial = Path.Combine(loc.BackupDir!, baseName + ".zip.partial");
            var final = Path.Combine(loc.BackupDir!, baseName + ".zip");
            var sw = Stopwatch.StartNew();
            try
            {
                SafeDelete(snapshot);
                using (var scope = _scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                    db.Database.SetCommandTimeout(TimeSpan.FromHours(1));
                    // The path is generated here from a timestamp - never from user input - so quoting it is enough.
                    await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{snapshot.Replace("'", "''")}'", ct);
                }

                var (sha, length) = await HashFileAsync(snapshot, ct);
                var facts = ReadFacts(snapshot, checkIntegrity: false);
                var manifest = new BackupManifest(1, _clock.GetUtcNow().UtcDateTime, AppVersion(), loc.Provider,
                    facts.LatestMigration, DbEntryName, length, sha, facts.RowCounts);

                await using (var fs = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    var db = zip.CreateEntry(DbEntryName, CompressionLevel.Optimal);
                    await using (var es = db.Open())
                    await using (var src = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read))
                        await src.CopyToAsync(es, ct);

                    var m = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                    await using var ms = m.Open();
                    await JsonSerializer.SerializeAsync(ms, manifest, Json, ct);
                }

                File.Move(partial, final);
                _log.Information("DATABASE backup created: {File} ({Kind}) in {Elapsed} ms", Path.GetFileName(final), kind, sw.ElapsedMilliseconds);
            }
            finally
            {
                SafeDelete(snapshot);
                SafeDelete(partial);
            }

            await PruneAsync(loc, keep: Path.GetFileName(final), ct);
            return ToInfo(final)!;
        }

        private async Task PruneAsync(Location loc, string keep, CancellationToken ct)
        {
            int retain;
            using (var scope = _scopes.CreateScope())
                retain = (int)await GetSettingAsync(scope.ServiceProvider.GetRequiredService<ChronicleDbContext>(), RetainKey, DefaultRetain);

            var all = ListBackups();
            // Newest first; the file just written wins any tie (two backups in the same second), so it is never the one pruned.
            static IOrderedEnumerable<BackupInfo> Newest(IEnumerable<BackupInfo> items, string keep) =>
                items.OrderByDescending(b => b.CreatedAtUtc).ThenByDescending(b => b.FileName == keep).ThenByDescending(b => b.FileName, StringComparer.Ordinal);

            foreach (var old in Newest(all.Where(b => b.Kind is BackupKinds.Scheduled or BackupKinds.Manual), keep).Skip(retain))
                DeleteQuietly(loc, old.FileName);
            foreach (var old in Newest(all.Where(b => b.Kind == BackupKinds.PreRestore), keep).Skip(PreRestoreRetain))
                DeleteQuietly(loc, old.FileName);
        }

        private void DeleteQuietly(Location loc, string name)
        {
            try { File.Delete(Path.Combine(loc.BackupDir!, name)); _log.Information("DATABASE backup pruned: {File}", name); }
            catch (Exception ex) { _log.Warning(ex, "Could not prune backup {File}", name); }
        }

        // ── list / read / delete ──────────────────────────────────────────────

        public IReadOnlyList<BackupInfo> ListBackups()
        {
            using var scope = _scopes.CreateScope();
            var loc = Locate(scope.ServiceProvider.GetRequiredService<ChronicleDbContext>());
            if (!loc.Supported || !Directory.Exists(loc.BackupDir)) return [];

            return Directory.EnumerateFiles(loc.BackupDir!, "*.zip")
                .Select(ToInfo)
                .Where(i => i is not null)
                .Select(i => i!)
                .OrderByDescending(i => i.CreatedAtUtc)
                .ToList();
        }

        private static BackupInfo? ToInfo(string path)
        {
            var name = Path.GetFileName(path);
            if (!SafeName.IsMatch(name)) return null;
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;   // deleted between listing and reading (pruning, or a person)
            var kind = name.StartsWith("pre-restore-", StringComparison.Ordinal) ? BackupKinds.PreRestore
                     : name.StartsWith("uploaded-", StringComparison.Ordinal) ? BackupKinds.Uploaded
                     : name.EndsWith("-scheduled.zip", StringComparison.Ordinal) || Regex.IsMatch(name, @"-scheduled-\d+\.zip$") ? BackupKinds.Scheduled
                     : BackupKinds.Manual;
            var manifest = TryReadManifest(path);
            return new BackupInfo(name, kind, fi.Length, manifest?.CreatedAtUtc ?? fi.LastWriteTimeUtc,
                manifest?.LatestMigration, manifest?.DbBytes, manifest?.RowCounts);
        }

        private static BackupManifest? TryReadManifest(string zipPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.GetEntry(ManifestEntryName);
                if (entry is null || entry.Length > 1024 * 1024) return null;
                using var s = entry.Open();
                return JsonSerializer.Deserialize<BackupManifest>(s, Json);
            }
            catch { return null; }
        }

        public string GetBackupPath(string fileName)
        {
            var loc = RequireSupported();
            if (string.IsNullOrWhiteSpace(fileName) || !SafeName.IsMatch(fileName) || fileName != Path.GetFileName(fileName))
                throw new DatabaseAdminException("NOT_FOUND", "No such backup.");
            var path = Path.GetFullPath(Path.Combine(loc.BackupDir!, fileName));
            // Belt and braces after the name check: the resolved file must sit directly in the backup folder.
            if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(loc.BackupDir!), StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new DatabaseAdminException("NOT_FOUND", "No such backup.");
            return path;
        }

        public bool DeleteBackup(string fileName)
        {
            var path = GetBackupPath(fileName);
            File.Delete(path);
            _log.Information("DATABASE backup deleted: {File}", fileName);
            return true;
        }

        // ── upload + validation ───────────────────────────────────────────────

        public async Task<BackupInfo> SaveUploadAsync(Stream body, CancellationToken ct = default)
        {
            var loc = RequireSupported();
            Directory.CreateDirectory(loc.BackupDir!);

            var name = $"uploaded-{_clock.GetUtcNow().UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.zip";
            var path = Path.Combine(loc.BackupDir!, name);
            var partial = path + ".partial";
            try
            {
                await using (var fs = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    await body.CopyToAsync(fs, ct);
                File.Move(partial, path);

                // The upload itself can take minutes and must not block the nightly backup; only the
                // checking (which extracts the database) takes the operation lock.
                using var _ = await AcquireAsync();
                var check = await ValidateCoreAsync(loc, path, ct);
                if (!check.Valid)
                {
                    SafeDelete(path);
                    throw new DatabaseAdminException("INVALID_BACKUP", check.Error ?? "That file is not a valid Chronicle backup.");
                }
                _log.Information("DATABASE backup uploaded and validated: {File}", name);
                return ToInfo(path)!;
            }
            catch
            {
                SafeDelete(partial);
                if (File.Exists(path) && TryReadManifest(path) is null) SafeDelete(path);
                throw;
            }
        }

        public async Task<BackupValidation> ValidateBackupAsync(string fileName, CancellationToken ct = default)
        {
            var loc = RequireSupported();
            var path = GetBackupPath(fileName);
            using var _ = await AcquireAsync();
            return await ValidateCoreAsync(loc, path, ct);
        }

        private async Task<BackupValidation> ValidateCoreAsync(Location loc, string zipPath, CancellationToken ct)
        {
            var scratch = Path.Combine(loc.BackupDir!, $"validate-{Guid.NewGuid():N}.tmp");
            try
            {
                var (manifest, error) = await ExtractAndCheckAsync(zipPath, scratch, ct);
                if (manifest is null) return new BackupValidation(false, error, null);

                var facts = ReadFacts(scratch, checkIntegrity: true);
                if (!facts.IsChronicleDb)
                    return new BackupValidation(false, "The database inside this backup is not a Chronicle database.", manifest);
                if (facts.IntegrityResult != "ok")
                    return new BackupValidation(false, $"The database inside this backup is damaged (integrity check: {Truncate(facts.IntegrityResult, 120)}).", manifest);

                using var scope = _scopes.CreateScope();
                var known = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>().Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
                if (facts.LatestMigration is not null && !known.Contains(facts.LatestMigration))
                    return new BackupValidation(false,
                        $"This backup was made by a newer version of Chronicle (database version {facts.LatestMigration}). Update Chronicle first, then restore it.", manifest);

                return new BackupValidation(true, null, manifest);
            }
            catch (InvalidDataException)
            {
                return new BackupValidation(false, "That file is not a readable zip archive.", null);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                SafeDelete(scratch);
            }
        }

        /// <summary>Checks the zip's structure, extracts the database to <paramref name="destination"/> and verifies its hash and size.</summary>
        private static async Task<(BackupManifest? Manifest, string? Error)> ExtractAndCheckAsync(string zipPath, string destination, CancellationToken ct)
        {
            using var zip = ZipFile.OpenRead(zipPath);

            // Exactly the two expected entries, no paths: a crafted archive cannot write anywhere else.
            if (zip.Entries.Count != 2 || zip.Entries.Any(e => e.FullName != e.Name || (e.Name != DbEntryName && e.Name != ManifestEntryName)))
                return (null, "This zip is not a Chronicle backup (unexpected contents).");

            var manifestEntry = zip.GetEntry(ManifestEntryName)!;
            var dbEntry = zip.GetEntry(DbEntryName)!;
            if (manifestEntry.Length > 1024 * 1024) return (null, "The backup's manifest is unreasonably large.");

            BackupManifest? manifest;
            try
            {
                await using var ms = manifestEntry.Open();
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(ms, Json, ct);
            }
            catch (JsonException) { return (null, "The backup's manifest is not readable."); }

            if (manifest is null || manifest.Format != 1 || string.IsNullOrWhiteSpace(manifest.Sha256))
                return (null, "The backup's manifest is missing or from an unknown format.");
            if (!manifest.Provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                return (null, "This backup is not from a SQLite database.");
            if (manifest.DbBytes != dbEntry.Length)
                return (null, "The backup is incomplete or has been altered (size does not match its manifest).");

            using var sha = SHA256.Create();
            await using (var src = dbEntry.Open())
            await using (var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var crypto = new CryptoStream(dst, sha, CryptoStreamMode.Write))
                await src.CopyToAsync(crypto, ct);

            var actual = Convert.ToHexString(sha.Hash!);
            if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                return (null, "The backup has been altered or damaged (checksum does not match its manifest).");

            return (manifest, null);
        }

        // ── restore ───────────────────────────────────────────────────────────

        public async Task StageRestoreAsync(string fileName, CancellationToken ct = default)
        {
            var loc = RequireSupported();
            var path = GetBackupPath(fileName);
            using var _ = await AcquireAsync();

            var check = await ValidateCoreAsync(loc, path, ct);
            if (!check.Valid) throw new DatabaseAdminException("INVALID_BACKUP", check.Error ?? "That backup cannot be restored.");

            // The restore briefly holds a safety snapshot of the live database AND an extracted copy of the
            // chosen one beside it; refuse up front rather than fail halfway.
            var needed = FileLength(loc.DbFile!) + (check.Manifest?.DbBytes ?? 0) * 2 + 64L * 1024 * 1024;
            var free = _freeDisk(loc.Dir!);
            if (free < needed)
                throw new DatabaseAdminException("LOW_DISK",
                    $"Not enough free disk space to restore safely: about {Format(needed)} needed, {Format(free)} available.");

            // Safety net first: if the restore turns out to be a mistake, the current data is one click away.
            var safety = await CreateBackupCoreAsync(loc, BackupKinds.PreRestore, ct);

            var pending = loc.PendingFile!;
            var partial = pending + ".partial";
            try
            {
                var (manifest, error) = await ExtractAndCheckAsync(path, partial, ct);
                if (manifest is null) throw new DatabaseAdminException("INVALID_BACKUP", error ?? "That backup cannot be restored.");
                SafeDelete(pending);
                File.Move(partial, pending);
            }
            finally { SafeDelete(partial); }

            _log.Warning("DATABASE restore staged from {File}; safety backup {Safety}; takes effect at the next start", fileName, safety.FileName);
        }

        // ── maintenance ───────────────────────────────────────────────────────

        public async Task<MaintenanceResult> RunMaintenanceAsync(bool heavy, CancellationToken ct = default)
        {
            var loc = RequireSupported();
            using var _ = await AcquireAsync();
            Directory.CreateDirectory(loc.TempDir!);

            var before = FileLength(loc.DbFile!);
            if (heavy)
            {
                // VACUUM rewrites the whole database, so it briefly needs about that much extra space.
                var needed = (long)(before * 1.2) + 64L * 1024 * 1024;
                var free = _freeDisk(loc.Dir!);
                if (free < needed)
                    throw new DatabaseAdminException("LOW_DISK",
                        $"Not enough free disk space for a full rebuild: {Format(needed)} needed, {Format(free)} available.");
            }

            var sw = Stopwatch.StartNew();
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.Database.SetCommandTimeout(TimeSpan.FromHours(2));
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                var conn = db.Database.GetDbConnection();
                // SQLite spills sorts and rebuilds to a temp folder; keep that beside the database.
                await ExecAsync(conn, $"PRAGMA temp_store_directory = '{loc.TempDir!.Replace("'", "''")}'", ct);
                var steps = heavy
                    ? new[] { "REINDEX", "VACUUM", "PRAGMA wal_checkpoint(TRUNCATE)" }
                    : new[] { "PRAGMA optimize", "ANALYZE", "PRAGMA wal_checkpoint(TRUNCATE)" };
                foreach (var step in steps) await ExecAsync(conn, step, ct);
            }
            finally { await db.Database.CloseConnectionAsync(); }

            var after = FileLength(loc.DbFile!);
            var summary = heavy
                ? $"Rebuilt indexes and compacted the database: {Format(before)} -> {Format(after)}."
                : "Refreshed query statistics and trimmed the write-ahead log.";
            _log.Information("DATABASE maintenance ({Kind}) finished in {Elapsed} ms: {Summary}", heavy ? "heavy" : "light", sw.ElapsedMilliseconds, summary);
            return new MaintenanceResult(summary, sw.Elapsed, before, after);
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private sealed record Facts(bool IsChronicleDb, string? LatestMigration, string IntegrityResult, IReadOnlyDictionary<string, long> RowCounts);

        private static Facts ReadFacts(string dbPath, bool checkIntegrity)
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString;
            using var conn = new SqliteConnection(cs);
            try
            {
                conn.Open();
                var integrity = "not checked";
                if (checkIntegrity)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA integrity_check";
                    integrity = Convert.ToString(cmd.ExecuteScalar()) ?? "no result";
                }

                var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) tables.Add(r.GetString(0));
                }

                var isChronicle = tables.Contains("users") && tables.Contains("media_items") && tables.Contains("__EFMigrationsHistory");
                string? latest = null;
                if (tables.Contains("__EFMigrationsHistory"))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT MigrationId FROM \"__EFMigrationsHistory\" ORDER BY MigrationId DESC LIMIT 1";
                    latest = cmd.ExecuteScalar() as string;
                }

                var counts = new Dictionary<string, long>();
                foreach (var t in CountedTables.Where(tables.Contains))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM \"{t}\"";
                    counts[t] = Convert.ToInt64(cmd.ExecuteScalar());
                }
                return new Facts(isChronicle, latest, integrity, counts);
            }
            catch (SqliteException ex)
            {
                // "file is not a database", corrupt header, etc.
                return new Facts(false, null, Truncate(ex.Message, 120), new Dictionary<string, long>());
            }
            finally { SqliteConnection.ClearAllPools(); }
        }

        private static async Task<(string Sha256, long Length)> HashFileAsync(string path, CancellationToken ct)
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = await SHA256.HashDataAsync(fs, ct);
            return (Convert.ToHexString(hash), fs.Length);
        }

        private static async Task ExecAsync(System.Data.Common.DbConnection conn, string sql, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task<long> ScalarLongAsync(System.Data.Common.DbConnection conn, string sql, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        }

        private static async Task<string?> ScalarStringAsync(System.Data.Common.DbConnection conn, string sql, CancellationToken ct)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                return await cmd.ExecuteScalarAsync(ct) as string;
            }
            catch (Exception) { return null; }
        }

        private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

        private static long FreeDiskOf(string dir)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
            catch { return long.MaxValue; }   // unknown (e.g. a network path): do not block on a guess
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        private static string AppVersion() =>
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "unknown";

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

        public static string Format(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double v = bytes; var i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return $"{v:0.#} {units[i]}";
        }
    }
}
