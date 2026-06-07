using Chronicle.Services.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    /// <summary>
    /// Returns all themes from all loaded theme plugins, in plugin registration order.
    /// Each entry includes the plugin ID so the frontend can attribute the theme's origin.
    /// </summary>
    [HttpGet]
    public IActionResult GetAll()
    {
        var themes = registry.GetThemePlugins()
            .SelectMany(plugin => plugin.GetThemes().Select(t => new
            {
                pluginId    = plugin.PluginId,
                key         = t.Key,
                label       = t.Label,
                description = t.Description,
                swatches    = t.Swatches,
                variables   = t.Variables,
            }))
            .ToList();

        return Ok(new { success = true, data = themes });
    }
}
