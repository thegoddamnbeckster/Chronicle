namespace Chronicle.Core.Models;

public class MediaItemRefreshLog
{
    public int      Id           { get; set; }
    public int      MediaItemId  { get; set; }
    public string   ProviderName { get; set; } = string.Empty;
    public DateTime RefreshedAt  { get; set; }
    public bool     Succeeded    { get; set; }
    public string?  ErrorMessage { get; set; }

    // Navigation
    public MediaItem? MediaItem { get; set; }
}
