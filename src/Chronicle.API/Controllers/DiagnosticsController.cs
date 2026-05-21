using Microsoft.AspNetCore.Mvc;
using Chronicle.API.DTOs;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly IConfiguration _config;

    public DiagnosticsController(IConfiguration config)
    {
        _config = config;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var apiDir = AppContext.BaseDirectory;
        var repoRoot = FindRepoRoot(apiDir) ?? apiDir;
        var dbPath = GetDbPath();
        var logsPath = Path.Combine(apiDir, "logs");
        var (branch, commitHash) = GetGitInfo(repoRoot);
        var apiProjectPath = Path.Combine(repoRoot, "src", "Chronicle.API", "Chronicle.API.csproj");

        // Read ports from ports.json
        var portsFile = Path.Combine(repoRoot, "ports.json");
        int apiPort = 8080, webPort = 3000;
        if (System.IO.File.Exists(portsFile))
        {
            try
            {
                var ports = JsonSerializer.Deserialize<PortsConfig>(
                    System.IO.File.ReadAllText(portsFile),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (ports != null) { apiPort = ports.Api; webPort = ports.Web; }
            }
            catch { }
        }

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";
        if (version.Contains('+')) version = version[..version.IndexOf('+')];

        bool dbExists = System.IO.File.Exists(dbPath);
        long dbSizeBytes = dbExists ? new System.IO.FileInfo(dbPath).Length : 0;

        return Ok(ApiResponse<DiagnosticsDto>.Ok(new DiagnosticsDto(
            RepoRoot: repoRoot,
            ApiProjectPath: apiProjectPath,
            ApiDir: apiDir,
            DbPath: dbPath,
            DbExists: dbExists,
            DbSizeBytes: dbSizeBytes,
            LogsPath: logsPath,
            Branch: branch,
            CommitHash: commitHash,
            ApiUrl: $"http://localhost:{apiPort}",
            WebUrl: $"http://localhost:{webPort}",
            Version: version
        )));
    }

    private string GetDbPath()
    {
        var cs = _config.GetConnectionString("DefaultConnection") ?? "";
        var match = Regex.Match(cs, @"Data Source=([^;]+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var path = match.Groups[1].Value.Trim();
            if (Path.IsPathRooted(path))
                return path;

            // SQLite resolves relative Data Source paths against the process working directory
            // (Environment.CurrentDirectory), NOT AppContext.BaseDirectory (the bin/ folder).
            // Under `dotnet run` these differ: CWD is the project folder, BaseDirectory is
            // bin/Debug/net9.0/.  In a published deployment they are the same.
            return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
        }
        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "chronicle.db"));
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                System.IO.File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static (string branch, string hash) GetGitInfo(string repoRoot)
    {
        try
        {
            static string Run(string args, string cwd)
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory = cwd,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return output;
            }
            var branch = Run("rev-parse --abbrev-ref HEAD", repoRoot);
            var hash   = Run("rev-parse --short HEAD", repoRoot);
            return (branch, hash);
        }
        catch { return ("unknown", "unknown"); }
    }

    private record PortsConfig(int Api, int Web);
}

public record DiagnosticsDto(
    string RepoRoot,
    string ApiProjectPath,
    string ApiDir,
    string DbPath,
    bool DbExists,
    long DbSizeBytes,
    string LogsPath,
    string Branch,
    string CommitHash,
    string ApiUrl,
    string WebUrl,
    string Version
);
