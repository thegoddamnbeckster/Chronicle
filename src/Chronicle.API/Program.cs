using System.Text;
using Chronicle.API;
using Chronicle.API.Authentication;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Import;
using Chronicle.Services.Plugins;
using Chronicle.Services.Reports;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;

// ── Bootstrap logger (before host is built) ───────────────────────────────────
// Captures startup errors before full Serilog configuration is ready.
// NOTE: CreateLogger() rather than CreateBootstrapLogger() — using the reloadable
// bootstrap logger causes a "logger already frozen" exception when WebApplicationFactory
// reconstructs the host a second time during integration tests.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// ── Windows Service support ───────────────────────────────────────────────────
// No-op when running as a console app; activates service lifetime when started
// by the Windows Service Control Manager.
builder.Host.UseWindowsService(options => options.ServiceName = "Chronicle");

// ── Port configuration ────────────────────────────────────────────────────────
// Reads ports.json from the project root (searched upward from working directory).
// Must happen before any service or host configuration that depends on ports.
var portConfig = PortManager.LoadConfig(Directory.GetCurrentDirectory());
PortManager.CheckPort(portConfig.Api);
builder.WebHost.UseUrls($"http://0.0.0.0:{portConfig.Api}");

// ── Serilog ───────────────────────────────────────────────────────────────────
// Reads retention from appsettings.json ("Serilog:RetainedLogDays").
// Log path uses AppContext.BaseDirectory (next to the exe) so that logs are
// written correctly when running as a Windows service — services start with
// the working directory set to System32, not the install folder.
var retainedLogDays = builder.Configuration.GetValue<int>("Serilog:RetainedLogDays", 30);
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
var logPath = Path.Combine(logDir, "chronicle-.log");

builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        path: logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: retainedLogDays,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext());

// ── Database ──────────────────────────────────────────────────────────────────
// Provider is selected by checking (in order):
//   1. "Database:Provider" in appsettings.json  (sqlite | postgresql)
//   2. DATABASE_PROVIDER environment variable
//   3. Connection string shape — starts with "Host=" → PostgreSQL
//   4. Default → SQLite
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=chronicle.db";

var dbProvider = builder.Configuration.GetValue<string>("Database:Provider")
    ?? Environment.GetEnvironmentVariable("DATABASE_PROVIDER")
    ?? (connectionString.StartsWith("Host=", StringComparison.OrdinalIgnoreCase) ? "postgresql" : "sqlite");

builder.Services.AddDbContext<ChronicleDbContext>(options =>
{
    if (dbProvider.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
        options.UseNpgsql(connectionString);
    else
        options.UseSqlite(connectionString);
});

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IApiTokenService, ApiTokenService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IMediaService, MediaService>();
builder.Services.AddScoped<ILibraryService, LibraryService>();
builder.Services.AddScoped<IScrobbleService, ScrobbleService>();
builder.Services.AddScoped<IStatsService, StatsService>();
builder.Services.AddScoped<IImportService, ImportService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IMediaListService, MediaListService>();
builder.Services.AddScoped<IDeviceAuthService, DeviceAuthService>();
// ScanProgressService is a singleton so the scoped FileScanService (writer) and the
// FileScanController progress endpoint (reader) share the same in-memory state.
builder.Services.AddSingleton<ScanProgressService>();
builder.Services.AddScoped<IFileScanService, FileScanService>();

// ── In-memory cache (used for plugin favicon proxy caching) ───────────────────
builder.Services.AddMemoryCache();

// ── Named HttpClient for fetching external favicons ───────────────────────────
// Separate named client so we can give it a short timeout and safe headers.
builder.Services.AddHttpClient("favicon", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Chronicle/1.0 (+https://github.com/thegoddamnbeckster/Chronicle)");
});

// ── Named HttpClient for GitHub API (plugin catalog downloads) ─────────────────
builder.Services.AddHttpClient("github", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Chronicle/1.0 (+https://github.com/thegoddamnbeckster/Chronicle)");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

    // Optional: authenticate with a GitHub token to raise the rate limit (5 000/hr vs 60/hr)
    // and allow access to repos that require authentication.
    // Configure via GitHub:Token in appsettings.Development.json (never commit that file).
    var githubToken = builder.Configuration["GitHub:Token"];
    if (!string.IsNullOrWhiteSpace(githubToken))
        c.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", githubToken);
});

// ── Plugin system ─────────────────────────────────────────────────────────────
// PluginRegistry is a singleton — it holds the in-process AssemblyLoadContexts.
// PluginService is scoped so it can access the request-scoped DbContext.
// PluginHostService loads all enabled plugins from the database on startup.
builder.Services.AddSingleton<IPluginRegistry, PluginRegistry>();
builder.Services.AddScoped<IPluginService, PluginService>();
builder.Services.AddHostedService<PluginHostService>();

// ── Background metadata refresh ───────────────────────────────────────────────
// MetadataRefreshService is both an IHostedService (background timer) and
// IMetadataRefreshService (injectable for on-demand single-item refresh).
// Register as a singleton so both aliases share the same instance.
builder.Services.AddSingleton<MetadataRefreshService>();
builder.Services.AddSingleton<IMetadataRefreshService>(sp => sp.GetRequiredService<MetadataRefreshService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetadataRefreshService>());

// ── Authentication — JWT Bearer + API Key ─────────────────────────────────────
// Both schemes are registered. The default authorization policy (below) accepts
// either, so [Authorize] on any controller works with both JWT and X-API-Key.
var jwtSecret = builder.Configuration["Security:JwtSecret"]
    ?? throw new InvalidOperationException("Security:JwtSecret must be configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = "Chronicle",
            ValidateAudience = true,
            ValidAudience = "Chronicle",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });

// Default policy accepts either Bearer JWT or X-API-Key authentication.
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(
            JwtBearerDefaults.AuthenticationScheme,
            ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
});

// ── Controllers + Swagger ─────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Chronicle API", Version = "v1" });

    // JWT Bearer
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });

    // API Key (X-API-Key header)
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "API key for scrobblers. Supply via the X-API-Key header. Example: \"chr_live_…\"",
        Name = "X-API-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            Array.Empty<string>()
        }
    });
});

// ── CORS ──────────────────────────────────────────────────────────────────────
// Origin derived from ports.json — no URLs need to be manually configured.
builder.Services.AddCors(options =>
{
    options.AddPolicy("Dev", policy => policy
        .WithOrigins($"http://localhost:{portConfig.Web}")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

// ── Migrate on startup (skip for InMemory used in tests) ─────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
    if (db.Database.IsRelational())
        db.Database.Migrate();
    else
        db.Database.EnsureCreated();
}

// ── Middleware pipeline ───────────────────────────────────────────────────────
// Request logging must come before routing so it captures all requests.
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate =
        "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Chronicle API v1"));
    app.UseCors("Dev");
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Make Program accessible to integration tests
public partial class Program { }
