namespace Chronicle.API.DTOs;

public class CollectionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public string? Overview { get; set; }
    public List<CollectionMemberDto> Movies { get; set; } = [];
}

public class CollectionMemberDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? PosterUrl { get; set; }
    public bool InLibrary { get; set; }
    public string? LibraryStatus { get; set; }
}
