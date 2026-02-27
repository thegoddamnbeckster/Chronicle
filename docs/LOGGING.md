# Chronicle Logging System

**Version:** 1.0  
**Last Updated:** 2026-01-12  
**Author:** Michael Beck with Anthropic Claude

---

## Overview

Chronicle implements a log4j-style logging system with size-based file rollover, configurable log levels, and per-component logging. All logging is thread-safe and supports multi-threaded applications.

---

## Log Format

Every log entry follows this format:

```
[TIMESTAMP] [LEVEL] [THREAD] [COMPONENT.METHOD] Message
```

**Example:**
```
[2026-01-12 15:30:45.123] [INFO ] [Thread-5] [Chronicle.Services.MetadataService.FetchMetadataAsync] Fetching metadata for "Blade Runner"
[2026-01-12 15:30:45.456] [DEBUG] [Thread-5] [Plugin.TMDB.SearchAsync] Sending API request to TMDB
[2026-01-12 15:30:45.789] [ERROR] [Thread-12] [Chronicle.Data.DatabaseService.SaveAsync] Database constraint failed: Foreign key violation
```

---

## Log Levels

| Level | Purpose | When to Use |
|-------|---------|-------------|
| **TRACE** | Very detailed debugging | Rarely used, extremely verbose |
| **DEBUG** | Detailed diagnostic info | Development, troubleshooting |
| **INFO** | General informational | Normal operations, milestones |
| **WARN** | Warning, unexpected but handled | Recoverable errors, deprecations |
| **ERROR** | Error, operation failed | Exceptions, failures |
| **FATAL** | Fatal error, cannot continue | Application crashes |

---

## Configuration

### config.json

```json
{
  "logging": {
    "directory": "/data/logs",
    "max_file_size_mb": 25,
    "max_backup_files": 10,
    "levels": {
      "Chronicle": "INFO",
      "Chronicle.Services": "DEBUG",
      "Chronicle.Data": "INFO",
      "Plugins.TMDB": "DEBUG",
      "Plugins.IMDb": "WARN"
    }
  }
}
```

### Level Hierarchy

Setting a level includes all higher-priority levels:
- **TRACE** = TRACE + DEBUG + INFO + WARN + ERROR + FATAL
- **INFO** = INFO + WARN + ERROR + FATAL (default)
- **ERROR** = ERROR + FATAL only

---

## File Management

### Rollover Strategy

**Size-Based Rollover:**
- Log files roll over when they reach 25MB
- NO time-based rollover
- Immediate rollover when size limit hit

### File Naming Pattern

```
/data/logs/
├── chronicle.log           (current, 0-25MB)
├── chronicle.log.1         (most recent backup, 25MB)
├── chronicle.log.2         (25MB)
├── chronicle.log.3         (25MB)
├── ...
├── chronicle.log.9         (25MB)
└── chronicle.log.10        (deleted when .11 created)
```

### Rollover Process

1. Current log reaches 25MB
2. Close current log file
3. Delete `chronicle.log.10` (if exists)
4. Rename `chronicle.log.9` → `chronicle.log.10`
5. Rename `chronicle.log.8` → `chronicle.log.9`
6. ... (shift all)
7. Rename `chronicle.log` → `chronicle.log.1`
8. Create new empty `chronicle.log`
9. Log rollover event in new file

---

## Plugin Logs

Each plugin gets its own log file:

```
/data/logs/plugins/
├── tmdb-scraper.log
├── tmdb-scraper.log.1
├── imdb-scraper.log
├── imdb-scraper.log.1
└── musicbrainz-scraper.log
```

**Same rollover rules apply:**
- 25MB max per file
- 10 backups maximum
- Size-based rollover

---

## Implementation

### Logger Class

```csharp
public class Logger
{
    private readonly string _logPath;
    private readonly string _componentName;
    private readonly string _version;
    private readonly LogLevel _minLevel;
    private readonly int _maxFileSizeMB = 25;
    private readonly int _maxBackupFiles = 10;
    private readonly object _lock = new object();
    private StreamWriter _writer;

    public enum LogLevel
    {
        TRACE = 0,
        DEBUG = 1,
        INFO = 2,
        WARN = 3,
        ERROR = 4,
        FATAL = 5
    }

    public void Info(string message, [CallerMemberName] string methodName = "")
        => Log(LogLevel.INFO, message, methodName);

    public void Error(string message, Exception ex = null, 
                     [CallerMemberName] string methodName = "")
    {
        var fullMessage = ex != null 
            ? $"{message}: {ex.GetType().Name} - {ex.Message}"
            : message;
        Log(LogLevel.ERROR, fullMessage, methodName);
    }

    private void Log(LogLevel level, string message, string methodName)
    {
        if (level < _minLevel) return;

        lock (_lock)
        {
            CheckAndRollover();
            
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var threadId = Thread.CurrentThread.ManagedThreadId;
            var levelStr = level.ToString().PadRight(5);
            var fullMethodName = $"{_componentName}.{methodName}";
            
            var logEntry = $"[{timestamp}] [{levelStr}] [Thread-{threadId}] [{fullMethodName}] {message}";
            
            _writer.WriteLine(logEntry);
        }
    }

    private void CheckAndRollover()
    {
        var fileInfo = new FileInfo(_logPath);
        
        if (fileInfo.Exists && fileInfo.Length >= (_maxFileSizeMB * 1024 * 1024))
        {
            Rollover();
        }
    }

    private void Rollover()
    {
        _writer?.Close();
        _writer?.Dispose();

        // Delete oldest backup
        var oldestBackup = $"{_logPath}.{_maxBackupFiles}";
        if (File.Exists(oldestBackup))
            File.Delete(oldestBackup);

        // Shift existing backups
        for (int i = _maxBackupFiles - 1; i >= 1; i--)
        {
            var current = $"{_logPath}.{i}";
            var next = $"{_logPath}.{i + 1}";
            if (File.Exists(current))
                File.Move(current, next);
        }

        // Move current to .1
        if (File.Exists(_logPath))
            File.Move(_logPath, $"{_logPath}.1");

        // Create new log
        _writer = new StreamWriter(_logPath, append: false)
        {
            AutoFlush = true
        };

        Info($"Log rolled over. Chronicle v{_version}");
    }
}
```

### Logger Factory

```csharp
public static class LoggerFactory
{
    private static readonly Dictionary<string, Logger> _loggers = new();
    private static string _logDirectory = "/data/logs";

    public static Logger GetLogger(string componentName, string version)
    {
        lock (_loggers)
        {
            if (!_loggers.ContainsKey(componentName))
            {
                var logPath = Path.Combine(_logDirectory, 
                    $"{SanitizeComponentName(componentName)}.log");
                var minLevel = GetConfiguredLevel(componentName);
                
                _loggers[componentName] = new Logger(componentName, version, 
                    logPath, minLevel);
            }
            
            return _loggers[componentName];
        }
    }

    public static Logger GetPluginLogger(string pluginName, string version)
    {
        var pluginLogDir = Path.Combine(_logDirectory, "plugins");
        Directory.CreateDirectory(pluginLogDir);
        
        var logPath = Path.Combine(pluginLogDir, 
            $"{SanitizeComponentName(pluginName)}.log");
        var minLevel = GetConfiguredLevel($"Plugins.{pluginName}");
        
        return new Logger($"Plugin.{pluginName}", version, logPath, minLevel);
    }
}
```

---

## Usage Examples

### Application Code

```csharp
public class MetadataService
{
    private static readonly Logger Logger = LoggerFactory.GetLogger(
        "Chronicle.Services.MetadataService", "1.2.0");

    public async Task<MediaMetadata> FetchMetadataAsync(string query)
    {
        Logger.Info($"Fetching metadata for: \"{query}\"");
        
        try
        {
            Logger.Debug($"Starting with {_scrapers.Count} scrapers");
            
            var results = await SearchAsync(query);
            
            if (results == null)
            {
                Logger.Warn($"No results found for: \"{query}\"");
                return null;
            }
            
            Logger.Info($"Found {results.Count} results");
            return results.First();
        }
        catch (HttpRequestException ex)
        {
            Logger.Error($"Network error fetching \"{query}\"", ex);
            throw;
        }
        catch (Exception ex)
        {
            Logger.Fatal($"Unexpected error for \"{query}\"", ex);
            throw;
        }
    }
}
```

### Plugin Code

```csharp
public class TMDBScraperPlugin : IMetadataProvider
{
    private readonly Logger _logger;

    public TMDBScraperPlugin()
    {
        _logger = LoggerFactory.GetPluginLogger("TMDB", "2.1.0");
    }

    public async Task<MediaMetadata> SearchAsync(string query)
    {
        _logger.Info($"Searching TMDB: \"{query}\"");
        
        var url = BuildApiUrl(query);
        _logger.Debug($"API URL: {url}");
        
        try
        {
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"TMDB returned {response.StatusCode}");
                return null;
            }
            
            var results = await ParseAsync(response);
            _logger.Info($"TMDB returned {results.Count} results");
            
            return results;
        }
        catch (TaskCanceledException ex)
        {
            _logger.Error("TMDB timeout", ex);
            throw;
        }
    }
}
```

---

## Best Practices

### DO
- ✅ Use appropriate log levels
- ✅ Include context (IDs, names, counts)
- ✅ Log entry and exit of critical methods
- ✅ Log all errors with exceptions
- ✅ Use structured messages

### DON'T
- ❌ Log sensitive data (passwords, tokens, PII)
- ❌ Log in tight loops (impacts performance)
- ❌ Use TRACE in production
- ❌ Include full stack traces (NOT helpful per requirements)
- ❌ Log redundant information

### Example - Good Logging

```csharp
Logger.Info($"User {userId} started session");
Logger.Debug($"Loading {mediaIds.Count} media items from database");
Logger.Warn($"Scraper {scraperName} returned 0 results for query: {query}");
Logger.Error($"Failed to save media ID={mediaId} to database", ex);
```

### Example - Bad Logging

```csharp
Logger.Debug($"Password: {password}");  // ❌ Sensitive data
Logger.Trace($"Loop iteration {i}");     // ❌ Too verbose
Logger.Info($"Method started");          // ❌ Not useful
Logger.Error($"Error: {ex.StackTrace}"); // ❌ No stack traces
```

---

## Performance Considerations

### Thread Safety
- All logging is thread-safe via `lock`
- Multiple threads can log simultaneously
- No race conditions or corruption

### Auto-Flush
- `AutoFlush = true` ensures immediate writes
- Critical for crash scenarios
- Slight performance overhead accepted

### File I/O
- File operations happen in locked section
- Prevents corruption
- Rollover is synchronous (blocks briefly)

---

## Troubleshooting

### No Logs Appearing
1. Check directory permissions
2. Verify `/data/logs` exists
3. Check configuration file syntax
4. Ensure log level allows messages

### Logs Not Rolling Over
1. Verify file size threshold
2. Check disk space
3. Ensure write permissions
4. Review rollover logic

### Performance Impact
1. Increase log level (ERROR instead of DEBUG)
2. Disable verbose plugins
3. Check disk I/O speed
4. Consider separate log drive

---

## Future Enhancements

### Phase 2+
- Remote logging (syslog, Splunk)
- JSON-formatted logs (machine-readable)
- Log compression (gzip old logs)
- Structured logging (key-value pairs)

### Phase 3+
- Centralized logging (Elasticsearch, Loki)
- Real-time log streaming (WebSocket)
- Log analysis (detect patterns, alerts)
- Retention policies (auto-delete after X days)

---

**Document Status:** Implementation Ready  
**Implementation Priority:** Phase 1 (MVP)
