using Chronicle.Plugins.Models;

namespace Chronicle.Plugins;

/// <summary>
/// Implemented by theme plugins that supply visual themes for the Chronicle UI.
///
/// A theme plugin declares one or more <see cref="ThemeDefinition"/> records, each
/// containing a full set of CSS custom-property values. The Chronicle frontend fetches
/// all available themes from every loaded theme plugin via <c>GET /api/v1/themes</c>
/// and applies the user's chosen theme by setting those CSS variables on
/// <c>document.documentElement.style</c>.
///
/// Multiple theme plugins can coexist — all themes from all plugins appear in the
/// Plugins → Themes section of the UI. Plugin authors are free to create and publish
/// additional theme packs.
///
/// Theme plugins do not require settings, lifecycle management, or async operations,
/// so there is no <c>Configure</c> or health-check method.
/// </summary>
public interface IThemePlugin
{
    /// <summary>Unique reverse-domain plugin identifier, e.g. <c>"chronicle.plugin.themes.default"</c>.</summary>
    string PluginId { get; }

    string Name    { get; }
    string Version { get; }
    string Author  { get; }

    /// <summary>Returns all themes provided by this plugin.</summary>
    IReadOnlyList<ThemeDefinition> GetThemes();
}
