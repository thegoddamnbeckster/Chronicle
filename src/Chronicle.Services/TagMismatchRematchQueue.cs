using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Chronicle.Services;

/// <summary>
/// In-process queue of media item IDs pending a metadata re-match after a contribution
/// detected their file tags disagree with what Chronicle has resolved. Drained by
/// <see cref="TagMismatchRematchTask"/>. Pure in-memory — a restart between an item being
/// queued and the next drain silently loses that one pending re-match (accepted trade-off:
/// infrequent, non-destructive, and the next disagreeing contribution re-flags it anyway).
/// </summary>
public sealed class TagMismatchRematchQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();
    private readonly ConcurrentDictionary<int, byte> _pending = new();

    /// <summary>Enqueues a media item for re-match. Returns false if already queued (de-duped).</summary>
    public bool TryEnqueue(int mediaItemId)
    {
        if (!_pending.TryAdd(mediaItemId, 0)) return false;
        _channel.Writer.TryWrite(mediaItemId);
        return true;
    }

    /// <summary>Drains every currently-queued item ID. Does not clear pending status — call MarkProcessed per item.</summary>
    public IReadOnlyList<int> DrainAll()
    {
        var batch = new List<int>();
        while (_channel.Reader.TryRead(out var id)) batch.Add(id);
        return batch;
    }

    public void MarkProcessed(int mediaItemId) => _pending.TryRemove(mediaItemId, out _);
}
