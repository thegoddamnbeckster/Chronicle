namespace Chronicle.Services.Database
{
    /// <summary>
    /// Completes a restore that <see cref="IDatabaseAdminService.StageRestoreAsync"/> staged. It must run at
    /// startup BEFORE anything opens the database, because the live file is locked while the application
    /// runs - which is why a restore is "stage, restart, swap" rather than an in-place overwrite.
    ///
    /// The database being replaced is not deleted: it is renamed beside the new one
    /// (<c>chronicle.db.replaced-yyyyMMdd-HHmmss</c>) and the newest two are kept, on top of the safety
    /// backup zip taken when the restore was staged.
    /// </summary>
    public static class PendingRestore
    {
        public const string Suffix = ".restore-pending";
        private const int KeepReplaced = 2;

        /// <returns>True when a staged restore was applied.</returns>
        public static bool ApplyIfPresent(string dbPath, Action<string> log, TimeProvider? clock = null)
        {
            var pending = dbPath + Suffix;
            if (!File.Exists(pending)) return false;

            var stamp = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss");
            var replaced = $"{dbPath}.replaced-{stamp}";
            var movedOld = false;
            try
            {
                if (File.Exists(dbPath))
                {
                    File.Move(dbPath, replaced);
                    movedOld = true;
                }
                // The old database's write-ahead log and shared-memory file belong to it, not to the restored one.
                foreach (var side in new[] { dbPath + "-wal", dbPath + "-shm" })
                    if (File.Exists(side)) File.Delete(side);

                File.Move(pending, dbPath);
                log($"A staged database restore was applied. The previous database was kept as {Path.GetFileName(replaced)}.");
                Prune(dbPath);
                return true;
            }
            catch (Exception ex)
            {
                // Put things back so the application starts on the data it had.
                try
                {
                    if (movedOld && !File.Exists(dbPath) && File.Exists(replaced)) File.Move(replaced, dbPath);
                }
                catch { /* nothing more can be done */ }
                log($"A staged database restore could NOT be applied ({ex.Message}); continuing with the existing database. The staged file was left in place.");
                return false;
            }
        }

        private static void Prune(string dbPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(dbPath)!;
                var old = Directory.GetFiles(dir, Path.GetFileName(dbPath) + ".replaced-*")
                    .OrderByDescending(f => f, StringComparer.Ordinal)   // the timestamp sorts correctly as text
                    .Skip(KeepReplaced);
                foreach (var f in old) File.Delete(f);
            }
            catch { /* housekeeping only */ }
        }
    }
}
