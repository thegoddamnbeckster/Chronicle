using System.Runtime.CompilerServices;
using Chronicle.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Chronicle.Services.Live
{
    public static class ChangeKinds
    {
        public const string Changed = "changed";
        public const string Deleted = "deleted";
    }

    public sealed record ChangedItem(int ItemId, string Kind);

    /// <summary>What a client learns by asking "what changed since revision N?".</summary>
    /// <param name="Reset">True when the client cannot be told exactly what changed (it asked too long ago, the
    /// server restarted, or a bulk operation changed everything): it should refetch rather than patch.</param>
    public sealed record ChangeSet(Guid Epoch, long Revision, bool Reset, IReadOnlyList<ChangedItem> Changes);

    /// <summary>
    /// A short, in-memory record of which library items changed, so the web UI can refresh just those without
    /// reloading everything. One <c>SaveChanges</c> interceptor feeds it, so every writer (enrichment, scans, sync,
    /// merges, art pinning, a person rating something) is covered in one place. Memory-only on purpose: after a
    /// restart the epoch changes and clients simply refetch once.
    ///
    /// Not covered: bulk <c>ExecuteUpdate/ExecuteDelete</c> statements bypass change tracking, so the code that
    /// issues one calls <see cref="MarkAllChanged"/>.
    /// </summary>
    public sealed class LibraryChangeFeed
    {
        public const int Capacity = 5000;

        private readonly Lock _gate = new();
        private readonly Queue<(long Revision, int ItemId, string Kind, int? UserId)> _events = new();
        private long _revision;
        private long _resetAtOrBelow;     // clients that last synced at or below this revision must refetch

        public Guid Epoch { get; } = Guid.NewGuid();

        public long Revision { get { lock (_gate) return _revision; } }

        /// <param name="userId">Set for a change that concerns one person's library entry; other people are not told about it.</param>
        public void Publish(IEnumerable<(int ItemId, string Kind, int? UserId)> changes)
        {
            lock (_gate)
            {
                foreach (var (itemId, kind, userId) in changes)
                {
                    _events.Enqueue((++_revision, itemId, kind, userId));
                    if (_events.Count > Capacity)
                    {
                        var dropped = _events.Dequeue();
                        // Anyone who last synced before the dropped event can no longer be told exactly.
                        _resetAtOrBelow = Math.Max(_resetAtOrBelow, dropped.Revision);
                    }
                }
            }
        }

        /// <summary>Everything may have changed (a bulk statement ran): every client must refetch.</summary>
        public void MarkAllChanged()
        {
            lock (_gate) _resetAtOrBelow = ++_revision;
        }

        public ChangeSet GetSince(long? since, Guid? epoch, int viewerUserId)
        {
            lock (_gate)
            {
                // First call (no baseline yet): just report where things stand.
                if (since is null || epoch is null)
                    return new ChangeSet(Epoch, _revision, false, []);

                if (epoch != Epoch || since < _resetAtOrBelow)
                    return new ChangeSet(Epoch, _revision, true, []);

                var changes = _events
                    .Where(e => e.Revision > since && (e.UserId is null || e.UserId == viewerUserId))
                    .GroupBy(e => e.ItemId)
                    .Select(g => new ChangedItem(g.Key, g.Last().Kind))   // the latest word on each item
                    .ToList();
                return new ChangeSet(Epoch, _revision, false, changes);
            }
        }
    }

    /// <summary>
    /// Watches every <c>SaveChanges</c> and tells <see cref="LibraryChangeFeed"/> which media items (and which person's
    /// library entries) were added, changed or removed. Ids of new rows are read AFTER the save, when the database has
    /// assigned them.
    /// </summary>
    public sealed class LibraryChangeInterceptor : SaveChangesInterceptor
    {
        private sealed record Pending(object Entity, EntityState State);

        private readonly LibraryChangeFeed _feed;
        private readonly ConditionalWeakTable<DbContext, List<Pending>> _pending = new();

        public LibraryChangeInterceptor(LibraryChangeFeed feed) => _feed = feed;

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Capture(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Capture(eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            Publish(eventData.Context);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Publish(eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override void SaveChangesFailed(DbContextErrorEventData eventData) => Forget(eventData.Context);

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Forget(eventData.Context);
            return Task.CompletedTask;
        }

        private void Capture(DbContext? context)
        {
            if (context is null) return;
            var list = new List<Pending>();
            foreach (var entry in context.ChangeTracker.Entries())
            {
                if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
                if (entry.Entity is MediaItem or UserLibrary) list.Add(new Pending(entry.Entity, entry.State));
            }
            if (list.Count == 0) { _pending.Remove(context); return; }
            _pending.AddOrUpdate(context, list);
        }

        private void Publish(DbContext? context)
        {
            if (context is null || !_pending.TryGetValue(context, out var list)) return;
            _pending.Remove(context);

            var changes = new List<(int ItemId, string Kind, int? UserId)>();
            foreach (var p in list)
            {
                var kind = p.State == EntityState.Deleted ? ChangeKinds.Deleted : ChangeKinds.Changed;
                switch (p.Entity)
                {
                    case MediaItem m when m.Id > 0:
                        changes.Add((m.Id, kind, null));
                        break;
                    case UserLibrary l when l.MediaItemId > 0:
                        // A library entry changing changes how the item looks to THAT person only.
                        changes.Add((l.MediaItemId, ChangeKinds.Changed, l.UserId));
                        break;
                }
            }
            if (changes.Count > 0) _feed.Publish(changes);
        }

        private void Forget(DbContext? context)
        {
            if (context is not null) _pending.Remove(context);
        }
    }
}
