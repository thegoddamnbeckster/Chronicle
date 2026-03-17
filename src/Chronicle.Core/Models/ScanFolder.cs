namespace Chronicle.Core.Models;

public class ScanFolder
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public int MediaTypeId { get; set; }
    public MediaType? MediaType { get; set; }
    public bool Recursive { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastScannedAt { get; set; }
}
