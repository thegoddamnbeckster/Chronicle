using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class HeadshotImageKeyTests
{
    [Theory]
    [InlineData("https://image.tmdb.org/t/p/h632/8vbMJ7qu9DLJcAyvA7G13hYNOFT.jpg")]
    [InlineData("https://image.tmdb.org/t/p/w500/8vbMJ7qu9DLJcAyvA7G13hYNOFT.jpg")]
    [InlineData("https://image.tmdb.org/t/p/original/8vbMJ7qu9DLJcAyvA7G13hYNOFT.jpg")]
    public void One_TMDB_file_has_one_key_at_every_size(string url) =>
        HeadshotImageKey.Of(url).Should().Be("tmdb:8vbmj7qu9dljcayva7g13hynoft.jpg");

    [Fact]
    public void Different_TMDB_files_have_different_keys() =>
        HeadshotImageKey.Of("https://image.tmdb.org/t/p/original/8vbMJ7qu9DLJcAyvA7G13hYNOFT.jpg")
            .Should().NotBe(HeadshotImageKey.Of("https://image.tmdb.org/t/p/original/evW0UUCitv6Ni2XNkSeXcHsu9gC.jpg"));

    [Fact]
    public void Tracking_parameters_do_not_make_a_new_picture()
    {
        var a = HeadshotImageKey.Of("https://upload.wikimedia.org/wikipedia/commons/0/0c/Stanislav_Ianevski_%281%29.jpg?utm_source=en.wikipedia.org&utm_campaign=api");
        var b = HeadshotImageKey.Of("https://upload.wikimedia.org/wikipedia/commons/0/0c/Stanislav_Ianevski_%281%29.jpg");

        a.Should().Be(b);
    }

    [Fact]
    public void Keys_ignore_case_in_the_host_and_blank_urls_have_none()
    {
        HeadshotImageKey.Of("https://IMAGE.TMDB.ORG/t/p/w500/Abc.JPG").Should().Be(HeadshotImageKey.Of("https://image.tmdb.org/t/p/original/abc.jpg"));
        HeadshotImageKey.Of(null).Should().BeEmpty();
        HeadshotImageKey.Of("  ").Should().BeEmpty();
    }

    [Fact]
    public void Urls_on_other_hosts_with_the_same_file_name_are_different_pictures() =>
        HeadshotImageKey.Of("https://a.example/photos/x.jpg").Should().NotBe(HeadshotImageKey.Of("https://b.example/photos/x.jpg"));
}

public class HeadshotGalleryTests
{
    private const string Close  = "8vbMJ7qu9DLJcAyvA7G13hYNOFT.jpg";
    private const string Wrong  = "evW0UUCitv6Ni2XNkSeXcHsu9gC.jpg";
    private static string Tmdb(string size, string file) => $"https://image.tmdb.org/t/p/{size}/{file}";

    private static ChronicleDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static PersonHeadshot Row(int id, string url, bool hidden = false, int person = 1) => new()
    {
        Id = id, PersonMediaItemId = person, Url = url, Source = "tmdb", IsHidden = hidden,
        FirstSeenAt = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc).AddMinutes(id),
    };

    /// <summary>Stanislav Yanevski's real rows: the close-up stored three times (three sizes), plus the stranger.</summary>
    private static List<PersonHeadshot> Yanevski() =>
    [
        Row(304034, Tmdb("original", Wrong)),
        Row(304033, Tmdb("original", Close)),
        Row(304032, Tmdb("w500", Close)),
        Row(299045, Tmdb("h632", Close)),
    ];

    // ── collapse ──────────────────────────────────────────────────────────────

    [Fact]
    public void One_picture_stored_at_three_sizes_is_one_gallery_entry()
    {
        var gallery = HeadshotGallery.Collapse(Yanevski(), Tmdb("h632", Close));

        gallery.Should().HaveCount(2);
    }

    [Fact]
    public void The_current_photo_is_marked_and_represented_by_the_row_actually_in_use()
    {
        var gallery = HeadshotGallery.Collapse(Yanevski(), Tmdb("h632", Close));

        var current = gallery.Single(p => p.IsCurrent);
        current.Row.Url.Should().Be(Tmdb("h632", Close));        // pinning this tile re-pins the very same url
        gallery.Single(p => !p.IsCurrent).Row.Url.Should().EndWith(Wrong);
    }

    [Fact]
    public void With_nothing_current_each_entry_is_its_newest_row_and_none_is_current()
    {
        var gallery = HeadshotGallery.Collapse(Yanevski(), currentPosterUrl: null);

        gallery.Should().OnlyContain(p => !p.IsCurrent);
    }

    [Fact]
    public void A_picture_is_hidden_when_a_row_of_it_is()
    {
        var rows = Yanevski();
        rows.Single(r => r.Id == 304034).IsHidden = true;

        var gallery = HeadshotGallery.Collapse(rows, Tmdb("h632", Close));

        gallery.Single(p => p.Row.Url.EndsWith(Wrong)).IsHidden.Should().BeTrue();
        gallery.Single(p => p.IsCurrent).IsHidden.Should().BeFalse();
    }

    // ── hide / restore ────────────────────────────────────────────────────────

    [Fact]
    public async Task Hiding_a_photo_hides_every_size_of_it_and_nothing_else()
    {
        await using var db = NewDb();
        db.PersonHeadshots.AddRange(Yanevski());
        db.PersonHeadshots.Add(Row(900, Tmdb("w500", Wrong), person: 2));    // someone else with the same file
        await db.SaveChangesAsync();

        var outcome = await HeadshotGallery.HideAsync(db, personId: 1, headshotId: 304034, Tmdb("h632", Close));

        outcome.Should().Be(HeadshotGallery.HideOutcome.Hidden);
        (await db.PersonHeadshots.Where(h => h.PersonMediaItemId == 1 && h.IsHidden).Select(h => h.Id).ToListAsync())
            .Should().BeEquivalentTo([304034]);
        (await db.PersonHeadshots.SingleAsync(h => h.Id == 900)).IsHidden.Should().BeFalse();
    }

    [Fact]
    public async Task Hiding_one_size_of_a_photo_hides_all_its_sizes()
    {
        await using var db = NewDb();
        db.PersonHeadshots.AddRange(Yanevski());
        await db.SaveChangesAsync();

        // Hide the old man's photo by pretending the close-up is not the current one.
        var outcome = await HeadshotGallery.HideAsync(db, 1, headshotId: 304032, currentPosterUrl: Tmdb("original", Wrong));

        outcome.Should().Be(HeadshotGallery.HideOutcome.Hidden);
        (await db.PersonHeadshots.Where(h => h.Url.EndsWith(Close)).ToListAsync()).Should().OnlyContain(h => h.IsHidden);
    }

    [Fact]
    public async Task The_photo_currently_shown_cannot_be_hidden_in_any_of_its_sizes()
    {
        await using var db = NewDb();
        db.PersonHeadshots.AddRange(Yanevski());
        await db.SaveChangesAsync();

        var outcome = await HeadshotGallery.HideAsync(db, 1, headshotId: 304033, currentPosterUrl: Tmdb("h632", Close));

        outcome.Should().Be(HeadshotGallery.HideOutcome.IsCurrent);
        (await db.PersonHeadshots.AnyAsync(h => h.IsHidden)).Should().BeFalse();
    }

    [Fact]
    public async Task Hiding_a_photo_that_belongs_to_another_person_is_not_found()
    {
        await using var db = NewDb();
        db.PersonHeadshots.Add(Row(900, Tmdb("w500", Wrong), person: 2));
        await db.SaveChangesAsync();

        (await HeadshotGallery.HideAsync(db, personId: 1, headshotId: 900, null)).Should().Be(HeadshotGallery.HideOutcome.NotFound);
        (await HeadshotGallery.HideAsync(db, personId: 1, headshotId: 12345, null)).Should().Be(HeadshotGallery.HideOutcome.NotFound);
    }

    [Fact]
    public async Task Restoring_makes_every_hidden_photo_of_the_person_visible_again()
    {
        await using var db = NewDb();
        db.PersonHeadshots.AddRange(Row(1, Tmdb("w500", Wrong), hidden: true), Row(2, Tmdb("w500", Close), hidden: true),
            Row(3, "https://x.example/a.jpg", person: 2, hidden: true));
        await db.SaveChangesAsync();

        var restored = await HeadshotGallery.RestoreAllAsync(db, personId: 1);

        restored.Should().Be(2);
        (await db.PersonHeadshots.Where(h => h.PersonMediaItemId == 1).ToListAsync()).Should().OnlyContain(h => !h.IsHidden);
        (await db.PersonHeadshots.SingleAsync(h => h.Id == 3)).IsHidden.Should().BeTrue();   // another person's: untouched
    }
}
