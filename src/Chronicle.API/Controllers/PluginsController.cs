using Chronicle.API.DTOs;
using Chronicle.Services.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/plugins")]
[Authorize]
public class PluginsController : ControllerBase
{
    private readonly IPluginService _pluginService;
    private readonly IPluginRegistry _registry;

    public PluginsController(IPluginService pluginService, IPluginRegistry registry)
    {
        _pluginService = pluginService;
        _registry = registry;
    }

    /// <summary>Lists all installed plugins.</summary>
    [HttpGet]
    public async Task<IActionResult> GetPlugins()
    {
        var plugins = await _pluginService.GetAllPluginsAsync();
        var dtos = plugins.Select(ToDto).ToList();
        return Ok(ApiResponse<List<PluginDto>>.Ok(dtos));
    }

    /// <summary>Gets a single installed plugin by its database id.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetPlugin(int id)
    {
        var plugin = await _pluginService.GetPluginAsync(id);
        if (plugin is null)
            return NotFound(ApiResponse<PluginDto>.Fail("PLUGIN_NOT_FOUND", "Plugin not found."));
        return Ok(ApiResponse<PluginDto>.Ok(ToDto(plugin)));
    }

    /// <summary>
    /// Installs a plugin from a DLL path on the server's filesystem.
    /// Admin only.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> InstallPlugin([FromBody] InstallPluginRequest request)
    {
        try
        {
            var plugin = await _pluginService.InstallPluginAsync(request.DllPath);
            return Ok(ApiResponse<PluginDto>.Ok(ToDto(plugin)));
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(ApiResponse<PluginDto>.Fail("DLL_NOT_FOUND", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiResponse<PluginDto>.Fail("ALREADY_INSTALLED", ex.Message));
        }
    }

    /// <summary>Updates the settings for a plugin.</summary>
    [HttpPut("{id:int}/settings")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateSettings(int id, [FromBody] UpdatePluginSettingsRequest request)
    {
        try
        {
            await _pluginService.UpdateSettingsAsync(id, request.Settings);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", ex.Message));
        }
    }

    /// <summary>Enables an installed plugin.</summary>
    [HttpPost("{id:int}/enable")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> EnablePlugin(int id)
    {
        try
        {
            await _pluginService.EnablePluginAsync(id);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", ex.Message));
        }
    }

    /// <summary>Disables a plugin (unloads from memory, keeps database record).</summary>
    [HttpPost("{id:int}/disable")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DisablePlugin(int id)
    {
        try
        {
            await _pluginService.DisablePluginAsync(id);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", ex.Message));
        }
    }

    /// <summary>Uninstalls a plugin (removes database record, unloads from memory).</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UninstallPlugin(int id)
    {
        try
        {
            await _pluginService.UninstallPluginAsync(id);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", ex.Message));
        }
    }

    /// <summary>Runs the plugin's health check and returns the result.</summary>
    [HttpGet("{id:int}/health")]
    public async Task<IActionResult> HealthCheck(int id)
    {
        var result = await _pluginService.HealthCheckAsync(id);
        if (result is null)
            return NotFound(ApiResponse<PluginHealthDto>.Fail("PLUGIN_NOT_LOADED", "Plugin not found or not loaded."));
        return Ok(ApiResponse<PluginHealthDto>.Ok(new PluginHealthDto(result)));
    }

    private PluginDto ToDto(Chronicle.Core.Models.Plugin p)
    {
        // Look up the loaded plugin so we can include the iconUrl from its manifest.
        // Disabled / unloaded plugins will have IconUrl = null.
        var loaded = _registry.GetLoadedPlugins()
            .FirstOrDefault(lp => lp.DbId == p.Id);

        return new(p.Id, p.PluginId, p.Name, p.Version, p.Author, p.Description,
            p.IsEnabled, p.InstalledAt, p.UpdatedAt, loaded?.Manifest.IconUrl);
    }
}
