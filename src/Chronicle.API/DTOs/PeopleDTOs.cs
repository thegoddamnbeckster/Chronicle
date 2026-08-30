namespace Chronicle.API.DTOs
{
    /// <summary>One row on the catalog-wide People grid (PeopleLibraryPage) -- see
    /// docs/plans/2026-08-28-people-section-design.md Section 5. Roles is the distinct set of
    /// media_credits.Role values this person has across every credit, used for the card's
    /// "positions" line (e.g. "Actor, Executive Producer").</summary>
    public record PersonListItemDto(
        int Id,
        string Name,
        string? PosterUrl,
        DateTime? BirthDate,
        DateTime? DeathDate,
        List<string> Roles
    );

    /// <summary>One credited title, for the person detail page's role-grouped credits section
    /// (Section 6). CharacterName is null for non-acting roles.</summary>
    public record PersonCreditDto(
        int MediaItemId,
        string Name,
        string? PosterUrl,
        int? Year,
        string MediaTypeName,
        string? CharacterName
    );

    public record PersonCreditGroupDto(
        string Role,
        List<PersonCreditDto> Items
    );

    /// <summary>One accumulated photo for a person (person_headshots), for the photo-picker
    /// section of the person detail page. IsCurrent marks whichever one is presently resolved
    /// onto the person's own PosterUrl (either an explicit pin via the standard _overrides
    /// mechanism, or -- absent a pin -- the most-recently-discovered headshot).</summary>
    public record PersonHeadshotDto(
        int Id,
        string Url,
        string? ThumbnailUrl,
        string Source,
        DateTime FirstSeenAt,
        bool IsCurrent
    );
}
