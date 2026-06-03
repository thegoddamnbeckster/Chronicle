namespace Chronicle.Core.Models;

public class MediaItemDuplicateCandidate
{
    public int Id { get; set; }
    public int ItemAId { get; set; }
    public int ItemBId { get; set; }
    public DateTime DetectedAt { get; set; }

    public MediaItem? ItemA { get; set; }
    public MediaItem? ItemB { get; set; }
}
