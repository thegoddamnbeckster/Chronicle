using Chronicle.Services.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ILogger = Serilog.ILogger;
using Log = Serilog.Log;

namespace Chronicle.API.Controllers;

/// <summary>
/// Aggregates all themes from every loaded <c>IThemePlugin</c> and serves them
/// to the frontend. Themes are unauthenticated so the login page can also be themed.
/// </summary>
[ApiController]
[Route("api/v1/themes")]
[AllowAnonymous]
public sealed class ThemesController(IPluginRegistry registry) : ControllerBase
{
    private static readonly ILogger _log = Log.ForContext<ThemesController>();

    /// <summary>
    /// Returns all themes from all loaded theme plugins, in plugin registration order.
    /// Each entry includes the plugin ID so the frontend can attribute the theme's origin.
    /// Plugins that throw during <c>GetThemes()</c> are skipped so one bad plugin cannot
    /// prevent all other theme plugins from contributing their themes.
    /// </summary>
    [HttpGet]
    public IActionResult GetAll()
    {
        var themes = new List<object>();

        foreach (var plugin in registry.GetThemePlugins())
        {
            IEnumerable<object> pluginThemes;
            try
            {
                pluginThemes = plugin.GetThemes().Select(t => (object)new
                {
                    pluginId    = plugin.PluginId,
                    key         = t.Key,
                    label       = t.Label,
                    description = t.Description,
                    // Normalise to exactly three swatches — pad or truncate.
                    // The TypeScript DTO expects a [string, string, string] tuple
                    // so we must never return fewer or more than three.
                    swatches    = NormaliseSwatches(t.Swatches),
                    variables   = t.Variables,
                });
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Plugin {PluginId} threw while enumerating themes — skipping", plugin.PluginId);
                continue;
            }

            themes.AddRange(pluginThemes);
        }

        return Ok(new { success = true, data = themes });
    }

    /// <summary>
    /// Ensures the swatch array is exactly three entries.
    /// Extra entries are discarded; missing entries are padded with <c>"#808080"</c> (mid-grey).
    /// </summary>
    private static string[] NormaliseSwatches(string[] raw)
    {
        if (raw.Length == 3) return raw;
        var result = new string[3];
        for (var i = 0; i < 3; i++)
            result[i] = i < raw.Length ? raw[i] : "#808080";
        return result;
    }
}
