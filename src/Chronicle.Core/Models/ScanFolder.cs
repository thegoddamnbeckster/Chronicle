namespace Chronicle.Core.Models;

public class ScanFolder
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    /// <summary>The media type of everything in this folder, or null to sort each file into its own type
    /// (see the scan page's "Detect automatically").</summary>
    public int? MediaTypeId { get; set; }

    /// <summary>Remember subtitles, artwork and extras with each item when this folder is scanned automatically.
    /// Null follows the global setting <c>scan.bundle_related_files</c>.</summary>
    public bool? BundleRelatedFiles { get; set; }
    public bool Recursive { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScannedAt { get; set; }

    public MediaType? MediaType { get; set; }
}
