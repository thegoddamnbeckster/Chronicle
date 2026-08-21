namespace Chronicle.Services.Security
{
    /// <summary>
    /// User ids that must be refused even when they present a valid, unexpired JWT.
    /// <para>
    /// Chronicle's tokens are stateless and live 24 hours by default, and nothing else
    /// re-reads the user row on a request. Without this, deactivating or deleting an account
    /// would leave its existing session working for up to a day — "remove this user" would not
    /// actually remove them. A tiny in-memory set keeps that check off the database on the
    /// hot path.
    /// </para>
    /// </summary>
    public interface IDeactivatedUserCache
    {
        bool IsBlocked(int userId);
        void Block(int userId);
        void Unblock(int userId);
        /// <summary>Rebuilds the whole set — used once at startup from the database.</summary>
        void Replace(IEnumerable<int> blockedUserIds);
    }

    public sealed class DeactivatedUserCache : IDeactivatedUserCache
    {
        // Copy-on-write: readers (every authenticated request) never take a lock; the rare
        // writer swaps in a whole new set.
        private volatile HashSet<int> _blocked = [];
        private readonly Lock _writeLock = new();

        public bool IsBlocked(int userId) => _blocked.Contains(userId);

        public void Block(int userId)
        {
            lock (_writeLock)
            {
                if (_blocked.Contains(userId)) return;
                _blocked = new HashSet<int>(_blocked) { userId };
            }
        }

        public void Unblock(int userId)
        {
            lock (_writeLock)
            {
                if (!_blocked.Contains(userId)) return;
                var next = new HashSet<int>(_blocked);
                next.Remove(userId);
                _blocked = next;
            }
        }

        public void Replace(IEnumerable<int> blockedUserIds)
        {
            lock (_writeLock)
                _blocked = [.. blockedUserIds];
        }
    }
}
