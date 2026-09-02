using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Chronicle.Core.Helpers;

/// <summary>
/// Converts an arbitrary XML document into a generic, lossless JSON tree -- every element
/// (including repeated siblings, captured as arrays) and every attribute is preserved, with
/// no hand-picked field allowlist. Written for storing local NFO sidecar files losslessly
/// per Chronicle's "lossless ingestion" architecture rule (see CLAUDE.md): unlike a typed
/// parser that only knows about the fields someone thought to add, this never silently drops
/// a tag a scraper, Kodi itself, or a fan-edit's custom NFO happens to write that Chronicle
/// doesn't specifically recognize -- movie, tvshow, episodedetails, and musicvideo NFOs (and
/// anything else shaped like XML) all round-trip through the exact same code path.
///
/// Convention (a common, unambiguous XML-to-JSON mapping): an attribute becomes an "@name"
/// key; an element's own text content becomes "#text" (only emitted alongside child elements
/// or attributes -- a pure text leaf like &lt;title&gt;Foo&lt;/title&gt; is just the plain
/// string "Foo", not a wrapper object); a repeated child element name becomes a JSON array.
/// </summary>
public static class XmlToJsonConverter
{
    /// <summary>Returns null if <paramref name="xml"/> is empty or not well-formed XML.</summary>
    public static JsonElement? ToJson(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = XDocument.Parse(xml.Trim());
            if (doc.Root is null) return null;

            var node = ElementToNode(doc.Root);
            using var jsonDoc = JsonDocument.Parse(node.ToJsonString());
            return jsonDoc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static JsonNode ElementToNode(XElement element)
    {
        var attributes    = element.Attributes().ToList();
        var childElements = element.Elements().ToList();

        if (attributes.Count == 0 && childElements.Count == 0)
        {
            // Pure leaf: just the trimmed text value, so simple tags like
            // <title>Foo</title> round-trip as a plain JSON string, not a wrapper object.
            return JsonValue.Create(element.Value.Trim())!;
        }

        var obj = new JsonObject();
        foreach (var attr in attributes)
            obj[$"@{attr.Name.LocalName}"] = attr.Value;

        var directText = element.Nodes().OfType<XText>()
            .Select(t => t.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);
        if (directText is not null)
            obj["#text"] = directText;

        foreach (var group in childElements.GroupBy(e => e.Name.LocalName))
        {
            var items = group.Select(ElementToNode).ToList();
            obj[group.Key] = items.Count == 1 ? items[0] : new JsonArray(items.ToArray());
        }

        return obj;
    }
}
