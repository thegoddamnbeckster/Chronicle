using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronicle.API;

/// <summary>
/// Serialises <see cref="DateTime"/> values as UTC ISO 8601 strings (with a trailing Z).
/// SQLite + EF Core returns <c>DateTimeKind.Unspecified</c> even when the stored value
/// originated from <c>DateTime.UtcNow</c>.  Without this converter System.Text.Json
/// omits the Z suffix, causing JavaScript to misparse the timestamp as local time.
/// With the Z suffix JavaScript's <c>Date</c> constructor correctly interprets the value
/// as UTC, and <c>toLocaleTimeString()</c> converts it to the browser's local timezone —
/// which for a self-hosted installation is the same as the server's local timezone.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dt = reader.GetDateTime();
        return dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            : dt.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        writer.WriteStringValue(utc);
    }
}
