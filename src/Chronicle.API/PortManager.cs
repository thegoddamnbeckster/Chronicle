using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace Chronicle.API;

public static class PortManager
{
    /// <summary>Chronicle's own established defaults (see CLAUDE.md) -- used whenever nothing
    /// more specific overrides them.</summary>
    private const int DefaultApiPort = 7979;
    private const int DefaultWebPort = 8888;

    /// <summary>
    /// Resolves the API/web ports, in order of precedence:
    ///   1. CHRONICLE_API_PORT / CHRONICLE_WEB_PORT environment variables -- the standard,
    ///      install-friendly override for a Docker deployment or a Windows Service (an
    ///      installer can set these directly; there's no shell profile to source).
    ///   2. ports.json, searched by walking up from <paramref name="searchRoot"/> -- the dev
    ///      workflow's own mechanism (repo-root ports.json, shared with vite.config.ts), which
    ///      also works unmodified for a packaged install that simply drops/edits ports.json
    ///      next to the published exe.
    ///   3. Chronicle's documented defaults (7979 / 8888) -- NOT the previous silent fallback of
    ///      8080/3000, which matched nothing else in the app or its docs.
    /// </summary>
    public static PortConfig LoadConfig(string searchRoot)
    {
        var (fileApi, fileWeb, source) = ReadPortsFile(searchRoot);

        var api = ReadPortEnvVar("CHRONICLE_API_PORT") ?? fileApi ?? DefaultApiPort;
        var web = ReadPortEnvVar("CHRONICLE_WEB_PORT") ?? fileWeb ?? DefaultWebPort;

        Console.WriteLine($"[Chronicle] Ports resolved: api={api} web={web} (source: {source}, " +
            "env vars CHRONICLE_API_PORT/CHRONICLE_WEB_PORT take precedence over both)");
        return new PortConfig(api, web);
    }

    private static int? ReadPortEnvVar(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (int.TryParse(raw, out var port) && port is > 0 and <= 65535) return port;
        Console.WriteLine($"[Chronicle] Warning: {name}='{raw}' is not a valid port — ignoring it");
        return null;
    }

    private static (int? Api, int? Web, string Source) ReadPortsFile(string searchRoot)
    {
        var portsFile = FindPortsFile(searchRoot);
        if (portsFile == null) return (null, null, "no ports.json found, using defaults");

        try
        {
            using var stream = File.OpenRead(portsFile);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            int? api = root.TryGetProperty("api", out var apiProp) ? apiProp.GetInt32() : null;
            int? web = root.TryGetProperty("web", out var webProp) ? webProp.GetInt32() : null;
            return (api, web, portsFile);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Chronicle] Warning: could not parse {portsFile} ({ex.Message}) — ignoring it");
            return (null, null, "unparseable ports.json, using defaults");
        }
    }

    /// <summary>
    /// Checks whether <paramref name="port"/> is already bound. If so, prints a prominent
    /// error showing the occupying process and a suggested free port, then exits.
    /// </summary>
    public static void CheckPort(int port)
    {
        if (!IsPortInUse(port)) return;

        var occupant  = GetProcessUsingPort(port);
        var suggested = FindNextFreePort(port);

        const int inner = 56;

        string Row(string text)
        {
            if (text.Length > inner) text = text[..inner];
            return $"  ║ {text.PadRight(inner)} ║";
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  ╔{new string('═', inner + 2)}╗");
        Console.Error.WriteLine(Row(""));
        Console.Error.WriteLine(Row("  PORT CONFLICT — Chronicle cannot start"));
        Console.Error.WriteLine(Row($"  Port {port} is already in use."));
        if (occupant != null)
            Console.Error.WriteLine(Row($"  In use by: {occupant}"));
        if (suggested > 0)
            Console.Error.WriteLine(Row($"  Nearest free port: {suggested}  (update ports.json)"));
        Console.Error.WriteLine(Row(""));
        Console.Error.WriteLine($"  ╚{new string('═', inner + 2)}╝");
        Console.Error.WriteLine();
        Console.ResetColor();

        Environment.Exit(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsPortInUse(int port) =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(ep => ep.Port == port);

    private static int FindNextFreePort(int startPort)
    {
        for (int p = startPort + 1; p <= startPort + 20; p++)
            if (!IsPortInUse(p)) return p;
        return -1;
    }

    private static string? GetProcessUsingPort(int port)
    {
        if (OperatingSystem.IsWindows())
            return GetProcessUsingPortWindows(port);

        if (OperatingSystem.IsLinux())
            return GetProcessUsingPortLinux(port);

        return null;    // macOS / unknown — skip identification
    }

    private static string? GetProcessUsingPortWindows(int port)
    {
        // netstat -ano lists all TCP listeners with PIDs on Windows
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains($":{port} ") && !line.Contains($":{port}\t")) continue;
                if (!line.Contains("LISTENING")) continue;

                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!int.TryParse(parts.Last(), out int pid)) continue;

                try   { return $"{Process.GetProcessById(pid).ProcessName} (PID {pid})"; }
                catch { return $"PID {pid}"; }
            }
        }
        catch { /* netstat unavailable */ }

        return null;
    }

    private static string? GetProcessUsingPortLinux(int port)
    {
        // ss -tlnp lists TCP listeners with process info on Linux
        try
        {
            var psi = new ProcessStartInfo("ss", $"-tlnp sport = :{port}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            // ss output example:
            //   State  Recv-Q Send-Q Local Address:Port   ...  users:(("dotnet",pid=12345,fd=9))
            foreach (var line in output.Split('\n'))
            {
                // Extract pid=NNNN from the users:((...)) section
                var pidMatch = System.Text.RegularExpressions.Regex.Match(line, @"pid=(\d+)");
                if (!pidMatch.Success) continue;

                if (!int.TryParse(pidMatch.Groups[1].Value, out int pid)) continue;

                // Also grab process name from users:(("name",...))
                var nameMatch = System.Text.RegularExpressions.Regex.Match(line, @"""([^""]+)""");
                var name = nameMatch.Success ? nameMatch.Groups[1].Value : "unknown";

                return $"{name} (PID {pid})";
            }
        }
        catch { /* ss unavailable */ }

        return null;
    }

    private static string? FindPortsFile(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "ports.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        return null;
    }
}

public record PortConfig(int Api, int Web);
