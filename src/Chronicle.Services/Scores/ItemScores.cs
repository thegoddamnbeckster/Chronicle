using System.Text.Json;
using Chronicle.Plugins.Models;

namespace Chronicle.Services.Scores;

/// <summary>An enabled plugin as the score collector needs to see it.</summary>
public sealed record ScorePlugin(string PluginId, string PluginName, IReadOnlyList<PluginRatingSource> Sources, int DbId = 0);

/// <summary>One tier of a source's colour scale, already checked: <c>Color</c> and <c>Track</c> are plain hex colours, <c>Icon</c> a name the page draws.</summary>
public sealed record ScoreTier(double Min, string Color, string? Track, string? Icon);

/// <summary>One score to show on an item's page, in its source's own terms.</summary>
public sealed record ItemScore(
    string Key, string? Variant, string DisplayName, double Value, double Scale, string Display, string? Star,
    double Normalized, int? Votes, string? Url, string Source, string PluginId, string? Attribution, IReadOnlyList<string> AlsoFrom,
    string Look = "plain", string? Label = null, string? Shape = null, string? FontFamily = null, string? TextColor = null,
    IReadOnlyList<ScoreTier>? Tiers = null, bool HasLogo = false, int PluginDbId = 0, string? State = null);

/// <summary>
/// Collects every score the plugins hold for one item (pure; no database). Names no plugin: every presentation choice comes
/// from the plugin's manifest <c>rating_sources</c>. See docs/plans/2026-11-24-detail-page-scores.md section 3.1.
/// </summary>
public static class ItemScores
{
    private sealed record Candidate(ScorePlugin Plugin, int Order, ItemScore Score);

    /// <param name="metadataJson">The item's MetadataJson (partitions keyed by full plugin id).</param>
    /// <param name="plugins">Enabled plugins only; a disabled plugin's data is not shown.</param>
    /// <param name="displayOrder">The type's saved plugin order (plugin ids); plugins not in it follow alphabetically.</param>
    public static IReadOnlyList<ItemScore> Collect(string? metadataJson, IEnumerable<ScorePlugin> plugins, IReadOnlyList<string>? displayOrder = null)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return [];
        JsonDocument doc;
        try { doc = JsonDocument.Parse(metadataJson); } catch (JsonException) { return []; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, i) in (displayOrder ?? []).Select((id, i) => (id, i))) order.TryAdd(id, i);
            var candidates = new List<Candidate>();

            foreach (var plugin in plugins)
            {
                if (!doc.RootElement.TryGetProperty(plugin.PluginId, out var part) || part.ValueKind != JsonValueKind.Object) continue;
                var idx = order.TryGetValue(plugin.PluginId, out var o) ? o : int.MaxValue;
                var plain = plugin.Sources.FirstOrDefault(s => s.PlainRating) ?? plugin.Sources.FirstOrDefault();
                var ext = Prop(part, "extendedData");
                var extVotes = ext is { } xe ? Int(xe, "votes") : null;

                var entries = ext is { } xo && Prop(xo, "ratings") is { ValueKind: JsonValueKind.Array } arr
                    ? arr.EnumerateArray().ToList() : [];
                foreach (var e in entries)
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var key = Str(e, "key");
                    if (string.IsNullOrWhiteSpace(key) || Num(e, "value") is not { } value || Num(e, "scale") is not { } scale) continue;
                    var variant = Str(e, "variant");
                    Add(candidates, plugin, idx, Declared(plugin, key, variant), key, variant, value, scale,
                        Int(e, "votes") ?? extVotes ?? Int(part, "votes"), Str(e, "url"), Str(e, "state"));
                }

                // The plain rating: only for a plugin that declared its source, and not when a contract entry already gave that source's score (in any variant).
                if (plain is not null && Num(part, "rating") is { } rating
                    && !candidates.Any(c => c.Plugin == plugin && Same(c.Score.Key, plain.Key)))
                {
                    var stored = plain.StoredScale > 0 ? plain.StoredScale : 10;
                    var scale = plain.Scale > 0 ? plain.Scale : 10;
                    var declaredVotes = !string.IsNullOrWhiteSpace(plain.VotesField) && ext is { } xv ? Int(xv, plain.VotesField) : null;
                    Add(candidates, plugin, idx, plain, plain.Key, plain.Variant, rating / stored * scale,
                        scale, declaredVotes ?? extVotes ?? Int(part, "votes"), null);
                }
            }

            return candidates
                .GroupBy(c => (Key: c.Score.Key.ToLowerInvariant(), Variant: (c.Score.Variant ?? "").ToLowerInvariant()))
                .Select(g =>
                {
                    var ranked = g.OrderBy(c => c.Order)
                        .ThenBy(c => c.Plugin.PluginId, StringComparer.OrdinalIgnoreCase).ToList();
                    var win = ranked[0];
                    var also = ranked.Skip(1).Select(c => c.Plugin.PluginName).Distinct().ToList();
                    return (win.Order, Score: win.Score with { AlsoFrom = also });
                })
                .OrderBy(x => x.Order).ThenBy(x => x.Score.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Score.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Score)
                .ToList();
        }
    }

    private static void Add(List<Candidate> list, ScorePlugin plugin, int order, PluginRatingSource? decl, string key, string? variant,
        double value, double scale, int? votes, string? url, string? state = null)
    {
        // A value above its scale (beyond rounding) is a wrong scale, not a score; it is dropped rather than clamped.
        if (!double.IsFinite(value) || !double.IsFinite(scale) || value <= 0 || scale <= 0 || value > scale * 1.0001) return;
        var display = string.Equals(decl?.Display, "percent", StringComparison.OrdinalIgnoreCase) ? "percent" : "outOf";
        var star = decl?.Star is "single" or "row" ? decl.Star : null;
        var name = string.IsNullOrWhiteSpace(decl?.DisplayName) ? key : decl!.DisplayName!;
        list.Add(new Candidate(plugin, order, new ItemScore(
            key, string.IsNullOrWhiteSpace(variant) ? null : variant, name,
            value, scale, display, star, value / scale * 100, votes is > 0 ? votes : null, SafeUrl(url), plugin.PluginName, plugin.PluginId,
            decl?.Attribution, [],
            LookOf(decl?.Look), Clean(decl?.Label, 40), decl?.Shape is { } sh && sh.Equals("circle", StringComparison.OrdinalIgnoreCase) ? "circle" : null,
            Fonts.FirstOrDefault(f => f.Equals(decl?.FontFamily?.Trim(), StringComparison.OrdinalIgnoreCase)), HexOf(decl?.TextColor),
            TiersOf(decl?.Thresholds), !string.IsNullOrWhiteSpace(decl?.LogoUrl), plugin.DbId,
            Icons.FirstOrDefault(i => i.Equals(state?.Trim(), StringComparison.OrdinalIgnoreCase)))));
    }

    /// <summary>The renderers the page has. A manifest picks one by name; any other word is plain.</summary>
    private static readonly string[] Looks = ["plain", "star-text", "ring", "icon-percent", "badge"];
    /// <summary>The open-licence faces the page knows; a manifest can name one, never supply a file.</summary>
    private static readonly string[] Fonts = ["Roboto", "Source Sans 3", "Libre Franklin", "Nunito Sans"];
    private static readonly string[] Icons = ["fresh", "rotten", "certified", "bucket-full", "bucket-tipped"];

    private static string LookOf(string? look) => Looks.FirstOrDefault(l => l.Equals(look?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "plain";

    // Only plain hex colours reach the page, so a manifest can never put anything else into a style.
    private static string? HexOf(string? c) =>
        c is not null && System.Text.RegularExpressions.Regex.IsMatch(c.Trim(), "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$") ? c.Trim() : null;

    private static string? Clean(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length <= max ? text.Trim() : text.Trim()[..max];

    private static IReadOnlyList<ScoreTier>? TiersOf(List<PluginScoreThreshold>? tiers)
    {
        if (tiers is not { Count: > 0 }) return null;
        var list = tiers.Where(t => double.IsFinite(t.Min) && HexOf(t.Color) is not null)
            .Select(t => new ScoreTier(t.Min, HexOf(t.Color)!, HexOf(t.Track), Icons.FirstOrDefault(i => i.Equals(t.Icon?.Trim(), StringComparison.OrdinalIgnoreCase))))
            .OrderBy(t => t.Min).Take(8).ToList();
        return list.Count == 0 ? null : list;
    }

    private static PluginRatingSource? Declared(ScorePlugin p, string key, string? variant) =>
        p.Sources.FirstOrDefault(s => Same(s.Key, key) && Same(s.Variant, variant))
        ?? p.Sources.FirstOrDefault(s => Same(s.Key, key) && string.IsNullOrEmpty(s.Variant));

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    // Only http(s) links reach the page; anything else a plugin supplies is dropped.
    private static string? SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.AbsoluteUri : null;

    private static JsonElement? Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;
    private static string? Str(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    private static double? Num(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? d : null;
    private static int? Int(JsonElement e, string name) => Num(e, name) is { } d && d is >= 0 and <= int.MaxValue ? (int)d : null;
}
