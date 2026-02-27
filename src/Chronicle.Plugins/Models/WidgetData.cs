namespace Chronicle.Plugins.Models;

/// <summary>Rendered output from a widget plugin, consumed by the frontend.</summary>
public class WidgetData
{
    public string Title { get; set; } = string.Empty;

    /// <summary>Template hint for the frontend renderer (e.g. "list", "chart", "stat").</summary>
    public string Template { get; set; } = "list";

    /// <summary>Generic item list — structure depends on the template.</summary>
    public List<Dictionary<string, object?>> Items { get; set; } = [];

    /// <summary>Additional key-value data the frontend may use.</summary>
    public Dictionary<string, object?> Extra { get; set; } = [];
}

/// <summary>Settings bag passed to a widget plugin's RenderAsync call.</summary>
public class WidgetSettings
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public WidgetSettings(IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    public string? Get(string key) =>
        _values.TryGetValue(key, out var v) ? v : null;

    public T? Get<T>(string key) where T : struct
    {
        if (!_values.TryGetValue(key, out var raw))
            return null;
        try { return (T)Convert.ChangeType(raw, typeof(T)); }
        catch { return null; }
    }
}
