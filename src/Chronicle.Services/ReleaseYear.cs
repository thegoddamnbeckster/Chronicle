namespace Chronicle.Services;

/// <summary>
/// What counts as a real release year. Kodi stores an unknown year as -1, which its JSON-RPC reports as
/// 65535; a device search carrying that value minted a new movie with Year 65535 (confirmed live
/// 2026-09-26: "Captain America: The Winter Soldier - Defrosted Edition"), which the device then showed
/// as year 65535 with a premiered date of 65535-11-30. An implausible year is treated as no year.
/// </summary>
public static class ReleaseYear
{
    /// <summary>The first motion picture is from 1888; allow a little headroom.</summary>
    public const int Earliest = 1878;

    /// <summary>Announced-but-unreleased titles carry future years; a decade is generous.</summary>
    public static int Latest => DateTime.UtcNow.Year + 10;

    public static bool IsPlausible(int? year) => year is { } y && y >= Earliest && y <= Latest;

    public static int? OrNull(int? year) => IsPlausible(year) ? year : null;
}
