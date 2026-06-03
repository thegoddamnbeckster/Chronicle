namespace Chronicle.Core.Models;

public class MediaItemDuplicateDismissal
{
    public int Id { get; set; }
    public int ItemAId { get; set; }
    public int ItemBId { get; set; }
    public DateTime DismissedAt { get; set; }
}
