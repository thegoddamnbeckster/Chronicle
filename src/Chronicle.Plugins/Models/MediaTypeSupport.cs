namespace Chronicle.Plugins.Models;

/// <summary>Describes which media type a plugin supports and what fields it provides.</summary>
public class MediaTypeSupport
{
    /// <summary>Media type name as stored in the database, e.g. "movie", "tv".</summary>
    public string MediaTypeName { get; set; } = string.Empty;

    /// <summary>List of metadata fields this plugin can populate for the media type.</summary>
    public List<string> SupportedFields { get; set; } = [];

    /// <summary>Lower numbers = higher priority when multiple providers support the same type.</summary>
    public int DefaultPriority { get; set; } = 10;
}
