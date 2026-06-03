namespace Chronicle.Core.Models;

public class MediaItemMerge
{
    public int Id { get; set; }
    public int WinnerId { get; set; }
    public int LoserOriginalId { get; set; }
    public string LoserName { get; set; } = string.Empty;
    public int LoserMediaTypeId { get; set; }
    public int LoserHierarchyLevel { get; set; }
    public int? LoserParentId { get; set; }
    /// <summary>JSON array of {Source, ExternalId} objects.</summary>
    public string LoserExternalIdsJson { get; set; } = "[]";
    /// <summary>JSON array of child MediaItem IDs that were re-parented to winner.</summary>
    public string LoserChildIdsJson { get; set; } = "[]";
    public DateTime MergedAt { get; set; }
    public int? MergedByUserId { get; set; }

    public MediaItem? Winner { get; set; }
}
