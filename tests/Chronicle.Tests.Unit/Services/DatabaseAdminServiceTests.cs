using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Database;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Unit.Services
{
    /// <summary>
    /// Real SQLite files in a private scratch folder per test (these tests are not Chronicle's runtime,
    /// so they may use the system temp folder; they clean up after themselves).
    /// </summary>
    public sealed class DatabaseAdminServiceTests : IDisposable
    {
        private sealed class FakeClock : TimeProvider
        {
            private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }

        private readonly string _root = Path.Combine(Path.GetTempPath(), "chronicle-dbadmin-tests-" + Guid.NewGuid().ToString("N"));
        private readonly string _dbFile;
        private readonly ServiceProvider _services;
        private readonly FakeClock _clock = new();
        private readonly DatabaseAdminService _admin;
        private readonly string _latestMigration;

        private string BackupDir => Path.Combine(_root, "backups");

        public DatabaseAdminServiceTests()
        {
            Directory.CreateDirectory(_root);
            _dbFile = Path.Combine(_root, "chronicle.db");
            _services = new ServiceCollection()
                .AddDbContext<ChronicleDbContext>(o => o.UseSqlite($"Data Source={_dbFile};Pooling=False"))
                .BuildServiceProvider();

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.Database.EnsureCreated();
            // EnsureCreated builds the schema without migration history; stamp it the way a migrated database looks.
            _latestMigration = db.Database.GetMigrations().Last();
            db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL)");
            db.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" VALUES ({0}, '9.0.0')", _latestMigration);
            db.Users.Add(new User { Username = "alice", PasswordHash = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, IsActive = true });
            db.SaveChanges();

            _admin = new DatabaseAdminService(_services.GetRequiredService<IServiceScopeFactory>(), _clock);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _services.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* scratch */ }
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private int UserCount()
        {
            using var scope = _services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ChronicleDbContext>().Users.Count();
        }

        private void AddUser(string name)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.Users.Add(new User { Username = name, PasswordHash = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, IsActive = true });
            db.SaveChanges();
        }

        private void SetSetting(string key, string value)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.AppSettings.Add(new AppSetting { Key = key, Value = value });
            db.SaveChanges();
        }

        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>Builds a backup zip by hand so each way of being wrong can be produced exactly.</summary>
        private string MakeZip(string name, byte[] dbBytes, Func<BackupManifest, BackupManifest>? tweakManifest = null,
            bool includeManifest = true, string dbEntryName = DatabaseAdminService.DbEntryName, string[]? extraEntries = null)
        {
            Directory.CreateDirectory(BackupDir);
            var manifest = new BackupManifest(1, DateTime.UtcNow, "test", "Microsoft.EntityFrameworkCore.Sqlite",
                _latestMigration, DatabaseAdminService.DbEntryName, dbBytes.Length,
                Convert.ToHexString(SHA256.HashData(dbBytes)), new Dictionary<string, long>());
            if (tweakManifest is not null) manifest = tweakManifest(manifest);

            var path = Path.Combine(BackupDir, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var s = zip.CreateEntry(dbEntryName).Open()) s.Write(dbBytes);
            if (includeManifest)
                using (var s = zip.CreateEntry(DatabaseAdminService.ManifestEntryName).Open())
                    JsonSerializer.Serialize(s, manifest, Camel);
            foreach (var extra in extraEntries ?? [])
                using (var s = zip.CreateEntry(extra).Open()) s.Write([1, 2, 3]);
            return path;
        }

        /// <summary>A byte-for-byte snapshot of the live database, taken the same way a backup is.</summary>
        private async Task<byte[]> SnapshotBytesAsync()
        {
            var info = await _admin.CreateBackupAsync(BackupKinds.Manual);
            var path = Path.Combine(BackupDir, info.FileName);
            byte[] bytes;
            using (var zip = ZipFile.OpenRead(path))
            using (var ms = new MemoryStream())
            {
                using (var s = zip.GetEntry(DatabaseAdminService.DbEntryName)!.Open()) s.CopyTo(ms);
                bytes = ms.ToArray();
            }
            File.Delete(path);
            return bytes;
        }

        // ══ backup ════════════════════════════════════════════════════════════

        [Fact]
        public async Task Backup_ProducesAZipWithTheDatabaseAndAManifestThatMatchesIt()
        {
            var info = await _admin.CreateBackupAsync(BackupKinds.Manual);

            info.Kind.Should().Be(BackupKinds.Manual);
            info.LatestMigration.Should().Be(_latestMigration);
            info.RowCounts!["users"].Should().Be(1);

            using var zip = ZipFile.OpenRead(Path.Combine(BackupDir, info.FileName));
            zip.Entries.Select(e => e.Name).Should().BeEquivalentTo([DatabaseAdminService.DbEntryName, DatabaseAdminService.ManifestEntryName]);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(zip.GetEntry(DatabaseAdminService.ManifestEntryName)!.Open(), Camel)!;
            using var db = zip.GetEntry(DatabaseAdminService.DbEntryName)!.Open();
            Convert.ToHexString(SHA256.HashData(db)).Should().Be(manifest.Sha256);
        }

        [Fact]
        public async Task Backup_IsAFaithfulCopy_ThatCanBeValidated_AndIgnoresLaterChanges()
        {
            var info = await _admin.CreateBackupAsync(BackupKinds.Manual);
            AddUser("bob");   // made after the backup

            var check = await _admin.ValidateBackupAsync(info.FileName);

            check.Valid.Should().BeTrue(check.Error);
            check.Manifest!.RowCounts["users"].Should().Be(1, "the backup holds the data as it was when taken");
            UserCount().Should().Be(2);
        }

        [Fact]
        public async Task Backup_LeavesNoScratchFilesBehind()
        {
            await _admin.CreateBackupAsync(BackupKinds.Manual);

            Directory.EnumerateFiles(BackupDir).Select(Path.GetFileName)
                .Should().OnlyContain(n => n!.EndsWith(".zip"), "snapshots and partial files are removed");
        }

        [Fact]
        public async Task TwoBackupsInTheSameSecond_DoNotOverwriteEachOther()
        {
            var a = await _admin.CreateBackupAsync(BackupKinds.Manual);
            var b = await _admin.CreateBackupAsync(BackupKinds.Manual);

            a.FileName.Should().NotBe(b.FileName);
            _admin.ListBackups().Should().HaveCount(2);
        }

        [Fact]
        public async Task Retention_KeepsTheNewestN_AndOnlyOfRegularBackups()
        {
            SetSetting(DatabaseAdminService.RetainKey, "3");
            for (var i = 0; i < 5; i++)
            {
                await _admin.CreateBackupAsync(BackupKinds.Manual);
                _clock.Advance(TimeSpan.FromMinutes(1));
            }
            await _admin.CreateBackupAsync(BackupKinds.PreRestore);

            var list = _admin.ListBackups();

            list.Count(b => b.Kind == BackupKinds.Manual).Should().Be(3);
            list.Count(b => b.Kind == BackupKinds.PreRestore).Should().Be(1, "safety backups are counted separately");
        }

        [Fact]
        public async Task Retention_KeepsTheNewest_NotTheOldest()
        {
            SetSetting(DatabaseAdminService.RetainKey, "2");
            var made = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                made.Add((await _admin.CreateBackupAsync(BackupKinds.Scheduled)).FileName);
                _clock.Advance(TimeSpan.FromHours(1));
            }

            _admin.ListBackups().Select(b => b.FileName).Should().BeEquivalentTo(made.Skip(2));
        }

        [Fact]
        public async Task PreRestoreBackups_KeepOnlyTheNewestThree()
        {
            for (var i = 0; i < 5; i++)
            {
                await _admin.CreateBackupAsync(BackupKinds.PreRestore);
                _clock.Advance(TimeSpan.FromMinutes(1));
            }

            _admin.ListBackups().Count(b => b.Kind == BackupKinds.PreRestore).Should().Be(DatabaseAdminService.PreRestoreRetain);
        }

        [Fact]
        public async Task UploadedBackups_AreNeverPrunedAutomatically()
        {
            SetSetting(DatabaseAdminService.RetainKey, "1");
            using var up = new MemoryStream(await File.ReadAllBytesAsync((await MakeZipFromLiveAsync("seed.zip"))));
            await _admin.SaveUploadAsync(up);
            for (var i = 0; i < 3; i++) { await _admin.CreateBackupAsync(BackupKinds.Manual); _clock.Advance(TimeSpan.FromMinutes(1)); }

            _admin.ListBackups().Should().ContainSingle(b => b.Kind == BackupKinds.Uploaded);
        }

        private async Task<string> MakeZipFromLiveAsync(string name) => MakeZip(name, await SnapshotBytesAsync());

        [Theory]
        [InlineData("scheduled", "x")]
        [InlineData("bogus", "x")]
        public async Task AnUnknownKind_IsRefused(string kind, string _)
        {
            if (kind == "scheduled") return;   // valid kind: covered elsewhere
            await FluentActions.Awaiting(() => _admin.CreateBackupAsync(kind)).Should().ThrowAsync<ArgumentException>();
        }

        // ══ list / names ══════════════════════════════════════════════════════

        [Fact]
        public async Task List_ReportsKindsFromTheirNames_NewestFirst()
        {
            var m = await _admin.CreateBackupAsync(BackupKinds.Manual);
            _clock.Advance(TimeSpan.FromHours(1));
            var s = await _admin.CreateBackupAsync(BackupKinds.Scheduled);

            var list = _admin.ListBackups();

            list.Select(b => b.FileName).Should().Equal(s.FileName, m.FileName);
            list[0].Kind.Should().Be(BackupKinds.Scheduled);
            list[1].Kind.Should().Be(BackupKinds.Manual);
        }

        [Fact]
        public void List_IgnoresFilesThatAreNotBackups()
        {
            Directory.CreateDirectory(BackupDir);
            File.WriteAllText(Path.Combine(BackupDir, "notes.txt"), "hi");
            File.WriteAllText(Path.Combine(BackupDir, "bad name!.zip"), "hi");

            _admin.ListBackups().Should().BeEmpty();
        }

        [Theory]
        [InlineData("../chronicle.db")]
        [InlineData("..\\chronicle.db")]
        [InlineData("../../etc/passwd")]
        [InlineData("C:\\Windows\\win.ini")]
        [InlineData("/etc/passwd")]
        [InlineData("backup.txt")]
        [InlineData("")]
        [InlineData("nested/backup.zip")]
        [InlineData("does-not-exist.zip")]
        [InlineData("chronicle.db")]
        public void GetBackupPath_RefusesAnythingButARealBackupInTheFolder(string name)
        {
            Directory.CreateDirectory(BackupDir);
            File.WriteAllText(Path.Combine(_root, "secret.zip"), "x");

            Action act = () => _admin.GetBackupPath(name);

            act.Should().Throw<DatabaseAdminException>().Which.Code.Should().Be("NOT_FOUND");
        }

        [Fact]
        public async Task Delete_RemovesTheFile_AndOnlyThatFile()
        {
            var a = await _admin.CreateBackupAsync(BackupKinds.Manual);
            var b = await _admin.CreateBackupAsync(BackupKinds.Manual);

            _admin.DeleteBackup(a.FileName);

            _admin.ListBackups().Select(x => x.FileName).Should().Equal(b.FileName);
            File.Exists(_dbFile).Should().BeTrue();
        }

        // ══ validation ════════════════════════════════════════════════════════

        [Fact]
        public async Task Validation_AcceptsAGoodBackup()
        {
            var zip = await MakeZipFromLiveAsync("good.zip");

            (await _admin.ValidateBackupAsync(Path.GetFileName(zip))).Valid.Should().BeTrue();
        }

        [Fact]
        public async Task Validation_RejectsSomethingThatIsNotAZip()
        {
            Directory.CreateDirectory(BackupDir);
            File.WriteAllBytes(Path.Combine(BackupDir, "junk.zip"), new byte[2048]);

            var check = await _admin.ValidateBackupAsync("junk.zip");

            check.Valid.Should().BeFalse();
            check.Error.Should().Contain("zip");
        }

        [Fact]
        public async Task Validation_RejectsAZipWithoutAManifest()
        {
            MakeZip("nomanifest.zip", await SnapshotBytesAsync(), includeManifest: false);

            (await _admin.ValidateBackupAsync("nomanifest.zip")).Valid.Should().BeFalse();
        }

        [Fact]
        public async Task Validation_RejectsExtraEntries_IncludingPathTraversalNames()
        {
            var bytes = await SnapshotBytesAsync();
            MakeZip("extra.zip", bytes, extraEntries: ["notes.txt"]);
            MakeZip("slip.zip", bytes, extraEntries: ["../../evil.dll"]);
            MakeZip("slip2.zip", bytes, extraEntries: ["sub/dir.txt"]);

            foreach (var name in new[] { "extra.zip", "slip.zip", "slip2.zip" })
            {
                var check = await _admin.ValidateBackupAsync(name);
                check.Valid.Should().BeFalse(name);
                check.Error.Should().Contain("unexpected contents");
            }
            File.Exists(Path.Combine(_root, "..", "evil.dll")).Should().BeFalse();
        }

        [Fact]
        public async Task Validation_RejectsABackupWhoseDatabaseWasAltered()
        {
            var bytes = await SnapshotBytesAsync();
            var original = new Dictionary<string, string>();
            var tampered = (byte[])bytes.Clone();
            tampered[tampered.Length / 2] ^= 0xFF;                       // same size, one byte different
            MakeZip("tampered.zip", tampered, m => m with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });

            var check = await _admin.ValidateBackupAsync("tampered.zip");

            check.Valid.Should().BeFalse();
            check.Error.Should().Contain("checksum");
        }

        [Fact]
        public async Task Validation_RejectsASizeThatDoesNotMatchTheManifest()
        {
            MakeZip("size.zip", await SnapshotBytesAsync(), m => m with { DbBytes = m.DbBytes + 1 });

            var check = await _admin.ValidateBackupAsync("size.zip");

            check.Valid.Should().BeFalse();
            check.Error.Should().Contain("size");
        }

        [Fact]
        public async Task Validation_RejectsANonSqliteManifest_AndAnUnknownFormat()
        {
            var bytes = await SnapshotBytesAsync();
            MakeZip("pg.zip", bytes, m => m with { Provider = "Npgsql.EntityFrameworkCore.PostgreSQL" });
            MakeZip("v2.zip", bytes, m => m with { Format = 2 });

            (await _admin.ValidateBackupAsync("pg.zip")).Valid.Should().BeFalse();
            (await _admin.ValidateBackupAsync("v2.zip")).Valid.Should().BeFalse();
        }

        [Fact]
        public void Validation_RejectsAFileThatIsNotADatabase_EvenWithAMatchingChecksum()
        {
            var garbage = new byte[4096];
            new Random(1).NextBytes(garbage);
            MakeZip("garbage.zip", garbage);

            var check = _admin.ValidateBackupAsync("garbage.zip").Result;

            check.Valid.Should().BeFalse();
            check.Error.Should().NotContain("checksum");
        }

        [Fact]
        public void Validation_RejectsAValidSqliteDatabaseThatIsNotChronicles()
        {
            var other = Path.Combine(_root, "other.db");
            using (var c = new SqliteConnection($"Data Source={other};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE recipes (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO recipes VALUES (1, 'soup');";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            MakeZip("recipes.zip", File.ReadAllBytes(other));

            var check = _admin.ValidateBackupAsync("recipes.zip").Result;

            check.Valid.Should().BeFalse();
            check.Error.Should().Contain("not a Chronicle database");
        }

        [Fact]
        public async Task Validation_RejectsABackupFromANewerVersion_WithAnUpdateHint()
        {
            var copy = Path.Combine(_root, "newer.db");
            File.WriteAllBytes(copy, await SnapshotBytesAsync());
            using (var c = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO \"__EFMigrationsHistory\" VALUES ('99991231000000_FromTheFuture', '99.0.0')";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            MakeZip("newer.zip", File.ReadAllBytes(copy));

            var check = await _admin.ValidateBackupAsync("newer.zip");

            check.Valid.Should().BeFalse();
            check.Error.Should().Contain("newer version");
        }

        [Fact]
        public async Task Validation_AcceptsABackupFromAnOlderVersion_ItMigratesWhenRestored()
        {
            var copy = Path.Combine(_root, "older.db");
            File.WriteAllBytes(copy, await SnapshotBytesAsync());
            using (var c = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"DELETE FROM \"__EFMigrationsHistory\" WHERE MigrationId = '{_latestMigration}'";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            MakeZip("older.zip", File.ReadAllBytes(copy));

            (await _admin.ValidateBackupAsync("older.zip")).Valid.Should().BeTrue();
        }

        [Fact]
        public async Task Validation_LeavesNoScratchFilesBehind()
        {
            MakeZip("size.zip", await SnapshotBytesAsync(), m => m with { DbBytes = 1 });
            await _admin.ValidateBackupAsync("size.zip");
            await MakeZipFromLiveAsync("ok.zip");
            await _admin.ValidateBackupAsync("ok.zip");

            Directory.EnumerateFiles(BackupDir).Select(Path.GetFileName).Should().OnlyContain(n => n!.EndsWith(".zip"));
        }

        // ══ upload ════════════════════════════════════════════════════════════

        [Fact]
        public async Task Upload_OfAGoodBackup_IsKeptAndListed()
        {
            var bytes = File.ReadAllBytes(await MakeZipFromLiveAsync("source.zip"));
            File.Delete(Path.Combine(BackupDir, "source.zip"));

            var info = await _admin.SaveUploadAsync(new MemoryStream(bytes));

            info.Kind.Should().Be(BackupKinds.Uploaded);
            info.FileName.Should().StartWith("uploaded-");
            _admin.ListBackups().Should().ContainSingle();
        }

        [Fact]
        public async Task Upload_OfGarbage_IsRejected_AndNothingIsLeftBehind()
        {
            var act = () => _admin.SaveUploadAsync(new MemoryStream(new byte[5000]));

            (await act.Should().ThrowAsync<DatabaseAdminException>()).Which.Code.Should().Be("INVALID_BACKUP");
            Directory.EnumerateFiles(BackupDir).Should().BeEmpty();
        }

        [Fact]
        public async Task Upload_OfATamperedBackup_IsRejected()
        {
            var bytes = await SnapshotBytesAsync();
            bytes[bytes.Length / 2] ^= 0xFF;
            var path = MakeZip("t.zip", bytes, m => m with { Sha256 = new string('0', 64) });
            var zipBytes = File.ReadAllBytes(path);
            File.Delete(path);

            var act = () => _admin.SaveUploadAsync(new MemoryStream(zipBytes));

            await act.Should().ThrowAsync<DatabaseAdminException>();
            Directory.EnumerateFiles(BackupDir).Should().BeEmpty();
        }

        // ══ restore ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task Restore_TakesASafetyBackup_AndStagesTheChosenOne()
        {
            var good = await _admin.CreateBackupAsync(BackupKinds.Manual);
            AddUser("bob");

            await _admin.StageRestoreAsync(good.FileName);

            File.Exists(_dbFile + PendingRestore.Suffix).Should().BeTrue();
            _admin.ListBackups().Should().ContainSingle(b => b.Kind == BackupKinds.PreRestore);
            UserCount().Should().Be(2, "nothing changes until the restart");
        }

        [Fact]
        public async Task Restore_OfAnInvalidBackup_ChangesNothing()
        {
            MakeZip("bad.zip", await SnapshotBytesAsync(), m => m with { Sha256 = new string('0', 64) });

            var act = () => _admin.StageRestoreAsync("bad.zip");

            (await act.Should().ThrowAsync<DatabaseAdminException>()).Which.Code.Should().Be("INVALID_BACKUP");
            File.Exists(_dbFile + PendingRestore.Suffix).Should().BeFalse();
            _admin.ListBackups().Should().NotContain(b => b.Kind == BackupKinds.PreRestore, "no safety backup is wasted on a restore that was refused");
        }

        [Fact]
        public async Task EndToEnd_RestoreBringsBackTheOldData_AndKeepsWhatItReplaced()
        {
            var good = await _admin.CreateBackupAsync(BackupKinds.Manual);   // one user
            AddUser("bob"); AddUser("carol");                                 // now three
            await _admin.StageRestoreAsync(good.FileName);

            SqliteConnection.ClearAllPools();
            _services.Dispose();   // "the application stops": nothing holds the file any more
            File.Exists(_dbFile + "-wal").Should().BeFalse("closing the last connection checkpoints and removes the log");

            var applied = PendingRestore.ApplyIfPresent(_dbFile, _ => { }, _clock);
            applied.Should().BeTrue();

            using var c = new SqliteConnection($"Data Source={_dbFile};Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM users";
            Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(1, "the backup had one user");
            File.Exists(_dbFile + PendingRestore.Suffix).Should().BeFalse();
            Directory.GetFiles(_root, "chronicle.db.replaced-*").Should().ContainSingle("the database it replaced is kept");
        }

        // ══ pending restore on its own ════════════════════════════════════════

        [Fact]
        public void NoPendingFile_MeansNothingHappens()
        {
            var db = Path.Combine(_root, "plain.db");
            File.WriteAllText(db, "data");

            PendingRestore.ApplyIfPresent(db, _ => { }).Should().BeFalse();
            File.ReadAllText(db).Should().Be("data");
        }

        [Fact]
        public void ApplyingARestore_ReplacesTheDatabase_RemovesTheOldLog_AndKeepsOnlyTwoReplacedCopies()
        {
            var db = Path.Combine(_root, "swap.db");
            var messages = new List<string>();
            for (var round = 1; round <= 4; round++)
            {
                File.WriteAllText(db, $"old-{round}");
                File.WriteAllText(db + "-wal", "stale log");
                File.WriteAllText(db + "-shm", "stale shm");
                File.WriteAllText(db + PendingRestore.Suffix, $"new-{round}");
                _clock.Advance(TimeSpan.FromSeconds(5));

                PendingRestore.ApplyIfPresent(db, messages.Add, _clock).Should().BeTrue();

                File.ReadAllText(db).Should().Be($"new-{round}");
                File.Exists(db + "-wal").Should().BeFalse();
                File.Exists(db + "-shm").Should().BeFalse();
            }
            Directory.GetFiles(_root, "swap.db.replaced-*").Should().HaveCount(2);
            File.ReadAllText(Directory.GetFiles(_root, "swap.db.replaced-*").OrderBy(x => x).Last()).Should().Be("old-4");
            messages.Should().HaveCount(4);
        }

        [Fact]
        public void IfTheSwapFails_TheExistingDatabaseIsKept_AndTheStagedFileIsLeftForARetry()
        {
            if (!OperatingSystem.IsWindows()) return;   // relies on Windows refusing to move an open file
            var db = Path.Combine(_root, "locked.db");
            File.WriteAllText(db, "current");
            File.WriteAllText(db + PendingRestore.Suffix, "staged");
            var messages = new List<string>();

            using (new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.None))
                PendingRestore.ApplyIfPresent(db, messages.Add).Should().BeFalse();

            File.ReadAllText(db).Should().Be("current");
            File.Exists(db + PendingRestore.Suffix).Should().BeTrue();
            messages.Should().ContainSingle().Which.Should().Contain("could NOT be applied");
        }

        // ══ maintenance ═══════════════════════════════════════════════════════

        [Fact]
        public async Task QuickMaintenance_Runs()
        {
            var result = await _admin.RunMaintenanceAsync(heavy: false);

            result.Summary.Should().Contain("statistics");
        }

        [Fact]
        public async Task FullMaintenance_ReclaimsSpace_AfterBulkDeletes()
        {
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                db.Database.ExecuteSqlRaw("CREATE TABLE junk (id INTEGER PRIMARY KEY, blob BLOB)");
                for (var i = 0; i < 200; i++)
                    db.Database.ExecuteSqlRaw("INSERT INTO junk (blob) VALUES (randomblob(20000))");
                db.Database.ExecuteSqlRaw("DELETE FROM junk");
            }
            var before = new FileInfo(_dbFile).Length;

            var result = await _admin.RunMaintenanceAsync(heavy: true);

            result.BytesAfter.Should().BeLessThan(before / 2, "freed pages are returned to the file system");
            result.Summary.Should().Contain("compacted");
            UserCount().Should().Be(1, "maintenance never changes data");
        }

        [Fact]
        public async Task Maintenance_PointsSqliteScratchFilesBesideTheDatabase()
        {
            await _admin.RunMaintenanceAsync(heavy: true);

            Directory.Exists(Path.Combine(_root, "temp")).Should().BeTrue();
        }

        // ══ disk space ════════════════════════════════════════════════════════

        private DatabaseAdminService WithFreeDisk(long free) =>
            new(_services.GetRequiredService<IServiceScopeFactory>(), _clock, _ => free);

        [Fact]
        public async Task FullMaintenance_IsRefused_WhenThereIsNotRoomToRebuild()
        {
            var admin = WithFreeDisk(1024);

            var act = () => admin.RunMaintenanceAsync(heavy: true);

            (await act.Should().ThrowAsync<DatabaseAdminException>()).Which.Code.Should().Be("LOW_DISK");
            UserCount().Should().Be(1);
        }

        [Fact]
        public async Task QuickMaintenance_NeedsNoSpareDisk()
        {
            await WithFreeDisk(1024).RunMaintenanceAsync(heavy: false);
        }

        [Fact]
        public async Task Restore_IsRefused_WhenThereIsNotRoomForTheSafetyBackupAndTheStagedCopy_AndChangesNothing()
        {
            var good = await _admin.CreateBackupAsync(BackupKinds.Manual);
            var tight = WithFreeDisk(1024);

            var act = () => tight.StageRestoreAsync(good.FileName);

            (await act.Should().ThrowAsync<DatabaseAdminException>()).Which.Code.Should().Be("LOW_DISK");
            File.Exists(_dbFile + PendingRestore.Suffix).Should().BeFalse();
            _admin.ListBackups().Should().NotContain(b => b.Kind == BackupKinds.PreRestore);
        }

        [Fact]
        public async Task Status_ReportsFreeDiskFromTheProbe()
        {
            (await WithFreeDisk(123456789).GetStatusAsync()).FreeDiskBytes.Should().Be(123456789);
        }

        [Fact]
        public async Task Retention_NeverPrunesTheBackupJustTaken_EvenWhenTimestampsTie()
        {
            SetSetting(DatabaseAdminService.RetainKey, "1");
            await _admin.CreateBackupAsync(BackupKinds.Manual);          // same fake second as the next one

            var second = await _admin.CreateBackupAsync(BackupKinds.Manual);

            File.Exists(Path.Combine(BackupDir, second.FileName)).Should().BeTrue("the caller was just handed this file");
            _admin.ListBackups().Should().ContainSingle().Which.FileName.Should().Be(second.FileName);
        }

        [Fact]
        public async Task AnUploadInProgress_DoesNotBlockABackup()
        {
            // A slow upload copies its bytes outside the operation lock, so the nightly backup still runs.
            var gate = new TaskCompletionSource();
            var slow = new GatedStream(gate.Task);
            var upload = _admin.SaveUploadAsync(slow);

            var backup = await _admin.CreateBackupAsync(BackupKinds.Scheduled);

            backup.Kind.Should().Be(BackupKinds.Scheduled);
            gate.SetResult();
            await upload.Invoking(t => t).Should().ThrowAsync<DatabaseAdminException>("the bytes were not a backup");
        }

        /// <summary>A stream that pauses mid-read until released, then yields junk and ends.</summary>
        private sealed class GatedStream(Task gate) : Stream
        {
            private bool _sent;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                await gate;
                if (_sent) return 0;
                _sent = true;
                buffer.Span[..100].Clear();
                return 100;
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                await ReadAsync(buffer.AsMemory(offset, count), ct);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // ══ status ════════════════════════════════════════════════════════════

        [Fact]
        public async Task Status_ReportsSizesBackupsAndTheLatestMigration()
        {
            await _admin.CreateBackupAsync(BackupKinds.Manual);

            var status = await _admin.GetStatusAsync();

            status.Supported.Should().BeTrue();
            status.DatabaseFile.Should().Be(_dbFile);
            status.DatabaseBytes.Should().BeGreaterThan(0);
            status.PageSize.Should().BeGreaterThan(0);
            status.LatestMigration.Should().Be(_latestMigration);
            status.BackupCount.Should().Be(1);
            status.LastBackupAtUtc.Should().NotBeNull();
            status.RetainCount.Should().Be(DatabaseAdminService.DefaultRetain);
            status.BackupDirectory.Should().Be(BackupDir);
            status.OverWarnSize.Should().BeFalse();
        }

        [Fact]
        public async Task Status_FlagsADatabaseOverTheConfiguredWarningSize()
        {
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                db.Database.ExecuteSqlRaw("CREATE TABLE junk (id INTEGER PRIMARY KEY, blob BLOB)");
                for (var i = 0; i < 120; i++) db.Database.ExecuteSqlRaw("INSERT INTO junk (blob) VALUES (randomblob(20000))");
            }
            SetSetting(DatabaseAdminService.WarnSizeMbKey, "1");

            (await _admin.GetStatusAsync()).OverWarnSize.Should().BeTrue();
        }

        [Fact]
        public async Task Status_ReportsFreePagesAfterDeletes()
        {
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                db.Database.ExecuteSqlRaw("CREATE TABLE junk (id INTEGER PRIMARY KEY, blob BLOB)");
                for (var i = 0; i < 50; i++) db.Database.ExecuteSqlRaw("INSERT INTO junk (blob) VALUES (randomblob(20000))");
                db.Database.ExecuteSqlRaw("DELETE FROM junk");
            }

            var status = await _admin.GetStatusAsync();

            status.FreePages.Should().BeGreaterThan(0);
            status.ReclaimableBytes.Should().Be(status.FreePages * status.PageSize);
        }

        // ══ unsupported provider ══════════════════════════════════════════════

        [Fact]
        public async Task OnANonSqliteDatabase_EverythingSaysSo_AndNothingThrowsUnexpectedly()
        {
            var services = new ServiceCollection()
                .AddDbContext<ChronicleDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()))
                .BuildServiceProvider();
            var admin = new DatabaseAdminService(services.GetRequiredService<IServiceScopeFactory>());

            var status = await admin.GetStatusAsync();
            status.Supported.Should().BeFalse();
            status.Note.Should().NotBeNullOrWhiteSpace();
            admin.ListBackups().Should().BeEmpty();

            foreach (var act in new Func<Task>[]
            {
                () => admin.CreateBackupAsync(BackupKinds.Manual),
                () => admin.RunMaintenanceAsync(false),
                () => admin.StageRestoreAsync("x.zip"),
                () => admin.SaveUploadAsync(new MemoryStream()),
            })
                (await act.Should().ThrowAsync<DatabaseAdminException>()).Which.Code.Should().Be("UNSUPPORTED");
        }

        [Fact]
        public async Task TheScheduledTasks_QuietlySkip_WhenTheDatabaseIsNotSupported()
        {
            var services = new ServiceCollection()
                .AddDbContext<ChronicleDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()))
                .BuildServiceProvider();
            var admin = new DatabaseAdminService(services.GetRequiredService<IServiceScopeFactory>());

            await new DatabaseBackupTask(admin).ExecuteAsync(CancellationToken.None);
            await new DatabaseLightMaintenanceTask(admin).ExecuteAsync(CancellationToken.None);
            await new DatabaseHeavyMaintenanceTask(admin).ExecuteAsync(CancellationToken.None);
        }

        [Fact]
        public async Task TheScheduledBackupTask_MakesAScheduledBackup()
        {
            await new DatabaseBackupTask(_admin).ExecuteAsync(CancellationToken.None);

            _admin.ListBackups().Should().ContainSingle(b => b.Kind == BackupKinds.Scheduled);
        }

        [Fact]
        public void TheTasksHaveDistinctIdsAndValidCronExpressions()
        {
            IScheduledTask[] tasks = [new DatabaseBackupTask(_admin), new DatabaseLightMaintenanceTask(_admin), new DatabaseHeavyMaintenanceTask(_admin)];

            tasks.Select(t => t.TaskId).Distinct().Should().HaveCount(3);
            foreach (var t in tasks)
                Cronos.CronExpression.Parse(t.DefaultCron).Should().NotBeNull(t.TaskId);
        }

        [Theory]
        [InlineData(0L, "0 B")]
        [InlineData(1536L, "1.5 KB")]
        [InlineData(5L * 1024 * 1024, "5 MB")]
        public void Format_IsHumanReadable(long bytes, string expected)
        {
            DatabaseAdminService.Format(bytes).Should().Be(expected);
        }
    }
}
