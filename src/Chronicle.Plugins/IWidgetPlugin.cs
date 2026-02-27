using Chronicle.Plugins.Models;

namespace Chronicle.Plugins;

/// <summary>
/// Implemented by dashboard widget plugins.
/// Widgets are stateless — they receive their settings on every <see cref="RenderAsync"/> call.
/// </summary>
public interface IWidgetPlugin
{
    /// <summary>Unique type identifier used to reference the widget in dashboard config.</summary>
    string WidgetType { get; }

    string DisplayName { get; }
    string Description { get; }

    /// <summary>Returns the settings schema used to generate the widget configuration UI.</summary>
    List<SettingDefinition> GetSettings();

    /// <summary>Renders the widget and returns its data payload for the frontend.</summary>
    Task<WidgetData> RenderAsync(WidgetSettings settings, CancellationToken ct = default);
}
