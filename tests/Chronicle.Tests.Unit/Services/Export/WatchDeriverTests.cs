using Chronicle.Services.Export;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Services.Export;

public class WatchDeriverTests
{
    private static readonly HashSet<string> ImportSources = new(StringComparer.OrdinalIgnoreCase) { "Simkl", "Trakt" };
    private const string Upstairs = "Kodi upstairs";
    private const string Reconciled = "Chronicle Scraper (reconciled from local Kodi playback)";

    private static DateTime T(string utc) => DateTime.SpecifyKind(DateTime.Parse(utc), DateTimeKind.Utc);

    /// <summary>A scrobbler's progress events: one every 30 seconds from <paramref name="from"/> to <paramref name="to"/> percent.</summary>
    private static IEnumerable<LocalEvent> Playback(string start, double from, double to, string device = Upstairs, double step = 0.5)
    {
        var t = T(start);
        for (var p = from; p <= to + 1e-9; p += step, t = t.AddSeconds(30))
            yield return new LocalEvent(t, p, p >= 80, device, false);
    }

    private static DerivedWatches Derive(IEnumerable<LocalEvent> events, bool completed = false, string? completedAt = null) =>
        WatchDeriver.Derive(events, ImportSources, completed, completedAt is null ? null : T(completedAt));

    // ── real playbacks ────────────────────────────────────────────────────────

    [Fact]
    public void AFutureOrPre1970Completion_IsNeverADerivedWatch()
    {
        var events = new[]
        {
            new LocalEvent(DateTime.UtcNow.AddYears(3), 100, true, "kodi", false),
            new LocalEvent(new DateTime(1901, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100, true, "kodi", false),
        };

        var derived = Derive(events, completed: true, completedAt: "2999-01-01T00:00:00Z");

        derived.Playbacks.Should().BeEmpty();
        derived.FirstWatchEvidence.Should().BeNull();
    }

    [Fact]
    public void One_playback_to_the_end_is_one_watch_at_its_last_event()
    {
        var events = Playback("2026-10-15 02:00:00", 60, 99.8).ToList();

        Derive(events).Playbacks.Should().ContainSingle().Which.Should().Be(events[^1].TimestampUtc);
    }

    [Fact]
    public void Brand_New_Day_a_rewatch_after_an_earlier_watch_is_two_playbacks()
    {
        var events = Playback("2026-08-02 02:50:00", 0, 99).Concat(Playback("2026-10-15 02:00:00", 0, 99.8)).ToList();

        var playbacks = Derive(events).Playbacks;

        playbacks.Should().HaveCount(2);
        playbacks[1].Date.Should().Be(new DateTime(2026, 10, 15));
    }

    [Fact]
    public void Finishing_a_film_in_two_sittings_is_one_watch_at_the_final_finish()
    {
        var monday = Playback("2026-10-12 03:00:00", 70, 86).ToList();
        var tuesday = Playback("2026-10-13 02:00:00", 86, 99.5).ToList();

        Derive(monday.Concat(tuesday)).Playbacks.Should().ContainSingle().Which.Should().Be(tuesday[^1].TimestampUtc);
    }

    [Fact]
    public void A_watch_started_over_part_way_is_not_a_new_watch_until_it_reaches_the_end()
    {
        var events = Playback("2026-10-01 20:00:00", 0, 99).Concat(Playback("2026-10-05 20:00:00", 0, 30)).ToList();

        Derive(events).Playbacks.Should().ContainSingle();
    }

    // ── first-watch evidence: never a rewatch ─────────────────────────────────

    [Fact]
    public void Device_reconciled_completions_are_never_playbacks_so_cannot_be_rewatches()
    {
        // A real watch, then a fake "newer" completion pulled in from a device (the SIMKL-import phantom).
        var real = Playback("2026-10-09 22:00:00", 0, 99).ToList();
        var phantom = new LocalEvent(T("2026-10-13 22:52:14"), 100, true, Reconciled, false);

        var derived = Derive(real.Append(phantom), completed: true, completedAt: "2026-10-13 22:52:14");

        derived.Playbacks.Should().ContainSingle().Which.Should().Be(real[^1].TimestampUtc);
    }

    [Fact]
    public void Fight_Club_with_only_reconciled_events_has_first_watch_evidence_and_no_playbacks()
    {
        // The real-library case behind 11,116 false rewatches: Kodi's play-count times, skewed copies and all.
        var events = new[]
        {
            new LocalEvent(T("2026-07-06 12:48:30"), 100, false, Reconciled, false),
            new LocalEvent(T("2026-08-03 15:31:29"), 100, true, Reconciled, false),
            new LocalEvent(T("2026-08-03 21:31:29"), 100, true, Reconciled, false),
        };

        var derived = Derive(events, completed: true, completedAt: "2026-08-03 21:31:29");

        derived.Playbacks.Should().BeEmpty();
        derived.FirstWatchEvidence.Should().Be(T("2026-08-03 15:31:29"));
    }

    [Fact]
    public void Another_services_events_are_first_watch_evidence_not_playbacks()
    {
        // The Abyss: a SIMKL import event whose time differs from SIMKL's own for the same watch.
        var events = new[] { new LocalEvent(T("2024-02-20 20:20:53"), 100, true, "Simkl", false) };

        var derived = Derive(events);

        derived.Playbacks.Should().BeEmpty();
        derived.FirstWatchEvidence.Should().Be(T("2024-02-20 20:20:53"));
    }

    [Fact]
    public void Evidence_is_the_earliest_marked_event_from_any_non_device_source()
    {
        var events = new[]
        {
            new LocalEvent(T("2026-09-20 21:00:00"), 100, true, "Trakt", false),
            new LocalEvent(T("2026-08-02 03:33:56"), 100, true, null, false),
            new LocalEvent(T("2026-07-01 03:33:56"), 100, false, Reconciled, false),     // not marked: ignored
        };

        Derive(events).FirstWatchEvidence.Should().Be(T("2026-08-02 03:33:56"));
    }

    [Fact]
    public void Without_any_event_a_completed_library_entry_is_the_evidence()
    {
        Derive([], completed: true, completedAt: "2026-09-01 12:00:00").FirstWatchEvidence.Should().Be(T("2026-09-01 12:00:00"));
    }

    [Fact]
    public void An_item_that_is_not_completed_and_never_finished_has_nothing()
    {
        var derived = Derive(Playback("2026-10-15 02:00:00", 0, 40), completed: false, completedAt: "2026-09-01 12:00:00");

        derived.Playbacks.Should().BeEmpty();
        derived.FirstWatchEvidence.Should().BeNull();
    }

    [Fact]
    public void Approximate_timestamps_are_never_used()
    {
        var events = new[] { new LocalEvent(T("2026-10-15 02:00:00"), 100, true, "Simkl", IsApproximate: true) };

        var derived = Derive(events);

        derived.Playbacks.Should().BeEmpty();
        derived.FirstWatchEvidence.Should().BeNull();
    }
}
