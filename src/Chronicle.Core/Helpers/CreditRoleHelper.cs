namespace Chronicle.Core.Helpers;

/// <summary>
/// Decides how to label a MediaMetadata.Cast entry's MediaCredit.Role when recording a credit.
///
/// Confirmed live (2026-09-14): every Cast entry, from every provider, was recorded with a
/// hardcoded Role of "Actor" and its own CastMember.Role value stashed into CharacterName --
/// correct for movies/TV (a cast entry genuinely is an actor playing a named character), but
/// wrong for a type with no "character" concept at all. Hardcover's own book contributor list
/// puts the real contribution ("Author", "Illustrator", "Translator", ...) into CastMember.Role
/// precisely because there's no character to separately track -- Ernest Cline's own author
/// credit on his own books was showing up grouped under an "Actor" section labeled "Author"
/// as if that were his character name.
/// </summary>
public static class CreditRoleHelper
{
    /// <summary>MediaType names whose credits genuinely represent an actor playing a character.
    /// Deliberately its own list, not a reuse of NfoKindHelper's video-library classification --
    /// today's membership happens to be identical, but the two questions ("is this Kodi-relevant"
    /// vs. "do this type's credits have a character concept") are conceptually independent and
    /// shouldn't be coupled just because they coincide right now.</summary>
    public static readonly string[] PerformanceTypeNames = ["movies", "tv", "anime", "fanedits", "anime_movies"];

    /// <summary>True when a Cast entry for this media type is an actor playing a character --
    /// i.e. MediaCredit.Role should be the literal "Actor" and CharacterName should carry the
    /// CastMember's own Role value. False for every other type, where the CastMember's own Role
    /// value (when present) IS the real contribution/relationship and belongs in MediaCredit.Role
    /// directly, with no CharacterName at all.</summary>
    public static bool CastEntryIsActingCredit(string? mediaTypeName) =>
        mediaTypeName is not null && PerformanceTypeNames.Contains(mediaTypeName);
}
