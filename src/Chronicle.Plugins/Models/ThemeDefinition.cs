namespace Chronicle.Plugins.Models;

/// <summary>
/// Describes a single theme provided by an <see cref="IThemePlugin"/>.
/// The <see cref="Variables"/> dictionary maps CSS custom-property names
/// (e.g. <c>--bg-primary</c>) to their values for this theme.
/// The frontend applies the theme by setting each variable directly on
/// <c>document.documentElement.style</c>, overriding the light-theme
/// defaults declared in the app's base stylesheet.
/// </summary>
/// <param name="Key">
/// Machine-readable identifier, unique within the plugin (e.g. <c>"dark-teal"</c>).
/// Stored in user preferences to re-apply the theme on next load.
/// </param>
/// <param name="Label">Human-readable name shown in the UI (e.g. <c>"Dark Teal"</c>).</param>
/// <param name="Description">Short descriptive line shown below the label (e.g. <c>"Dark teal with green accent"</c>).</param>
/// <param name="Swatches">
/// Exactly three hex colour strings representing [background, card/midtone, accent].
/// Used to render the small colour-preview dots in the theme picker.
/// </param>
/// <param name="Variables">
/// Full set of CSS custom-property overrides for this theme.
/// Must include every variable listed in the base stylesheet's <c>:root</c> block
/// so that no fallback leaks through when the theme is applied.
/// </param>
public record ThemeDefinition(
    string Key,
    string Label,
    string Description,
    string[] Swatches,
    IReadOnlyDictionary<string, string> Variables
);
