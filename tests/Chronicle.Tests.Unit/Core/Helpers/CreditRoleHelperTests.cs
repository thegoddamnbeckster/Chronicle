using Chronicle.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Core.Helpers;

public class CreditRoleHelperTests
{
    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    [InlineData("anime")]
    [InlineData("fanedits")]
    [InlineData("anime_movies")]
    public void CastEntryIsActingCredit_PerformanceTypes_ReturnsTrue(string mediaTypeName)
    {
        CreditRoleHelper.CastEntryIsActingCredit(mediaTypeName).Should().BeTrue();
    }

    // Confirmed live (2026-09-14): Ernest Cline's own "Author" credit on his own books showed
    // up grouped under an "Actor" section on his person page, with "Author" stashed in as if it
    // were his character name -- a book (and every other non-performance type) has no character
    // concept at all, so its Cast entries' own Role value IS the real contribution.
    [Theory]
    [InlineData("book")]
    [InlineData("audiobook")]
    [InlineData("music")]
    [InlineData("podcast")]
    [InlineData(null)]
    public void CastEntryIsActingCredit_NonPerformanceTypes_ReturnsFalse(string? mediaTypeName)
    {
        CreditRoleHelper.CastEntryIsActingCredit(mediaTypeName).Should().BeFalse();
    }
}
