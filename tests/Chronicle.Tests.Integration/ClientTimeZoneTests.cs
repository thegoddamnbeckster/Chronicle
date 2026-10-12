using System.Text.Json;
using Chronicle.API;
using Microsoft.AspNetCore.Http;

namespace Chronicle.Tests.Integration;

/// <summary>
/// A Kodi add-on declares its time zone (X-Client-Timezone) and Chronicle answers in that zone and reads offset-less
/// timestamps in it; nothing about any particular zone is built in. Regression: the add-on used to send its local
/// clock as if it were UTC and read Chronicle's UTC as if it were local, which left a six-hour error (Mountain time)
/// in watched/resume comparisons.
/// </summary>
[Collection("ClientTimeZone")]   // the converter reads a process-wide accessor
public class ClientTimeZoneTests : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new UtcDateTimeConverter() } };

    public ClientTimeZoneTests() => ClientTimeZone.Accessor = new HttpContextAccessor();
    public void Dispose() => ClientTimeZone.Accessor = null;

    private static void AsClient(string? header)
    {
        var ctx = new DefaultHttpContext();
        if (header != null) ctx.Request.Headers[ClientTimeZone.Header] = header;
        ClientTimeZone.Capture(ctx);
        ((HttpContextAccessor)ClientTimeZone.Accessor!).HttpContext = ctx;
    }

    private static string Write(DateTime utc) => JsonSerializer.Serialize(utc, Options).Trim('"');
    private static DateTime Read(string iso) => JsonSerializer.Deserialize<DateTime>($"\"{iso}\"", Options);

    [Fact] public void NoHeader_WritesUtcWithZ_AsBefore()
    {
        AsClient(null);
        Assert.Equal("2026-09-04T01:34:43Z", Write(new DateTime(2026, 9, 4, 1, 34, 43, DateTimeKind.Utc)));
    }

    [Fact] public void NoHeader_ReadsNaiveAsUtc_AsBefore()
    {
        AsClient(null);
        var dt = Read("2026-09-03T19:34:48");
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(new DateTime(2026, 9, 3, 19, 34, 48), dt);
    }

    [Fact] public void IanaZone_WritesLocalTimeWithOffset_DstAware()
    {
        AsClient("America/Denver");
        Assert.Equal("2026-09-03T19:34:43-06:00", Write(new DateTime(2026, 9, 4, 1, 34, 43, DateTimeKind.Utc)));   // summer (MDT)
        Assert.Equal("2026-01-15T18:00:00-07:00", Write(new DateTime(2026, 1, 16, 1, 0, 0, DateTimeKind.Utc)));    // winter (MST)
    }

    [Fact] public void DifferentZone_GetsItsOwnTime()
    {
        AsClient("Europe/London");
        Assert.Equal("2026-09-04T02:34:43+01:00", Write(new DateTime(2026, 9, 4, 1, 34, 43, DateTimeKind.Utc)));
    }

    [Fact] public void FixedOffset_Works()
    {
        AsClient("-06:00");
        Assert.Equal("2026-09-03T19:34:43-06:00", Write(new DateTime(2026, 9, 4, 1, 34, 43, DateTimeKind.Utc)));
        AsClient("+05:30");
        Assert.Equal("2026-09-04T07:04:43+05:30", Write(new DateTime(2026, 9, 4, 1, 34, 43, DateTimeKind.Utc)));
    }

    [Fact] public void NaiveTimestamp_IsReadInTheClientsZone()
    {
        AsClient("America/Denver");
        Assert.Equal(new DateTime(2026, 9, 4, 1, 34, 43), Read("2026-09-03T19:34:43"));
        Assert.Equal(DateTimeKind.Utc, Read("2026-09-03T19:34:43").Kind);
    }

    [Fact] public void TimestampWithOffsetOrZ_IsExactWhateverTheHeader()
    {
        AsClient("Europe/London");
        var expected = new DateTime(2026, 9, 4, 1, 34, 43);
        Assert.Equal(expected, Read("2026-09-03T19:34:43-06:00"));
        Assert.Equal(expected, Read("2026-09-04T01:34:43Z"));
    }

    [Fact] public void RoundTrip_ReturnsTheSameInstant()
    {
        AsClient("America/Denver");
        var utc = new DateTime(2026, 11, 1, 7, 30, 0, DateTimeKind.Utc);   // inside the fall-back hour
        Assert.Equal(utc, Read(Write(utc)));
    }

    [Fact] public void NonexistentLocalTime_DoesNotThrow()
    {
        AsClient("America/Denver");
        var dt = Read("2026-03-08T02:30:00");   // skipped by spring forward
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
    }

    [Theory]
    [InlineData("")] [InlineData("garbage")] [InlineData("+25:00")] [InlineData("Mars/Olympus")]
    public void UnusableHeader_FallsBackToUtc(string header)
    {
        AsClient(header);
        Assert.Equal("2026-09-04T01:34:43Z", Write(new DateTime(2026, 9, 4, 1, 34, 43, DateTimeKind.Utc)));
    }

    [Fact] public void NoActiveRequest_WritesUtc()
    {
        ((HttpContextAccessor)ClientTimeZone.Accessor!).HttpContext = null;
        Assert.Equal("2026-09-04T01:34:43Z", Write(new DateTime(2026, 9, 4, 1, 34, 43, DateTimeKind.Utc)));
    }
}
