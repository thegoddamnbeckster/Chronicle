using System.Globalization;

namespace Chronicle.API;

/// <summary>
/// The time zone a calling device says it is in (header <see cref="Header"/>), so Chronicle can speak that device's
/// local time instead of assuming a zone: a Kodi add-on reports its own zone on every request, and the JSON Chronicle
/// returns to it carries timestamps in that zone (with an explicit offset), while timestamps it sends without an
/// offset are read as that zone's local time. Requests without the header (the web UI, other clients) keep the
/// original behaviour: UTC with a trailing Z in, and naive values read as UTC.
///
/// The header value is an IANA/Windows zone id ("America/Denver"), or a fixed UTC offset ("-06:00", "+05:30", "Z")
/// for devices that can only report an offset. Nothing about any particular zone is built in.
/// </summary>
public static class ClientTimeZone
{
    public const string Header = "X-Client-Timezone";
    internal const string ItemKey = "Chronicle.ClientTimeZone";

    /// <summary>The zone the current request declared, or null.</summary>
    public static TimeZoneInfo? Current => Accessor?.HttpContext?.Items[ItemKey] as TimeZoneInfo;

    public static IHttpContextAccessor? Accessor { get; set; }

    /// <summary>Reads the header into the request's items. Unreadable values are ignored (UTC behaviour).</summary>
    public static void Capture(HttpContext ctx)
    {
        if (Parse(ctx.Request.Headers[Header].ToString()) is { } tz)
            ctx.Items[ItemKey] = tz;
    }

    public static TimeZoneInfo? Parse(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 64) return null;

        if (value.Equals("Z", StringComparison.OrdinalIgnoreCase) || value.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        if (value[0] is '+' or '-' && TimeSpan.TryParseExact(value[1..], [@"hh\:mm", "hhmm", "hh"], CultureInfo.InvariantCulture, out var span)
            && span <= TimeSpan.FromHours(14))
        {
            var offset = value[0] == '-' ? -span : span;
            return TimeZoneInfo.CreateCustomTimeZone($"UTC{value}", offset, $"UTC{value}", $"UTC{value}");
        }

        try { return TimeZoneInfo.FindSystemTimeZoneById(value); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    /// <summary>A naive local clock reading in <paramref name="tz"/> to UTC. A reading that does not exist (spring
    /// forward) or exists twice (fall back) takes the zone's standard offset rather than failing the request.</summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var offset = tz.IsInvalidTime(local) ? tz.BaseUtcOffset
                   : tz.IsAmbiguousTime(local) ? tz.BaseUtcOffset
                   : tz.GetUtcOffset(local);
        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }
}
