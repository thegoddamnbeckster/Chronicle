using System.Data;
using System.Diagnostics;
using System.Reflection;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Chronicle.Services.Database
{
    /// <param name="Skipped">Rows left out because they refer to a row that does not exist (orphans an older version left
    /// behind; SQLite did not stop them, PostgreSQL would refuse them). They point at nothing, so nothing can use them.</param>
    public sealed record CopyTableResult(string Table, long SourceRows, long TargetRows, long Skipped = 0)
    {
        public bool Matches => SourceRows == TargetRows + Skipped;
    }

    public sealed record CopyReport(IReadOnlyList<CopyTableResult> Tables, TimeSpan Elapsed)
    {
        public bool AllMatch => Tables.All(t => t.Matches);
        public long TotalSkipped => Tables.Sum(t => t.Skipped);
        public long TotalRows => Tables.Sum(t => t.TargetRows);
    }

    /// <summary>
    /// Copies every table of one Chronicle database into another of the same model (SQLite to PostgreSQL, or any pair
    /// of providers EF supports). The source is only read. Tables are copied parents first; a table that points at itself
    /// (items and their parents) is copied so that a row always arrives after the row it refers to, so no constraint is
    /// ever switched off. Row counts of both sides are compared at the end.
    /// </summary>
    public static class DatabaseCopier
    {
        public const int DefaultBatchSize = 2000;

        public static async Task<CopyReport> CopyAsync(
            ChronicleDbContext source, ChronicleDbContext target, bool replace, Action<string>? log = null,
            CancellationToken ct = default, int batchSize = DefaultBatchSize)
        {
            var clock = Stopwatch.StartNew();
            log ??= _ => { };
            target.ChangeTracker.Clear();
            source.ChangeTracker.Clear();
            var types = Order(target.Model.GetEntityTypes()
                .Where(t => !t.IsOwned() && t.GetTableName() is not null && t.FindPrimaryKey() is not null && t.BaseType is null).ToList());

            // The target must be ready to receive: a database nobody has used (the migrations alone put a few reference rows
            // in, such as the built-in media types; those are in the source too and are replaced, not kept). Anything a
            // person created (accounts, library items) means this is somebody's database: refuse unless told to replace it.
            var inUse = new List<string>();
            foreach (var type in types.Where(t => t.GetTableName() is "users" or "media_items" or "user_libraries" or "interaction_events"))
                if (await CountAsync(target, type, ct) is > 0 and var n)
                    inUse.Add($"{type.GetTableName()} ({n})");
            if (inUse.Count > 0 && !replace)
                throw new InvalidOperationException(
                    "The target database already contains data (" + string.Join(", ", inUse) + "). Use an empty database, or ask for the target to be replaced.");
            foreach (var type in Enumerable.Reverse(types))
                if (await CountAsync(target, type, ct) > 0)
                    await ExecuteAsync(target, $"DELETE FROM {Quote(target, type)}", ct);

            var results = new List<CopyTableResult>();
            foreach (var type in types)
            {
                ct.ThrowIfCancellationRequested();
                var sourceRows = await CountAsync(source, type, ct);
                log($"Copying {type.GetTableName()} ({sourceRows} rows)...");
                long skipped = 0;
                if (sourceRows > 0)
                {
                    var method = typeof(DatabaseCopier).GetMethod(nameof(CopyTableAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(type.ClrType);
                    skipped = await (Task<long>)method.Invoke(null, [source, target, type, batchSize, log, ct])!;
                }
                results.Add(new CopyTableResult(type.GetTableName()!, sourceRows, await CountAsync(target, type, ct), skipped));
            }

            if (target.Database.IsNpgsql())
                foreach (var type in types)
                    await ResetSequenceAsync(target, type, ct);

            return new CopyReport(results, clock.Elapsed);
        }

        // ── ordering ──────────────────────────────────────────────────────────────

        /// <summary>Parents before the tables that refer to them. A cycle between different tables is not supported.</summary>
        internal static List<IEntityType> Order(List<IEntityType> types)
        {
            var remaining = types.ToList();
            var ordered = new List<IEntityType>();
            while (remaining.Count > 0)
            {
                var ready = remaining.Where(t => t.GetForeignKeys()
                    .Where(fk => fk.PrincipalEntityType != t && fk.PrincipalEntityType.BaseType is null)
                    .All(fk => ordered.Contains(fk.PrincipalEntityType) || !remaining.Contains(fk.PrincipalEntityType))).ToList();
                if (ready.Count == 0)
                    throw new InvalidOperationException("The tables refer to each other in a circle: " + string.Join(", ", remaining.Select(t => t.GetTableName())));
                ordered.AddRange(ready);
                remaining.RemoveAll(ready.Contains);
            }
            return ordered;
        }

        // ── one table ─────────────────────────────────────────────────────────────

        private static async Task<long> CopyTableAsync<T>(
            ChronicleDbContext source, ChronicleDbContext target, IEntityType type, int batchSize, Action<string> log, CancellationToken ct)
            where T : class
        {
            var key = type.FindPrimaryKey()!;
            var dateProps = type.GetProperties()
                .Where(p => (p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)) && p.PropertyInfo is not null).Select(p => p.PropertyInfo!).ToList();
            var utc = target.Database.IsNpgsql();
            long done = 0, skipped = 0;

            async Task WriteAsync(List<T> rows)
            {
                if (utc) foreach (var row in rows) MakeUtc(row, dateProps);
                try
                {
                    target.AddRange(rows);
                    await target.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (IsForeignKeyViolation(ex))
                {
                    // Something in this batch points at a row that does not exist. Find which rows, one at a time, and
                    // leave those out; everything else still goes in.
                    target.ChangeTracker.Clear();
                    foreach (var row in rows)
                    {
                        try
                        {
                            target.Add(row);
                            await target.SaveChangesAsync(ct);
                        }
                        catch (DbUpdateException single) when (IsForeignKeyViolation(single))
                        {
                            skipped++;
                            if (skipped <= 3) log($"  {type.GetTableName()}: left out a row that refers to something that no longer exists");
                        }
                        target.ChangeTracker.Clear();
                    }
                }
                target.ChangeTracker.Clear();
                done += rows.Count;
                if (done % (batchSize * 25) < batchSize) log($"  {type.GetTableName()}: {done} rows");
            }

            var keyProp = key.Properties.Count == 1 ? key.Properties[0] : null;
            var selfFk = type.GetForeignKeys().FirstOrDefault(fk => fk.PrincipalEntityType == type && fk.Properties.Count == 1);

            if (keyProp is not null && selfFk is not null && keyProp.ClrType == typeof(int))
            {
                // Items and their parents: copy in an order where every parent arrives before its children.
                var order = await ParentFirstOrderAsync(source, type, keyProp, selfFk.Properties[0], ct);
                foreach (var chunk in order.Chunk(batchSize))
                {
                    var ids = chunk.ToArray();
                    var rows = await source.Set<T>().AsNoTracking().Where(e => ids.Contains(EF.Property<int>(e, keyProp.Name))).ToListAsync(ct);
                    await WriteAsync(rows);
                }
            }
            else if (keyProp is not null && keyProp.ClrType == typeof(int))
            {
                var last = int.MinValue;
                while (true)
                {
                    var lastId = last;
                    var rows = await source.Set<T>().AsNoTracking().Where(e => EF.Property<int>(e, keyProp.Name) > lastId)
                        .OrderBy(e => EF.Property<int>(e, keyProp.Name)).Take(batchSize).ToListAsync(ct);
                    if (rows.Count == 0) break;
                    last = (int)keyProp.PropertyInfo!.GetValue(rows[^1])!;
                    await WriteAsync(rows);
                }
            }
            else
            {
                // Composite or non-integer keys: small tables; page by position in key order.
                var page = 0;
                while (true)
                {
                    IQueryable<T> query = source.Set<T>().AsNoTracking();
                    IOrderedQueryable<T>? ordered = null;
                    foreach (var p in key.Properties)
                        ordered = ordered is null ? query.OrderBy(e => EF.Property<object>(e, p.Name)) : ordered.ThenBy(e => EF.Property<object>(e, p.Name));
                    var rows = await ordered!.Skip(page * batchSize).Take(batchSize).ToListAsync(ct);
                    if (rows.Count == 0) break;
                    page++;
                    await WriteAsync(rows);
                }
            }
            return skipped;
        }

        /// <summary>Row ids of a self-referencing table, every parent before its children (rows whose parent is missing or
        /// null first; a loop in the data is broken rather than followed forever).</summary>
        private static async Task<List<int>> ParentFirstOrderAsync(
            ChronicleDbContext source, IEntityType type, IProperty keyProp, IProperty parentProp, CancellationToken ct)
        {
            var pairs = new Dictionary<int, int?>();
            var conn = source.Database.GetDbConnection();
            var opened = conn.State != ConnectionState.Open;
            if (opened) await conn.OpenAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                var helper = source.GetService<ISqlGenerationHelper>();
                cmd.CommandText = $"SELECT {helper.DelimitIdentifier(keyProp.GetColumnName())}, {helper.DelimitIdentifier(parentProp.GetColumnName())} FROM {Quote(source, type)}";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    pairs[Convert.ToInt32(reader.GetValue(0))] = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1));
            }
            finally { if (opened) await conn.CloseAsync(); }

            var children = new Dictionary<int, List<int>>();
            var order = new List<int>(pairs.Count);
            var queue = new Queue<int>();
            foreach (var (id, parent) in pairs)
            {
                if (parent is null || !pairs.ContainsKey(parent.Value)) queue.Enqueue(id);
                else
                {
                    if (!children.TryGetValue(parent.Value, out var list)) children[parent.Value] = list = [];
                    list.Add(id);
                }
            }
            var seen = new HashSet<int>();
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!seen.Add(id)) continue;
                order.Add(id);
                if (children.TryGetValue(id, out var kids)) foreach (var k in kids) queue.Enqueue(k);
            }
            // Anything left is in a parent loop: copy it too, after the rest.
            foreach (var id in pairs.Keys) if (seen.Add(id)) order.Add(id);
            return order;
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        /// <summary>A constraint failure that means "refers to a row that is not there" on PostgreSQL (23503) or SQLite.</summary>
        private static bool IsForeignKeyViolation(DbUpdateException ex)
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e is Npgsql.PostgresException { SqlState: "23503" }) return true;
                if (e.Message.Contains("FOREIGN KEY constraint failed", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void MakeUtc(object row, List<PropertyInfo> props)
        {
            foreach (var p in props)
            {
                var value = p.GetValue(row);
                if (value is DateTime dt && dt.Kind != DateTimeKind.Utc)
                    p.SetValue(row, DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            }
        }

        private static string Quote(DbContext db, IEntityType type) =>
            db.GetService<ISqlGenerationHelper>().DelimitIdentifier(type.GetTableName()!, type.GetSchema());

        private static async Task<long> CountAsync(DbContext db, IEntityType type, CancellationToken ct)
        {
            var conn = db.Database.GetDbConnection();
            var opened = conn.State != ConnectionState.Open;
            if (opened) await conn.OpenAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(db, type)}";
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            }
            finally { if (opened) await conn.CloseAsync(); }
        }

        private static Task<int> ExecuteAsync(DbContext db, string sql, CancellationToken ct) =>
            db.Database.ExecuteSqlRawAsync(sql, ct);

        /// <summary>After copying explicit ids, the next generated id must come after the largest one.</summary>
        private static async Task ResetSequenceAsync(DbContext db, IEntityType type, CancellationToken ct)
        {
            var key = type.FindPrimaryKey();
            if (key is null || key.Properties.Count != 1) return;
            var prop = key.Properties[0];
            if (prop.ValueGenerated != ValueGenerated.OnAdd || (prop.ClrType != typeof(int) && prop.ClrType != typeof(long))) return;
            var helper = db.GetService<ISqlGenerationHelper>();
            var table = Quote(db, type);
            var column = helper.DelimitIdentifier(prop.GetColumnName());
            var tableLiteral = table.Replace("'", "''");
            var columnName = prop.GetColumnName().Replace("'", "''");
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT setval(pg_get_serial_sequence('{tableLiteral}', '{columnName}'), (SELECT COALESCE(MAX({column}), 0) + 1 FROM {table}), false)", ct);
        }
    }
}
