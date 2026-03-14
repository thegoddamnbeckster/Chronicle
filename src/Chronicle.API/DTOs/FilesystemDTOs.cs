namespace Chronicle.API.DTOs;

public record FilesystemEntryDto(string Name, string Path);

public record FilesystemListingDto(
    string? Path,
    string? Parent,
    List<FilesystemEntryDto> Directories
);
