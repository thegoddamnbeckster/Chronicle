# Chronicle Security

**Version:** 1.0  
**Last Updated:** 2026-01-12  
**Author:** Chronicle Contributors with Anthropic Claude

---

## Security Philosophy

Chronicle prioritizes user security and data privacy through:

1. **Defense in Depth** - Multiple layers of security
2. **Secure by Default** - Safe configuration out of the box
3. **Principle of Least Privilege** - Users/apps get minimum necessary access
4. **Encryption at Rest and in Transit** - Protect sensitive data
5. **Transparency** - Open source, auditable code

---

## Authentication

### User Authentication

**Password Requirements:**
- Minimum 12 characters (configurable)
- Must contain: uppercase, lowercase, number, special character
- No dictionary words or common passwords
- Password strength meter during registration

**Password Storage:**
```csharp
// NEVER store plaintext passwords
// Use BCrypt with cost factor 12
public class PasswordHasher
{
    public string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
    }
    
    public bool VerifyPassword(string password, string hash)
    {
        return BCrypt.Net.BCrypt.Verify(password, hash);
    }
}
```

**Database Storage:**
```sql
CREATE TABLE users (
    id INTEGER PRIMARY KEY,
    username TEXT NOT NULL UNIQUE,
    email TEXT NOT NULL UNIQUE,
    password_hash TEXT NOT NULL,  -- BCrypt hash, NEVER plaintext
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
```

**Key Points:**
- ✅ BCrypt with cost factor 12
- ✅ Salted automatically by BCrypt
- ✅ Computationally expensive (protects against brute force)
- ❌ NEVER plaintext
- ❌ NEVER MD5/SHA1
- ❌ NEVER base64 encoding (not encryption)

### Session Management

**Session keys** (web UI; replaced the earlier JWTs on 2026-10-07):

- Chronicle generates a random 256-bit key (`chr_sess_...`) at login. It is opaque: no claims,
  nothing to decode or forge.
- Only a SHA-256 hash of the key is kept, in a memory-only store. Nothing is written to disk, so
  **restarting the API ends every session**.
- The key is sent as `Authorization: Bearer ...` and checked on every request. The user's role
  and active state are read live on each request.
- One key per login; a user may have many sessions (cap 20; the least recently used is dropped).
- Expiry: idle timeout (`auth.session_idle_hours`, default 24) and an absolute lifetime
  (`auth.session_max_days`, default 30). Automatic polling does not count as activity.
- Ended by logout, password change/reset, deactivation, deletion, expiry or restart.
- Never logged: the key, the password. Rejected keys are logged by a short hash tag only.
- Session management endpoints require a real login; an API key cannot use them.

**Connection logging.** Logins (success and failure), registrations, logouts, session revocations
and every rejected session key or API key are logged under `Chronicle.Auth` with the caller's
address and user-agent. Repeated rejections from the same source for the same reason are
throttled to one line per minute so a stale browser tab cannot flood the log.

**Session features** (Settings → My Profile → Active Sessions; admins can see and end a user's
sessions from Manage Users):
- View active sessions (device, address, signed-in and last-active times)
- End an individual session
- "Sign out everywhere"
- Automatic cleanup of expired sessions (every 5 minutes)

**Sign-in throttling.** Failed sign-ins are counted per (address, username), per address, and per
username across all addresses, in memory (a restart clears them). A caller over a limit gets `429`
with `Retry-After` and is refused even with the right password until the oldest failures age out.
New accounts are limited per address per hour, and device pairing is limited the same way (pairing
starts per address per hour; unknown-code guesses per address per 15 minutes; at most 200 unexpired
codes). Every limit is an `app_settings` row and applies within about a minute:

| Setting | Default |
|---|---|
| `auth.login_max_failures_per_address_user` | 5 |
| `auth.login_max_failures_per_address` | 20 |
| `auth.login_max_failures_per_user` | 30 |
| `auth.login_window_minutes` | 15 |
| `auth.register_max_per_address_hour` | 10 |
| `auth.pairing_initiates_per_address_hour` | 10 |
| `auth.pairing_misses_per_address_15min` | 20 |
| `auth.session_idle_hours` / `auth.session_max_days` | 24 / 30 |

The per-username limit is deliberately the most generous: it is the one an attacker can use to lock
the real user out for a while. If that ever happens, restarting the API clears all counters.

Behind a reverse proxy, set `Security:TrustedProxies` (addresses or CIDR ranges, comma separated)
so only the proxy can supply `X-Forwarded-For`. Unset, any client can claim an address; Chronicle
then logs a warning at startup, and the audit log shows the real connection address next to any
forwarded one.

**Session cookie for images.** Login also sets `chronicle_session` (HttpOnly, SameSite=Strict, path
`/api/`) holding the same key. Its only job is to let `<img>` tags, which cannot send an
Authorization header, load `media/{id}/local-poster` and `plugins/{id}/icon`. The cookie is
accepted by those two read-only routes alone, never counts as activity, and ends with the session.
The icon route fetches the image named in the plugin's manifest through the same filtered fetcher as
the poster proxy, so a manifest cannot aim the server at its own network.

### Forgotten passwords

Three ways back in, all producing the same kind of credential: a **single-use reset token** (32 random bytes; only its
SHA-256 hash is stored; expires after `auth.reset_token_minutes`, default 60; a newer token replaces any earlier one).

1. **Email link** - Login page -> *Forgot password?* -> username or email address. Needs Settings -> Email to be set up. The
   reply is always the same whether or not the account exists or has an address, the email is sent after the reply (so
   timing does not give it away either), and requests are limited per address and per person (`auth.forgot_per_address_hour`
   5, `auth.forgot_per_identifier_hour` 3). The link is built from the **public address configured on the Email page**, never
   from the request's Host header (which a caller controls), and the token travels in the URL fragment so it never reaches
   a server log or a Referer header.
2. **Administrator-issued code** - Settings -> Users -> *Reset Code* (browser session, administrators only; an API key
   cannot). Shown once; the person enters it on *Forgot password* -> *I have a reset code*.
3. **Local recovery command** - `Chronicle.API --reset-admin-password <username>` prints a code and exits. It needs access
   to the machine and database file, which is the point: it is the way back when the only administrator is locked out and
   no email is set up. Docker: `docker compose exec api dotnet Chronicle.API.dll --reset-admin-password NAME`.

Using a token sets the new password, ends **every** session of that account, and does not sign anyone in. Wrong or reused
codes are all answered identically and counted per address (`auth.reset_misses_per_address_15min`, 10). Every step is in the
audit log (`AUTH password-reset ...`).

**Mail settings.** Settings -> Email (administrators, browser session). The mail password is stored as entered, like plugin
credentials, and guarded the other way: it is write-only through the API and `email.*` rows are hidden from the general
settings listing and cannot be written through it.

### API Key Authentication

**Scopes.** An API key used to carry its owner's full privileges. Each key now has a scope:

| Scope | For | May call |
|---|---|---|
| `full` | scripts that need everything their owner can do | anything the owner can, except manage keys, sessions and pairing (those need a login) |
| `device` | Kodi boxes and the Audiobookshelf scrobbler | scrobble, the Kodi scraper, `users/me`, reading lists, rating via `library/{id}`, resetting watch progress |
| `bridge` | the Audiobookshelf metadata bridge | search/read/create/refresh media items only |

A `device` or `bridge` key is never an administrator, even when its owner is, and a request outside
its scope gets `403` and an `AUTH api-key scope denied` log line. Keys created before scopes
existed stay `full` until changed (API Keys page, or `PUT /api/v1/tokens/{id}/scope`); keys paired
through the QR flow from now on are `device`. The rules live in code (`ApiKeyScopes`) rather than
the UI, because they are a security boundary.

For scrobblers and automation:

```sql
CREATE TABLE api_tokens (
    id INTEGER PRIMARY KEY,
    user_id INTEGER NOT NULL,
    token TEXT NOT NULL UNIQUE,  -- Securely generated
    name TEXT NOT NULL,  -- "Kodi Upstairs", "Python Script"
    permissions JSON NOT NULL,   -- ["scrobble", "read"]
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    last_used_at TIMESTAMP,
    expires_at TIMESTAMP,  -- NULL = never expires
    is_revoked BOOLEAN DEFAULT 0,
    
    FOREIGN KEY (user_id) REFERENCES users(id)
);
```

**Token Generation:**
```csharp
public string GenerateApiToken()
{
    // Cryptographically secure random token
    var bytes = new byte[32];
    using (var rng = RandomNumberGenerator.Create())
    {
        rng.GetBytes(bytes);
    }
    return Convert.ToBase64String(bytes);
}
```

**API Token Features:**
- User-friendly names ("Kodi Living Room")
- Granular permissions
- Optional expiration
- Usage tracking (last used)
- Easy revocation

---

## Authorization

### Role-Based Access Control (RBAC)

**User Roles:**

```sql
CREATE TABLE roles (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,  -- "admin", "user", "limited"
    permissions JSON NOT NULL
);

CREATE TABLE user_roles (
    user_id INTEGER NOT NULL,
    role_id INTEGER NOT NULL,
    
    PRIMARY KEY (user_id, role_id),
    FOREIGN KEY (user_id) REFERENCES users(id),
    FOREIGN KEY (role_id) REFERENCES roles(id)
);
```

**Default Roles:**

| Role | Permissions |
|------|-------------|
| **Admin** | Full system access, user management, settings |
| **User** | Full personal access, cannot manage others |
| **Limited** | Read-only access, cannot scrobble or modify |

**Permission Model:**
```json
{
  "scrobble": true,
  "read_own_history": true,
  "read_all_history": false,
  "modify_metadata": true,
  "manage_users": false,
  "view_admin_panel": false,
  "manage_plugins": false,
  "modify_settings": false
}
```

### Resource-Level Authorization

**Per-Resource Permissions:**

```csharp
[Authorize(Permission = "read_history")]
public async Task<IActionResult> GetHistory(int userId)
{
    // Check if current user can access this user's history
    if (!await _authService.CanAccessUserHistory(CurrentUser, userId))
    {
        return Forbid();
    }
    
    var history = await _historyService.GetHistoryAsync(userId);
    return Ok(history);
}
```

**Permission Checks:**
- Own resources: Always allowed (user can see own history)
- Family member resources: Parent/guardian can access children
- Friend resources: Only if explicitly shared
- Admin: Can access all (with audit logging)

---

## Encryption

### Sensitive Data Encryption

**Plugin API Keys & Secrets:**

```csharp
public class SecretEncryption
{
    private readonly byte[] _encryptionKey;

    public SecretEncryption()
    {
        // Derive key from machine-specific data + user secret
        _encryptionKey = DeriveKey();
    }

    public string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        aes.GenerateIV();
        
        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(
            Encoding.UTF8.GetBytes(plaintext), 0, plaintext.Length);
        
        // Return: IV + encrypted data as base64
        var combined = aes.IV.Concat(encrypted).ToArray();
        return Convert.ToBase64String(combined);
    }

    public string Decrypt(string ciphertext)
    {
        var data = Convert.FromBase64String(ciphertext);
        var iv = data.Take(16).ToArray();
        var encrypted = data.Skip(16).ToArray();
        
        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        aes.IV = iv;
        
        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        
        return Encoding.UTF8.GetString(decrypted);
    }

    private byte[] DeriveKey()
    {
        // Derive from:
        // 1. Machine ID
        // 2. User-provided master password (optional)
        // 3. Hardware-specific data
        
        var machineId = GetMachineId();
        var masterPassword = GetMasterPassword(); // From config
        
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password: masterPassword ?? machineId,
            salt: Encoding.UTF8.GetBytes("Chronicle.Encryption.Salt.v1"),
            iterations: 100000,
            hashAlgorithm: HashAlgorithmName.SHA256
        );
        
        return pbkdf2.GetBytes(32); // 256-bit key
    }
}
```

**Database Storage:**
```json
{
  "api_key": "encrypted:AES256:dGVzdCBkYXRhIGVuY3J5cHRlZA==",
  "language": "en-US"
}
```

**What Gets Encrypted:**
- ✅ Plugin API keys
- ✅ OAuth tokens
- ✅ SMTP passwords
- ✅ Webhook secrets
- ❌ User passwords (hashed with BCrypt, not encrypted)
- ❌ General settings (not sensitive)

### Database Encryption (Optional)

**SQLite Encryption:**
```bash
# Using SQLCipher
PRAGMA key = 'user-master-password';
```

**PostgreSQL Encryption:**
- Transparent Data Encryption (TDE)
- Column-level encryption
- Full disk encryption at OS level

**Backup Encryption:**
- All backups encrypted before storage
- Same encryption key as secrets
- Cannot restore without key

---

## Network Security

### HTTPS/TLS

**Reverse Proxy Setup (Recommended):**

```nginx
# Nginx configuration
server {
    listen 443 ssl http2;
    server_name chronicle.example.com;
    
    ssl_certificate /etc/letsencrypt/live/chronicle.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/chronicle.example.com/privkey.pem;
    
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;
    
    location / {
        proxy_pass http://localhost:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

**Let's Encrypt Integration:**
- Automatic certificate provisioning
- Auto-renewal
- Multiple domain support

**Internal Network:**
- HTTP acceptable for local network only
- HTTPS strongly recommended for any internet exposure

### Rate Limiting

**API Rate Limits:**

```sql
CREATE TABLE api_rate_limits (
    id INTEGER PRIMARY KEY,
    token_id INTEGER NOT NULL,
    endpoint TEXT NOT NULL,
    requests_count INTEGER DEFAULT 0,
    window_start TIMESTAMP NOT NULL,
    
    FOREIGN KEY (token_id) REFERENCES api_tokens(id),
    UNIQUE(token_id, endpoint, window_start)
);
```

**Default Limits:**
- **Authentication:** 5 attempts per 15 minutes per IP
- **API (authenticated):** 100 requests per minute
- **API (unauthenticated):** 10 requests per minute
- **Scrobble:** 10 per minute (prevent runaway scripts)

**Rate Limit Response:**
```json
{
  "error": "Rate limit exceeded",
  "retry_after": 45,
  "limit": 100,
  "remaining": 0,
  "reset_at": "2026-01-12T15:30:00Z"
}
```

**Headers:**
```
HTTP/1.1 429 Too Many Requests
X-RateLimit-Limit: 100
X-RateLimit-Remaining: 0
X-RateLimit-Reset: 1704729000
Retry-After: 45
```

### CORS Configuration

```csharp
services.AddCors(options =>
{
    options.AddPolicy("ChroniclePolicy", builder =>
    {
        builder
            .WithOrigins(Configuration.GetSection("AllowedOrigins").Get<string[]>())
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});
```

**Default:** Same-origin only  
**Configurable:** Allow specific domains for API access

---

## Input Validation

### SQL Injection Prevention

**Always Use Parameterized Queries:**

```csharp
// ✅ CORRECT - Parameterized
var user = await _db.QueryFirstOrDefaultAsync<User>(
    "SELECT * FROM users WHERE username = @Username",
    new { Username = username }
);

// ❌ WRONG - String concatenation (SQL injection vulnerable)
var user = await _db.QueryFirstOrDefaultAsync<User>(
    $"SELECT * FROM users WHERE username = '{username}'"
);
```

**Entity Framework Core:**
```csharp
// Automatically parameterized
var user = await _context.Users
    .Where(u => u.Username == username)
    .FirstOrDefaultAsync();
```

### XSS Prevention

**Output Encoding:**
```csharp
// React automatically escapes
<div>{user.username}</div>  // Safe

// Manual encoding when needed
var encoded = WebUtility.HtmlEncode(userInput);
```

**Content Security Policy (CSP):**
```
Content-Security-Policy: 
    default-src 'self'; 
    script-src 'self' 'unsafe-inline'; 
    style-src 'self' 'unsafe-inline'; 
    img-src 'self' data: https:;
```

### CSRF Protection

**Token-Based Protection:**
```csharp
[ValidateAntiForgeryToken]
public async Task<IActionResult> UpdateSettings(SettingsModel model)
{
    // CSRF token validated automatically
    await _settingsService.UpdateAsync(model);
    return Ok();
}
```

**SameSite Cookies:**
```csharp
services.AddAuthentication()
    .AddCookie(options =>
    {
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });
```

---

## Security Headers

**Default Security Headers:**

```
Strict-Transport-Security: max-age=31536000; includeSubDomains
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
X-XSS-Protection: 1; mode=block
Referrer-Policy: strict-origin-when-cross-origin
Permissions-Policy: geolocation=(), microphone=(), camera=()
```

**Implementation:**
```csharp
app.Use(async (context, next) =>
{
    context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Add("X-Frame-Options", "DENY");
    context.Response.Headers.Add("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Add("Referrer-Policy", "strict-origin-when-cross-origin");
    
    await next();
});
```

---

## Audit Logging

**Security Events to Log:**

```sql
CREATE TABLE audit_log (
    id INTEGER PRIMARY KEY,
    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    user_id INTEGER,
    event_type TEXT NOT NULL,  -- "login", "failed_login", "password_change"
    ip_address TEXT,
    user_agent TEXT,
    details JSON,
    severity TEXT DEFAULT 'info'  -- "info", "warning", "critical"
);

CREATE INDEX idx_audit_log_timestamp ON audit_log(timestamp DESC);
CREATE INDEX idx_audit_log_user_id ON audit_log(user_id);
CREATE INDEX idx_audit_log_event_type ON audit_log(event_type);
```

**Logged Events:**
- ✅ Successful login
- ✅ Failed login attempts
- ✅ Password changes
- ✅ User creation/deletion
- ✅ Permission changes
- ✅ Settings modifications
- ✅ API token creation/revocation
- ✅ Plugin installation/removal
- ✅ Database backups

**Example Entry:**
```json
{
  "timestamp": "2026-01-12T15:30:00Z",
  "user_id": 5,
  "event_type": "failed_login",
  "ip_address": "192.168.1.100",
  "user_agent": "Mozilla/5.0...",
  "details": {
    "username": "jsmith",
    "reason": "invalid_password",
    "attempt_number": 3
  },
  "severity": "warning"
}
```

**Audit Log UI:**
- View recent security events
- Filter by user/event type
- Export for analysis
- Alert on suspicious activity

**Retention:**
- Keep all audit logs for 90 days minimum
- Critical events kept for 1 year
- Configurable retention policy

---

## Two-Factor Authentication (2FA)

### TOTP (Time-Based One-Time Password)

**Phase 2 Feature:**

```sql
CREATE TABLE user_2fa (
    user_id INTEGER PRIMARY KEY,
    secret TEXT NOT NULL,  -- Encrypted TOTP secret
    backup_codes JSON NOT NULL,  -- Array of one-time backup codes
    enabled BOOLEAN DEFAULT 1,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (user_id) REFERENCES users(id)
);
```

**Setup Flow:**
1. User enables 2FA in settings
2. Generate TOTP secret
3. Display QR code (for Google Authenticator, Authy, etc.)
4. User scans QR code
5. User enters verification code
6. System validates code
7. Generate backup codes
8. 2FA enabled

**Login Flow with 2FA:**
1. Username + password
2. If valid, prompt for 2FA code
3. Validate TOTP code
4. Grant access

**Backup Codes:**
- 10 one-time use codes
- Used if TOTP device lost
- Each code can only be used once
- Regenerate after use

---

## Plugin Security

### Plugin Sandboxing (Future)

**Isolated Execution:**
- Plugins run in restricted context
- Limited file system access
- Cannot access other plugins' data
- Network access optional (per-plugin)

**Permission Model:**
```json
{
  "plugin_name": "tmdb-scraper",
  "permissions": [
    "network.http",
    "filesystem.read:/data/cache",
    "database.read:media_items"
  ]
}
```

### Plugin installation and file integrity (built)

* **Allowlist.** A plugin installs only if its id is in the catalog (the hosted `plugins.json`, its last good copy, or the built-in list), or an administrator has set
  the app setting `plugins.allow_unlisted` to `true`. The manual `POST /api/v1/plugins` route used to take any DLL path on
  the server; it now accepts only a DLL inside the `plugins/` folder, with a `manifest.json` naming an allowed id, and the
  check happens before any plugin code is loaded. A new folder that merely appears under `plugins/` at start is ignored
  (and logged) unless its id is allowed.
* **File hash.** One SHA-256 over every DLL under the plugin's folder plus `manifest.json` is recorded at install or
  catalog update (`plugins.FilesSha256`; the previous value is kept). It is checked before every load (start, enable,
  reload). A mismatch means the plugin is **not loaded**, `IntegrityBlockedAt` is set, administrators get a
  `plugin.integrity` notification, and the Plugins page offers "Approve changed files". A plugin from before hashing has
  its hash recorded on first load. An update through Chronicle records the new hash itself and shows "Files changed".
* **Developers** run `Chronicle.API --accept-plugin-changes` after rebuilding (RunTestEnvironment.ps1 does).
* **Not built:** author signing (below) and sandboxing. The hash proves the files are the ones Chronicle installed, not
  that the original author is trustworthy; that is what the allowlist is for.
* Writing a plugin: see `docs/PLUGIN_DEVELOPMENT_GUIDE.md`.

### Plugin Verification

**Code Signing (Future):**
- Verify plugin author
- Detect tampering
- Trust model (trusted developers)

**Manual Review:**
- Community-reviewed plugins
- Rating system
- Report malicious plugins

---

## Secrets Management

### Configuration Secrets

**Environment Variables:**
```bash
# .env file (not committed to git)
DATABASE_PASSWORD=secure_password
ENCRYPTION_KEY=32_byte_key
JWT_SECRET=jwt_signing_key
SMTP_PASSWORD=email_password
```

**Loading:**
```csharp
var dbPassword = Environment.GetEnvironmentVariable("DATABASE_PASSWORD");
```

**Docker Secrets:**
```yaml
# docker-compose.yml
services:
  chronicle:
    secrets:
      - db_password
      - encryption_key

secrets:
  db_password:
    file: ./secrets/db_password.txt
  encryption_key:
    file: ./secrets/encryption_key.txt
```

---

## Security Best Practices

### For Users

**DO:**
- ✅ Use strong, unique passwords
- ✅ Enable 2FA when available
- ✅ Keep Chronicle updated
- ✅ Use HTTPS for external access
- ✅ Regular backups
- ✅ Monitor audit logs
- ✅ Revoke unused API tokens
- ✅ Use reverse proxy (Nginx, Caddy)

**DON'T:**
- ❌ Reuse passwords
- ❌ Expose directly to internet without HTTPS
- ❌ Share API tokens publicly
- ❌ Ignore update notifications
- ❌ Disable security features

### For Developers

**DO:**
- ✅ Always use parameterized queries
- ✅ Validate and sanitize all input
- ✅ Encrypt sensitive data
- ✅ Log security events
- ✅ Follow principle of least privilege
- ✅ Regular security audits
- ✅ Dependency updates

**DON'T:**
- ❌ Store plaintext passwords/secrets
- ❌ Trust user input
- ❌ Use deprecated crypto algorithms
- ❌ Commit secrets to git
- ❌ Disable security checks in production

---

## Vulnerability Reporting

**Security Contact:**
- [GitHub Security Advisories](https://github.com/thegoddamnbeckster/Chronicle/security/advisories)

**Responsible Disclosure:**
1. Report vulnerability privately
2. Wait for confirmation
3. Allow time for patch (typically 90 days)
4. Coordinate public disclosure

**Security Updates:**
- Critical patches released immediately
- Security advisories published
- CVE numbers assigned for serious issues

---

## Compliance

### GDPR Considerations

**User Rights:**
- Right to access data
- Right to export data
- Right to delete data ("right to be forgotten")
- Right to data portability

**Implementation:**
- Export all user data (JSON/CSV)
- Delete all user data on request
- Audit log of data access
- Privacy policy

### Data Retention

**Configurable Policies:**
- Scrobble history: Keep forever (default) or X years
- Audit logs: 90 days minimum, 1 year recommended
- Session tokens: Auto-expire
- API rate limit data: 24 hours

---

## Security Checklist

### Deployment Checklist

- [ ] Change default admin password
- [ ] Enable HTTPS
- [ ] Configure firewall
- [ ] Set up backups
- [ ] Enable audit logging
- [ ] Configure rate limiting
- [ ] Review default permissions
- [ ] Set up monitoring/alerts
- [ ] Test backup restore
- [ ] Review security headers
- [ ] Update dependencies
- [ ] Disable debug mode

### Regular Maintenance

- [ ] Review audit logs weekly
- [ ] Update Chronicle monthly (or when security patches released)
- [ ] Review active sessions monthly
- [ ] Review API tokens quarterly
- [ ] Test backup restores quarterly
- [ ] Review user permissions quarterly
- [ ] Update dependencies quarterly
- [ ] Security audit annually

---

**Document Status:** Complete  
**Implementation Priority:** Phase 1 (Core security), Phase 2 (2FA, advanced features)
