# Feature Design: Malicious Plugin Detection

**Status:** Design/Planning
**Target:** Phase 3
**Goal:** Prevent untrusted or malicious plugins from compromising the Chronicle installation. Provide layered defences at install time, load time, and runtime.

---

## Threat Model

Chronicle loads arbitrary .NET assemblies from disk. A malicious plugin could:
- **Data exfiltration** — send Chronicle database contents to an external server
- **Credential theft** — read stored API tokens from plugin settings
- **Filesystem access** — read/write/delete arbitrary files on the host
- **Code execution** — spawn child processes or load additional unverified assemblies
- **Denial of service** — consume CPU/memory/network indefinitely
- **Supply chain attack** — a legitimate plugin is compromised in its distribution channel

---

## Defence Layers

### Layer 1 — Plugin Signing (Trust Verification)

**Mechanism:** Each plugin DLL and its `manifest.json` are signed with the author's Ed25519 private key. Chronicle verifies the signature at install and load time using the author's public key, which is distributed via the Chronicle plugin registry.

**Implementation:**
```csharp
public class PluginSignatureVerifier
{
    /// <summary>
    /// Verifies the SHA-256 hash of dllBytes against the .sig file
    /// using the author's Ed25519 public key from the plugin registry.
    /// </summary>
    public static bool Verify(byte[] dllBytes, byte[] signature, byte[] publicKey);
}
```

Plugin manifest gains a `"signature"` field:
```json
{
  "plugin_id": "trakt",
  "signature": "base64-encoded-ed25519-sig-of-sha256(dll-bytes)",
  "public_key": "base64-encoded-ed25519-public-key"
}
```

**Plugin registry** (`https://chronicle-plugins.example.com/registry.json`):
- Chronicle-maintained list of verified plugins and their trusted public keys
- Chronicle checks the registry when installing a plugin
- Plugins not in the registry are flagged as "unverified" and require admin confirmation

**Verification at install:**
1. Download/locate the DLL and `manifest.json`
2. Compute SHA-256 of the DLL bytes
3. Verify signature against the hash using the public key from the manifest
4. Check public key against the registry
5. If any step fails → block install, show warning

**Verification at startup:**
- Re-verify all enabled plugins before loading them
- Prevents tampering between install and next start

---

### Layer 2 — .NET Runtime Isolation (AssemblyLoadContext Sandbox)

Chronicle already uses `AssemblyLoadContext` to isolate plugin assemblies. Extend it with restrictions:

**Restrict outbound network calls (optional, configurable):**
```csharp
public class SandboxedPluginLoadContext : PluginLoadContext
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Block plugins from loading System.Net.Sockets directly
        // (they still can via HttpClient which is controlled by the host)
        if (assemblyName.Name is "System.Net.Sockets" or "System.Net.Http")
            return null; // use shared host assembly
        return base.Load(assemblyName);
    }
}
```

**HttpClient factory control:**
Plugins receive `IHttpClientFactory` via their `Configure()` call (future enhancement). This lets Chronicle apply policies:
- Per-plugin `HttpClient` with a named handler
- Polly policies: timeout (max 30s per request), circuit breaker
- Optional: allowlist/blocklist of domains per plugin category

---

### Layer 3 — Static Analysis at Install Time

Before registering a plugin, Chronicle performs a lightweight static analysis of the DLL using `System.Reflection`:

```csharp
public class PluginStaticAnalyser
{
    public PluginRiskReport Analyse(string dllPath)
    {
        var assembly = Assembly.LoadFrom(dllPath);
        var warnings = new List<string>();

        // Flag suspicious type references
        var suspiciousTypes = new[]
        {
            "System.IO.File",              // filesystem access
            "System.IO.Directory",         // directory traversal
            "System.Diagnostics.Process",  // spawning child processes
            "System.Reflection.Emit",      // dynamic code generation
            "System.Runtime.InteropServices.Marshal", // native memory / P/Invoke
            "Microsoft.CSharp.RuntimeBinder", // dynamic dispatch (harder to analyse)
        };

        foreach (var type in assembly.GetReferencedAssemblies())
            foreach (var suspect in suspiciousTypes)
                if (type.FullName?.Contains(suspect) == true)
                    warnings.Add($"References {suspect}");

        return new PluginRiskReport(warnings);
    }
}
```

The risk report is shown in the UI when installing an unverified plugin. If the plugin is in the registry and signed, the warning is informational only.

---

### Layer 4 — Runtime Monitoring

**Resource limits** (via `System.Threading.Timer` + memory inspection):
- CPU watchdog: if a plugin's import/search call hasn't returned in 60 seconds, cancel the `CancellationToken` and log a warning
- Memory baseline: record host process `GC.GetTotalAllocatedBytes()` before and after each plugin call; log if delta > 500 MB

**Network monitoring:**
- Register a custom `DelegatingHandler` in the host's `HttpClientFactory`
- Log all outbound HTTP requests made via Chronicle's factory (plugin ID, URL, response code, bytes transferred)
- Admin can audit these logs via `GET /api/v1/admin/plugin-network-log`

**Exception monitoring:**
- All plugin calls wrapped in try/catch
- Unhandled exceptions from a plugin increment a failure counter
- After N consecutive failures → auto-disable the plugin and alert admin

---

### Layer 5 — Permissions Manifest

Extend `manifest.json` with a `"permissions"` array that declares what the plugin needs:

```json
{
  "plugin_id": "trakt",
  "permissions": [
    "network:trakt.tv",
    "network:api.trakt.tv"
  ]
}
```

Permission categories:
| Permission | Description |
|---|---|
| `network:*` | Outbound HTTP to any host |
| `network:{hostname}` | Outbound HTTP to a specific hostname |
| `filesystem:read` | Read files from the filesystem |
| `filesystem:write` | Write files to the filesystem |
| `process:spawn` | Start child processes |

At install time, Chronicle displays the permission list to the admin. At runtime, a future `PermissionEnforcingHandler` can deny requests to undeclared hosts.

---

### Layer 6 — User-Facing Trust UI

**Plugin install dialog shows:**
1. Plugin name, author, description, version
2. ✅ Signed / ⚠️ Unverified badge
3. Registry status: "Verified by Chronicle" / "Community plugin" / "Unknown"
4. Static analysis warnings (if any)
5. Permissions requested
6. "Install anyway" requires admin confirmation for unverified plugins

**Plugin list view shows:**
- Last-verified timestamp
- Failure counter (resets on successful call)
- Network activity summary (requests/24h)

---

## Threat Response Matrix

| Threat | Mitigations |
|---|---|
| Tampered DLL | Signature verification at install + startup (Layer 1) |
| Exfiltration | Network monitoring + domain allowlist (Layers 2, 4) |
| Credential access | Plugins only receive their own settings via `Configure()` |
| Filesystem traversal | Static analysis flags `System.IO` usage (Layer 3) |
| Process spawning | Static analysis flags `Process` usage (Layer 3) |
| Supply chain | Registry + signing (Layer 1) |
| DoS (CPU/memory) | Resource watchdog (Layer 4) |
| Dynamic code gen | Static analysis flags `Reflection.Emit` (Layer 3) |

---

## Implementation Order

1. Define `PluginRiskReport` + `PluginStaticAnalyser` (quick win — runs at install)
2. Add `permissions` field to `PluginManifest` model
3. Add `is_verified`, `signature`, `risk_warnings_json` fields to `plugins` table (migration)
4. Implement `PluginSignatureVerifier` using `System.Security.Cryptography`
5. Re-verify all plugins at `PluginHostService` startup
6. Add resource watchdog to `PluginRegistry.LoadPluginAsync`
7. Add network monitoring `DelegatingHandler`
8. Frontend: trust badges in plugin install dialog and plugin list
9. (Optional Phase 4) Plugin registry service
10. (Optional Phase 4) Domain-level HTTP allowlist enforcement

---

## Key Design Decision: No Full Sandboxing

Full OS-level sandboxing (AppContainer, seccomp, etc.) is complex to implement correctly and would break legitimate plugin functionality. Chronicle's approach is:

- **Make the rules clear** via the permissions manifest
- **Make violations visible** via monitoring and logging
- **Trust but verify** via signing + static analysis
- **Degrade gracefully** via auto-disable on repeated failures

This is the same approach used by VS Code extensions and JetBrains plugins — accepted industry practice for plugin ecosystems.
