# Chronicle Plugin System

**Version:** 1.0  
**Last Updated:** 2026-01-12  
**Author:** Michael Beck with Anthropic Claude

---

## Overview

Chronicle's plugin system is the foundation of its extensibility. Plugins provide metadata scraping, define new media types, create dashboard widgets, and more. The system is designed so that NO functionality is hardcoded - everything goes through plugins.

---

## Plugin Types

### 1. Metadata Scraper Plugins
Fetch metadata from external sources (TMDB, MusicBrainz, etc.)

### 2. Media Type Plugins  
Define new media types (Board Games, Podcasts, etc.)

### 3. Widget Plugins
Create custom dashboard widgets

### 4. Export/Import Plugins
Handle data export/import in various formats

### 5. Notification Plugins
Send notifications (Discord, Email, Webhooks)

---

## Metadata Scraper Plugin

### Interface

```csharp
public interface IMetadataProvider
{
    // Plugin identity
    string Name { get; }
    string Version { get; }
    
    // Define what this plugin supports
    MediaTypeSupport[] GetSupportedMediaTypes();
    
    // Define configurable settings
    PluginSettingsSchema GetSettingsSchema();
    
    // Core scraping methods
    Task<MediaMetadata> SearchAsync(string query);
    Task<MediaMetadata> GetByIdAsync(string id);
    Task<byte[]> GetImageAsync(string url);
    
    // Health check
    Task<bool> HealthCheckAsync();
}

public class MediaTypeSupport
{
    public string MediaTypeName { get; set; }  
    public List<string> SupportedFields { get; set; }  
    public int DefaultPriority { get; set; }
}
```

### Example Implementation - TMDB Plugin

```csharp
public class TMDBScraperPlugin : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly Logger _logger;
    private PluginSettings _settings;

    public string Name => "TMDB";
    public string Version => "2.1.0";

    public MediaTypeSupport[] GetSupportedMediaTypes()
    {
        return new[]
        {
            new MediaTypeSupport
            {
                MediaTypeName = "movie",
                SupportedFields = new List<string>
                {
                    "title", "description", "release_date", 
                    "runtime", "poster_url", "backdrop_url",
                    "genres", "cast", "directors", "rating"
                },
                DefaultPriority = 1
            },
            new MediaTypeSupport
            {
                MediaTypeName = "tv",
                SupportedFields = new List<string>
                {
                    "title", "description", "first_air_date",
                    "poster_url", "backdrop_url", "genres",
                    "cast", "creators", "network"
                },
                DefaultPriority = 1
            }
        };
    }

    public PluginSettingsSchema GetSettingsSchema()
    {
        return new PluginSettingsSchema
        {
            Settings = new List<SettingDefinition>
            {
                new SettingDefinition
                {
                    Key = "api_key",
                    Label = "TMDB API Key",
                    Description = "Get from https://themoviedb.org/settings/api",
                    Type = SettingType.Password,
                    Required = true
                },
                new SettingDefinition
                {
                    Key = "language",
                    Label = "Language",
                    Type = SettingType.Dropdown,
                    DefaultValue = "en-US",
                    Options = new List<SelectOption>
                    {
                        new SelectOption { Value = "en-US", Label = "English" },
                        new SelectOption { Value = "fr-FR", Label = "French" }
                    }
                },
                new SettingDefinition
                {
                    Key = "include_adult",
                    Label = "Include Adult Content",
                    Type = SettingType.Boolean,
                    DefaultValue = false
                }
            }
        };
    }

    public async Task<MediaMetadata> SearchAsync(string query)
    {
        _logger.Info($"Searching TMDB for: {query}");
        
        var apiKey = _settings.Get<string>("api_key");
        var language = _settings.Get<string>("language");
        
        var url = $"https://api.themoviedb.org/3/search/movie?" +
                  $"query={Uri.EscapeDataString(query)}&" +
                  $"api_key={apiKey}&language={language}";
        
        _logger.Debug($"API URL: {url}");
        
        var response = await _httpClient.GetAsync(url);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.Warn($"TMDB returned {response.StatusCode}");
            return null;
        }
        
        var json = await response.Content.ReadAsStringAsync();
        var results = ParseResults(json);
        
        _logger.Info($"Found {results.Count} results");
        return results;
    }

    public async Task<bool> HealthCheckAsync()
    {
        try
        {
            var apiKey = _settings.Get<string>("api_key");
            var url = $"https://api.themoviedb.org/3/configuration?api_key={apiKey}";
            
            var response = await _httpClient.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
```

---

## Plugin Settings

### Settings Schema

```csharp
public class PluginSettingsSchema
{
    public List<SettingDefinition> Settings { get; set; }
}

public class SettingDefinition
{
    public string Key { get; set; }              
    public string Label { get; set; }            
    public string Description { get; set; }      
    public SettingType Type { get; set; }        
    public bool Required { get; set; }
    public object DefaultValue { get; set; }
    public List<SelectOption> Options { get; set; }
}

public enum SettingType
{
    Text,
    Password,
    Number,
    Boolean,
    Dropdown,
    MultiSelect,
    Url,
    FilePath,
    TextArea
}
```

### Chronicle Renders UI Automatically

Based on schema, Chronicle generates:

```
[TMDB Scraper Settings]

API Key: [_____________________________] (required)
         Get from https://themoviedb.org/settings/api

Language: [English (US) ▼]

☐ Include Adult Content

[Test Connection] [Save Settings]
```

---

## Plugin Installation

### Manual Installation

1. Copy plugin DLL to `/plugins/active/`
2. Copy config JSON to `/plugins/configs/`
3. Restart Chronicle
4. Configure in UI

### Directory Structure

```
/plugins/
├── /active/
│   ├── tmdb-scraper.dll
│   └── custom-plugin.dll
├── /backups/
│   ├── tmdb-scraper-v2.0.0.dll
│   └── tmdb-scraper-v1.9.0.dll
└── /configs/
    ├── tmdb-scraper.json
    └── custom-plugin.json
```

---

## Plugin Updates

### Update Manifest (updates.json)

```json
{
  "name": "tmdb-scraper",
  "versions": {
    "stable": {
      "version": "2.1.0",
      "released": "2026-01-10",
      "download_url": "https://github.com/.../v2.1.0/tmdb-scraper.dll",
      "checksum": "sha256:abc123...",
      "changelog": "- Added 4K poster support\n- Fixed rate limiting",
      "min_chronicle_version": "1.0.0"
    },
    "beta": {
      "version": "2.2.0-beta.1",
      "released": "2026-01-11",
      "download_url": "https://github.com/.../v2.2.0-beta.1/tmdb-scraper.dll",
      "checksum": "sha256:def456...",
      "min_chronicle_version": "1.1.0"
    }
  }
}
```

### Update Process

1. Chronicle checks updates.json
2. Downloads new version to temp
3. Verifies checksum
4. Backs up current version
5. Swaps DLL files
6. Runs health check
7. Rollback if health check fails

---

## Scraper Priority System

### Per Media Type Configuration

```
[Movies - Metadata Scrapers]

Field: Title
  1. ✓ TMDB (supports)
  2. ✓ IMDb (supports)
  3. - FanArt.tv (does not support)

Field: Poster
  1. ✓ TMDB (supports)
  2. ✓ FanArt.tv (supports)
  3. - IMDb (does not support)
```

### Database Storage

```sql
CREATE TABLE scraper_priorities (
    id INTEGER PRIMARY KEY,
    media_type_id INTEGER NOT NULL,
    plugin_id INTEGER NOT NULL,
    priority INTEGER NOT NULL,
    supported_fields JSON NOT NULL,
    is_enabled BOOLEAN DEFAULT 1,
    fallback_behavior TEXT DEFAULT 'continue'
);
```

### Auto-Population

When plugin installed:
1. Chronicle reads `GetSupportedMediaTypes()`
2. Automatically adds to priority lists
3. Uses `DefaultPriority` as starting position
4. User can reorder in UI

---

## Media Type Plugin

### Defining New Media Types

```json
{
  "type": "board_games",
  "display_name": "Board Games",
  "hierarchy": ["publisher", "game", "expansion"],
  "interaction_verb": "play",
  "session_noun": "game session",
  "progress_unit": "plays",
  "icon": "dice",
  "default_scrapers": ["boardgamegeek"],
  "fields": [
    {"name": "players", "type": "range"},
    {"name": "duration", "type": "integer"},
    {"name": "complexity", "type": "float"}
  ]
}
```

### Installation

1. Drop JSON file in `/plugins/media-types/`
2. Restart Chronicle
3. Media type immediately available

---

## Widget Plugin

### Interface

```csharp
public interface IWidgetPlugin
{
    string WidgetType { get; }
    string DisplayName { get; }
    string Description { get; }
    
    List<SettingDefinition> GetSettings();
    Task<WidgetData> RenderAsync(WidgetSettings settings);
}
```

### Example - Upcoming Releases Widget

```csharp
public class UpcomingReleasesWidget : IWidgetPlugin
{
    public string WidgetType => "upcoming_releases";
    public string DisplayName => "Upcoming Releases";
    public string Description => "Shows media releasing soon";

    public List<SettingDefinition> GetSettings()
    {
        return new List<SettingDefinition>
        {
            new SettingDefinition
            {
                Key = "days_ahead",
                Label = "Days to Look Ahead",
                Type = SettingType.Number,
                DefaultValue = 7
            },
            new SettingDefinition
            {
                Key = "media_types",
                Label = "Media Types",
                Type = SettingType.MultiSelect,
                Options = new List<SelectOption>
                {
                    new SelectOption { Value = "movie", Label = "Movies" },
                    new SelectOption { Value = "tv", Label = "TV Shows" }
                }
            }
        };
    }

    public async Task<WidgetData> RenderAsync(WidgetSettings settings)
    {
        var daysAhead = settings.Get<int>("days_ahead");
        var mediaTypes = settings.Get<List<string>>("media_types");
        
        var releases = await GetUpcomingReleasesAsync(daysAhead, mediaTypes);
        
        return new WidgetData
        {
            Title = "Upcoming Releases",
            Items = releases,
            Template = "list"
        };
    }
}
```

---

## Plugin Logging

Each plugin gets its own log file:

```csharp
public class MyPlugin : IMetadataProvider
{
    private readonly Logger _logger;

    public MyPlugin()
    {
        _logger = LoggerFactory.GetPluginLogger("MyPlugin", "1.0.0");
    }

    public async Task DoSomethingAsync()
    {
        _logger.Info("Starting operation");
        _logger.Debug("Detailed info here");
        _logger.Error("Something failed", exception);
    }
}
```

Output: `/data/logs/plugins/myplugin.log`

---

## Plugin Development Best Practices

### DO
- ✅ Declare all supported media types
- ✅ Declare all supported fields
- ✅ Implement comprehensive settings schema
- ✅ Include health check
- ✅ Log all operations
- ✅ Handle errors gracefully
- ✅ Respect rate limits
- ✅ Cache results when appropriate

### DON'T
- ❌ Hardcode API keys (use settings)
- ❌ Block main thread (use async)
- ❌ Throw exceptions without logging
- ❌ Skip health checks
- ❌ Ignore Chronicle version requirements
- ❌ Store state between calls (plugins are stateless)

---

## Plugin Distribution

### GitHub Release

1. Create repository
2. Tag releases (v1.0.0, v1.1.0)
3. Include updates.json
4. Attach DLL to release
5. Include SHA256 checksum

### Custom Hosting

1. Host DLL on web server
2. Provide updates.json endpoint
3. Users add custom URL in Chronicle settings

---

## Testing Plugins

### Unit Tests

```csharp
[Test]
public async Task SearchAsync_ValidQuery_ReturnsResults()
{
    var plugin = new TMDBScraperPlugin();
    plugin.Configure(new PluginSettings
    {
        { "api_key", "test_key" },
        { "language", "en-US" }
    });
    
    var results = await plugin.SearchAsync("Blade Runner");
    
    Assert.NotNull(results);
    Assert.True(results.Count > 0);
}
```

### Integration Tests

```csharp
[Test]
public async Task Plugin_InstallAndExecute_Works()
{
    var chronicle = new ChronicleTestInstance();
    
    await chronicle.InstallPluginAsync("tmdb-scraper.dll");
    var metadata = await chronicle.FetchMetadataAsync("Blade Runner");
    
    Assert.NotNull(metadata);
    Assert.Equal("Blade Runner", metadata.Title);
}
```

---

## Security Considerations

### Sandboxing (Future)
- Plugins run in isolated context
- Limited file system access
- No access to other plugins' data
- Rate limiting per plugin

### Code Signing (Future)
- Verify plugin author
- Prevent tampering
- Trust model

---

**Document Status:** Implementation Ready  
**Implementation Priority:** Phase 1 (Core), Phase 2 (Full Features)
