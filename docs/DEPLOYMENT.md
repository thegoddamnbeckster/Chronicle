# Chronicle Deployment Guide

**Version:** 1.0  
**Last Updated:** 2026-01-12  
**Author:** Chronicle Contributors with Anthropic Claude

---

## System Requirements

### Minimum Requirements
- **CPU:** 2 cores
- **RAM:** 2GB
- **Storage:** 10GB free space
- **OS:** Windows 10+, Linux (any modern distro), macOS 10.15+

### Recommended Requirements
- **CPU:** 4 cores
- **RAM:** 4GB
- **Storage:** 50GB free space (for backups, logs)
- **OS:** Windows 11, Ubuntu 22.04+, Debian 11+, macOS 12+

### Database Requirements
- **SQLite:** Included, no additional requirements
- **PostgreSQL:** Version 13+ (for production deployments)

---

## Installation Methods

### Windows Installation

**Method 1: Standalone Executable (Recommended)**

1. **Download:**
   ```
   https://github.com/thegoddamnbeckster/Chronicle/releases/latest
   ```

2. **Extract:**
   ```
   Extract to: C:\Chronicle\
   ```

3. **Run:**
   ```
   Double-click Chronicle.exe
   or
   cd C:\Chronicle
   .\Chronicle.exe
   ```

4. **Access:**
   ```
   Open browser: http://localhost:8080
   ```

5. **First-Time Setup:**
   - Create admin account
   - Configure basic settings
   - Add first media type

**Method 2: Windows Service**

```powershell
# Install as Windows Service
sc create Chronicle binPath="C:\Chronicle\Chronicle.exe" start=auto

# Start service
sc start Chronicle

# Stop service
sc stop Chronicle

# Remove service
sc delete Chronicle
```

**Firewall Configuration:**
```powershell
# Allow Chronicle through Windows Firewall
New-NetFirewallRule -DisplayName "Chronicle" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow
```

---

### Linux Installation

**Method 1: Binary Download**

```bash
# Download
wget https://github.com/thegoddamnbeckster/Chronicle/releases/latest/download/chronicle-linux-x64.tar.gz

# Extract
tar -xzf chronicle-v1.0.0-linux-x64.tar.gz

# Move to /opt
sudo mv chronicle /opt/chronicle

# Create data directory
sudo mkdir -p /opt/chronicle/data

# Set permissions
sudo chown -R $USER:$USER /opt/chronicle

# Run
cd /opt/chronicle
./Chronicle

# Access
http://localhost:8080
```

**Method 2: systemd Service**

```bash
# Create service file
sudo nano /etc/systemd/system/chronicle.service
```

```ini
[Unit]
Description=Chronicle Media Tracker
After=network.target

[Service]
Type=simple
User=chronicle
Group=chronicle
WorkingDirectory=/opt/chronicle
ExecStart=/opt/chronicle/Chronicle
Restart=on-failure
RestartSec=10

# Environment
Environment="ASPNETCORE_ENVIRONMENT=Production"
Environment="ASPNETCORE_URLS=http://0.0.0.0:8080"

# Logging
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
```

```bash
# Create chronicle user
sudo useradd -r -s /bin/false chronicle
sudo chown -R chronicle:chronicle /opt/chronicle

# Reload systemd
sudo systemctl daemon-reload

# Enable and start
sudo systemctl enable chronicle
sudo systemctl start chronicle

# Check status
sudo systemctl status chronicle

# View logs
sudo journalctl -u chronicle -f
```

---

### Docker Installation (Recommended)

**docker-compose.yml:**

```yaml
version: '3.8'

services:
  chronicle:
    image: ghcr.io/thegoddamnbeckster/chronicle:latest
    container_name: chronicle
    restart: unless-stopped
    ports:
      - "8080:8080"
    volumes:
      - ./data:/data
      - ./logs:/logs
      - ./backups:/backups
      - ./plugins:/plugins
    environment:
      - TZ=America/Edmonton
      - CHRONICLE_DATABASE=sqlite
      - CHRONICLE_LOGLEVEL=INFO
    networks:
      - chronicle-network

  # Optional: PostgreSQL
  postgres:
    image: postgres:15
    container_name: chronicle-db
    restart: unless-stopped
    environment:
      - POSTGRES_DB=chronicle
      - POSTGRES_USER=chronicle
      - POSTGRES_PASSWORD=secure_password
    volumes:
      - ./postgres-data:/var/lib/postgresql/data
    networks:
      - chronicle-network

networks:
  chronicle-network:
    driver: bridge

volumes:
  data:
  logs:
  backups:
  plugins:
```

**Start Container:**
```bash
# Start
docker-compose up -d

# View logs
docker-compose logs -f

# Stop
docker-compose down

# Update
docker-compose pull
docker-compose up -d
```

**Docker Run (without compose):**
```bash
docker run -d \
  --name chronicle \
  -p 8080:8080 \
  -v $(pwd)/data:/data \
  -v $(pwd)/logs:/logs \
  -v $(pwd)/backups:/backups \
  -e TZ=America/Edmonton \
  ghcr.io/thegoddamnbeckster/chronicle:latest
```

---

### macOS Installation

**Method 1: Binary Download**

```bash
# Download
curl -L https://github.com/thegoddamnbeckster/Chronicle/releases/latest/download/chronicle-macos-x64.tar.gz -o chronicle.tar.gz

# Extract
tar -xzf chronicle.tar.gz

# Move to Applications
mv chronicle /Applications/Chronicle

# Run
cd /Applications/Chronicle
./Chronicle

# Access
http://localhost:8080
```

**Method 2: Homebrew**

Not currently available.

# Run as service
brew services start chronicle
```

---

## Configuration

### Configuration File

**Location:**
- Windows: `C:\Chronicle\data\config.json`
- Linux: `/opt/chronicle/data/config.json`
- Docker: `/data/config.json`

**Example config.json:**

```json
{
  "server": {
    "port": 8080,
    "bind_address": "0.0.0.0",
    "base_url": "http://localhost:8080"
  },
  "database": {
    "type": "sqlite",
    "connection_string": "Data Source=/data/chronicle.db",
    "auto_backup": true,
    "backup_retention_days": 30
  },
  "logging": {
    "directory": "/logs",
    "level": "INFO",
    "max_file_size_mb": 25,
    "max_backup_files": 10,
    "levels": {
      "Chronicle": "INFO",
      "Plugins": "WARN"
    }
  },
  "security": {
    "jwt_secret": "CHANGE_THIS_TO_RANDOM_STRING",
    "jwt_expiration_hours": 24,
    "password_min_length": 12,
    "enable_rate_limiting": true,
    "max_login_attempts": 5
  },
  "features": {
    "enable_registration": true,
    "enable_federation": false,
    "enable_webhooks": true
  },
  "updates": {
    "check_for_updates": true,
    "auto_update": false,
    "update_channel": "stable"
  }
}
```

### Environment Variables

**Priority:** Environment variables override config.json

```bash
# Server
CHRONICLE_PORT=8080
CHRONICLE_BIND_ADDRESS=0.0.0.0
CHRONICLE_BASE_URL=https://chronicle.example.com

# Database
CHRONICLE_DATABASE_TYPE=sqlite
CHRONICLE_DATABASE_CONNECTION="Data Source=/data/chronicle.db"

# Logging
CHRONICLE_LOG_LEVEL=INFO
CHRONICLE_LOG_DIRECTORY=/logs

# Security
CHRONICLE_JWT_SECRET=your_secure_secret_here
CHRONICLE_ENABLE_RATE_LIMITING=true

# Features
CHRONICLE_ENABLE_REGISTRATION=true
CHRONICLE_ENABLE_FEDERATION=false
```

---

## Reverse Proxy Setup

### Nginx

**Installation:**
```bash
sudo apt install nginx
```

**Configuration:**
```nginx
# /etc/nginx/sites-available/chronicle

server {
    listen 80;
    server_name chronicle.example.com;
    
    # Redirect to HTTPS
    return 301 https://$server_name$request_uri;
}

server {
    listen 443 ssl http2;
    server_name chronicle.example.com;
    
    # SSL Configuration
    ssl_certificate /etc/letsencrypt/live/chronicle.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/chronicle.example.com/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;
    
    # Security Headers
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;
    add_header X-Frame-Options "DENY" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-XSS-Protection "1; mode=block" always;
    
    # Proxy Settings
    location / {
        proxy_pass http://localhost:7979;   # or http://<chronicle-container-name>:7979 on a shared Docker network
        proxy_http_version 1.1;
        
        # Headers. This nginx is the edge (nothing in front of it), so it OVERWRITES
        # X-Forwarded-For with the address that really connected to it. Do not use
        # $proxy_add_x_forwarded_for here: that appends to whatever the client sent, and a client
        # could prepend a fake address. If another proxy sits in front of this one, trust that one
        # instead (see "Telling Chronicle which proxy to trust").
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header X-Forwarded-Port $server_port;
        
        # WebSocket support
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        
        # Timeouts
        proxy_connect_timeout 60s;
        proxy_send_timeout 60s;
        proxy_read_timeout 60s;
    }
    
    # Increase upload size for large files
    client_max_body_size 100M;
}
```

**Enable Site:**
```bash
sudo ln -s /etc/nginx/sites-available/chronicle /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl reload nginx
```

**Let's Encrypt SSL:**
```bash
# Install certbot
sudo apt install certbot python3-certbot-nginx

# Get certificate
sudo certbot --nginx -d chronicle.example.com

# Auto-renewal
sudo certbot renew --dry-run
```

### Telling Chronicle which proxy to trust

Chronicle writes the caller's address to its security log (`AUTH ...` lines) and uses it to
rate-limit failed logins. Behind a proxy that address comes from `X-Forwarded-For`, and by default
Chronicle believes that header from ANY sender - so a client could fake its address. Setting
`Security:TrustedProxies` makes Chronicle believe the header only when it arrives from your proxy.

The value is whatever address (or network) your proxy connects to Chronicle FROM. That depends on
your setup, so it is never hard-coded: discover it, then put it in `.env`.

1. **Fix the proxy headers first** (the nginx block above) and confirm the log shows real client
   addresses: make one failed login, then `docker compose logs api | grep "AUTH login FAILED"`.
   At this point the address is correct only because every sender is still trusted.
2. **Find the proxy's address.**
   - Proxy and Chronicle in Docker on a shared network - use that network's range, which survives
     the proxy container being recreated with a new address:
     `docker network ls`, then
     `docker network inspect <shared-network> --format '{{range .IPAM.Config}}{{.Subnet}} {{end}}'`.
     Use the user-defined network both containers are on, not the default `bridge`.
   - Proxy on the host or another machine - use the address Chronicle sees it connect from: make a
     request through it and read `connection from ...` in the `AUTH` log line (shown whenever a
     forwarded address was applied).
   Prefer the narrowest value that still covers the proxy: a single address if it never changes,
   otherwise the shared network's subnet.
3. **Set it** in `.env` next to `docker-compose.yml` (no edits to the compose file needed):
   `CHRONICLE_TRUSTED_PROXIES=<address-or-cidr>` (several values may be separated by commas), then
   `docker compose up -d`.
4. **Prove it.** Two checks.
   - *The setting is honoured.* Temporarily set `CHRONICLE_TRUSTED_PROXIES=192.0.2.1` (an address
     reserved for documentation, so nothing real matches it) and run `docker compose up -d`. Then
     send a request with a fake header:
     `curl -s -o /dev/null -H "X-Forwarded-For: 6.6.6.6" -H "Content-Type: application/json" -d '{"username":"probe","password":"xxxxxxxx"}' http://localhost:7979/api/v1/auth/login`
     and look at `docker compose logs api | grep "login FAILED" | tail -1`. It must NOT show
     `6.6.6.6`. (Do not use this curl as the final test with your real value set: a request from
     the Docker host can arrive from the proxy network's gateway address, which may be inside the
     trusted range, so the fake header would then be believed - correctly, as far as Chronicle can tell.)
   - *Real addresses come through.* Put your real value back, `docker compose up -d`, make a failed
     login through the proxy from a normal browser, and confirm the log line shows your own address.

Mistakes to avoid: a range that is too wide (any container in it can then claim an address); a
range that does NOT include the proxy (every user then appears to come from the proxy's own
address and one person's failed logins throttle everyone); and trusting the default `bridge`
network by habit.

### Caddy (Simpler Alternative)

**Installation:**
```bash
sudo apt install -y debian-keyring debian-archive-keyring apt-transport-https
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo apt update
sudo apt install caddy
```

**Caddyfile:**
```
chronicle.example.com {
    reverse_proxy localhost:8080
}
```

**That's it!** Caddy automatically handles:
- HTTPS certificates (Let's Encrypt)
- Certificate renewal
- HTTP to HTTPS redirect
- Security headers

```bash
sudo systemctl restart caddy
```

---

## Update System

### Automatic Updates

**Chronicle Updater Service:**

Chronicle includes a separate updater process that handles updates safely.

**How It Works:**

1. **Check for Updates:**
   - Queries GitHub Releases API every 6 hours (configurable)
   - Compares current version vs available version
   - Notifies user in UI if update available

2. **Download Update:**
   - Downloads new version to temp directory
   - Verifies SHA256 checksum
   - Validates compatibility

3. **Backup:**
   - Automatic database backup before update
   - Backup configuration files
   - Record current version

4. **Install:**
   - Stop Chronicle service
   - Extract new version to `/bin/vX.X.X/`
   - Update `/bin/current` symlink atomically
   - Start Chronicle service

5. **Health Check:**
   - Verify Chronicle starts successfully
   - Check database connection
   - Validate API responds
   - 60-second timeout

6. **Rollback (if failed):**
   - Stop new version
   - Restore old version symlink
   - Restore database from backup
   - Start old version
   - Alert user

**Directory Structure:**
```
/chronicle/
├── /bin/
│   ├── current → v1.2.0/    (symlink)
│   ├── v1.0.0/
│   ├── v1.1.0/
│   └── v1.2.0/              (active)
├── /data/
│   └── chronicle.db
└── updater.exe
```

### Manual Updates

**Windows:**
```powershell
# Stop Chronicle
Stop-Service Chronicle

# Download new version
Invoke-WebRequest -Uri "https://github.com/.../Chronicle-v1.1.0.zip" -OutFile "Chronicle-v1.1.0.zip"

# Backup
Copy-Item "C:\Chronicle\data" "C:\Chronicle\data.backup" -Recurse

# Extract new version
Expand-Archive -Path "Chronicle-v1.1.0.zip" -DestinationPath "C:\Chronicle\bin\v1.1.0"

# Update symlink
Remove-Item "C:\Chronicle\bin\current"
New-Item -ItemType SymbolicLink -Path "C:\Chronicle\bin\current" -Target "C:\Chronicle\bin\v1.1.0"

# Start Chronicle
Start-Service Chronicle
```

**Linux:**
```bash
# Stop service
sudo systemctl stop chronicle

# Backup
sudo cp -r /opt/chronicle/data /opt/chronicle/data.backup.$(date +%Y%m%d)

# Download
wget https://github.com/.../chronicle-v1.1.0-linux-x64.tar.gz

# Extract
tar -xzf chronicle-v1.1.0-linux-x64.tar.gz
sudo mv chronicle /opt/chronicle/bin/v1.1.0

# Update symlink
sudo rm /opt/chronicle/bin/current
sudo ln -s /opt/chronicle/bin/v1.1.0 /opt/chronicle/bin/current

# Start service
sudo systemctl start chronicle

# Verify
sudo systemctl status chronicle
```

**Docker:**
```bash
# Pull new image
docker-compose pull

# Stop and recreate containers
docker-compose up -d

# Cleanup old images
docker image prune -f
```

### Update Channels

**stable:** Production-ready releases (recommended)
**beta:** Pre-release testing, may have bugs
**nightly:** Daily builds, bleeding edge (not recommended for production)
**manual:** Never auto-update

**Configuration:**
```json
{
  "updates": {
    "check_for_updates": true,
    "auto_update": false,
    "update_channel": "stable"
  }
}
```

### Rollback

**Automatic Rollback:**
If update fails health check, Chronicle automatically rolls back.

**Manual Rollback:**

**Windows:**
```powershell
# List versions
ls C:\Chronicle\bin\

# Change symlink to previous version
Remove-Item "C:\Chronicle\bin\current"
New-Item -ItemType SymbolicLink -Path "C:\Chronicle\bin\current" -Target "C:\Chronicle\bin\v1.0.0"

# Restart
Restart-Service Chronicle
```

**Linux:**
```bash
# List versions
ls /opt/chronicle/bin/

# Change symlink
sudo rm /opt/chronicle/bin/current
sudo ln -s /opt/chronicle/bin/v1.0.0 /opt/chronicle/bin/current

# Restart
sudo systemctl restart chronicle
```

**Database Rollback:**
```bash
# If database was migrated and needs rollback
sudo systemctl stop chronicle
sudo cp /opt/chronicle/backups/pre-v1.1.0-upgrade.db /opt/chronicle/data/chronicle.db
sudo systemctl start chronicle
```

---

## Safe Mode

### Entering Safe Mode

**Trigger Safe Mode:**

**Method 1: Create File**
```bash
# Linux/macOS
touch /opt/chronicle/data/SAFE_MODE

# Windows
New-Item -Path "C:\Chronicle\data\SAFE_MODE" -ItemType File
```

**Method 2: Hold Shift Key**
- Hold Shift while starting Chronicle
- Release after Chronicle window appears

**Method 3: Boot Parameter**
```bash
./Chronicle --safe-mode
```

### Safe Mode Features

**What's Different:**
- ✅ All plugins disabled
- ✅ Minimal configuration loaded
- ✅ Read-only mode (no modifications)
- ✅ Diagnostic tools available
- ❌ Scrobbling disabled
- ❌ Background jobs disabled
- ❌ Updates disabled

**Safe Mode UI:**

```
╔═══════════════════════════════════════╗
║   CHRONICLE SAFE MODE                 ║
╟───────────────────────────────────────╢
║                                       ║
║ Chronicle is running in Safe Mode.   ║
║ All plugins are disabled.            ║
║                                       ║
║ Current Issues Detected:              ║
║ • Plugin "broken-plugin" failed       ║
║ • Database connection timeout         ║
║                                       ║
║ Options:                              ║
║ [1] Reset All Settings to Defaults   ║
║ [2] Disable Problematic Plugins      ║
║ [3] Restore from Backup              ║
║ [4] View Logs                        ║
║ [5] Run Database Integrity Check     ║
║ [6] Exit Safe Mode and Retry         ║
║                                       ║
║ Select option: _                     ║
╚═══════════════════════════════════════╝
```

### Exiting Safe Mode

```bash
# Remove safe mode file
rm /opt/chronicle/data/SAFE_MODE

# Restart Chronicle
sudo systemctl restart chronicle
```

---

## Backup & Restore

> **Built-in database backups (SQLite).** Settings -> Database (administrators) takes consistent zipped
> backups while Chronicle runs, nightly at 02:00 and on demand, keeps the newest 10 (configurable) in a
> `backups` folder next to the database file (in Docker, inside the data volume), and can download, upload,
> validate and restore them. A restore needs you to type `RESTORE`, makes a safety backup of the current data
> first, then restarts Chronicle to swap the file in (Docker's `restart: unless-stopped` and the Windows
> service recovery setting bring it back; with the dev script, run it again). Everyone is signed out by the
> restart. A backup checks itself before it is trusted: its checksum, its SQLite integrity, and that it is not
> from a *newer* Chronicle than the one running.
>
> Backups hold the whole database, including account password hashes, API key hashes and plugin credentials
> (which Chronicle stores as entered), so keep the files somewhere only administrators can read. The `keys`
> folder (Data Protection keys, used only to read old encrypted values) is not inside them. PostgreSQL is not backed up by the app; use
> `pg_dump`. Weekly "quick" and monthly "full rebuild" maintenance run as scheduled tasks too. SQLite's scratch
> files are kept in a `temp` folder beside the database, never the system temp folder.

### Automatic Backups

**Schedule:**
- Before updates (always)
- Daily at 1:00 AM (configurable)
- Before major operations (plugin install, bulk import)

**Backup Location:**
```
/backups/
├── pre-v1.1.0-upgrade.db
├── pre-v1.2.0-upgrade.db
├── daily-2026-01-01.db
├── daily-2026-01-02.db
└── daily-2026-01-03.db
```

**Retention Policy:**
```json
{
  "backup_retention": {
    "keep_daily": 30,
    "keep_weekly": 12,
    "keep_monthly": 12,
    "keep_before_upgrade": "all"
  }
}
```

### Manual Backup

**Via UI:**
1. Settings → Backup & Restore
2. Click "Create Backup Now"
3. Download backup file

**Via Command Line:**

**SQLite:**
```bash
# Stop Chronicle
sudo systemctl stop chronicle

# Backup database
cp /opt/chronicle/data/chronicle.db /opt/chronicle/backups/manual-$(date +%Y%m%d).db

# Backup config
cp /opt/chronicle/data/config.json /opt/chronicle/backups/config-$(date +%Y%m%d).json

# Start Chronicle
sudo systemctl start chronicle
```

**PostgreSQL:**
```bash
# Backup (no downtime required)
pg_dump -U chronicle -d chronicle -f /opt/chronicle/backups/chronicle-$(date +%Y%m%d).sql

# Or compressed
pg_dump -U chronicle -d chronicle | gzip > /opt/chronicle/backups/chronicle-$(date +%Y%m%d).sql.gz
```

### Restore from Backup

**Via UI:**
1. Stop Chronicle
2. Settings → Backup & Restore
3. Select backup file
4. Click "Restore"
5. Confirm
6. Chronicle restarts automatically

**Via Command Line:**

**SQLite:**
```bash
# Stop Chronicle
sudo systemctl stop chronicle

# Restore database
cp /opt/chronicle/backups/daily-2026-01-01.db /opt/chronicle/data/chronicle.db

# Start Chronicle
sudo systemctl start chronicle
```

**PostgreSQL:**
```bash
# Stop Chronicle
sudo systemctl stop chronicle

# Drop and recreate database
psql -U postgres -c "DROP DATABASE chronicle;"
psql -U postgres -c "CREATE DATABASE chronicle OWNER chronicle;"

# Restore
psql -U chronicle -d chronicle -f /opt/chronicle/backups/chronicle-2026-01-01.sql

# Or from compressed
gunzip < /opt/chronicle/backups/chronicle-2026-01-01.sql.gz | psql -U chronicle -d chronicle

# Start Chronicle
sudo systemctl start chronicle
```

---

## Troubleshooting

### Chronicle Won't Start

**1. Check Logs:**
```bash
# Linux systemd
sudo journalctl -u chronicle -n 100

# Docker
docker logs chronicle

# Windows
C:\Chronicle\logs\chronicle.log
```

**2. Common Issues:**

**Port Already in Use:**
```
Error: Failed to bind to address http://0.0.0.0:8080
Solution: Change port in config.json or stop conflicting service
```

**Database Locked:**
```
Error: Database is locked
Solution: Stop all Chronicle instances, remove lock file
rm /opt/chronicle/data/chronicle.db-wal
rm /opt/chronicle/data/chronicle.db-shm
```

**Permissions Error:**
```
Error: Access denied to /opt/chronicle/data
Solution: Fix ownership
sudo chown -R chronicle:chronicle /opt/chronicle
```

### Can't Access Web UI

**1. Check Chronicle is Running:**
```bash
# Linux
sudo systemctl status chronicle

# Docker
docker ps | grep chronicle

# Windows
tasklist | findstr Chronicle
```

**2. Check Port:**
```bash
# Linux
sudo netstat -tlnp | grep 8080

# Windows
netstat -ano | findstr :8080
```

**3. Check Firewall:**
```bash
# Linux
sudo ufw status
sudo ufw allow 8080

# Windows
netsh advfirewall firewall show rule name="Chronicle"
```

**4. Test Locally:**
```bash
curl http://localhost:8080/api/health
```

### Database Issues

**Corruption:**
```bash
# SQLite integrity check
sqlite3 /opt/chronicle/data/chronicle.db "PRAGMA integrity_check;"

# If corrupted, restore from backup
sudo systemctl stop chronicle
cp /opt/chronicle/backups/daily-2026-01-01.db /opt/chronicle/data/chronicle.db
sudo systemctl start chronicle
```

**Migration Failed:**
```bash
# Check migration status
SELECT * FROM schema_migrations;

# Restore from pre-update backup
cp /opt/chronicle/backups/pre-v1.1.0-upgrade.db /opt/chronicle/data/chronicle.db
```

### Plugin Issues

**Plugin Won't Load:**
```
Error: Failed to load plugin 'tmdb-scraper'
Solution: 
1. Check plugin compatibility with Chronicle version
2. View plugin log: /logs/plugins/tmdb-scraper.log
3. Try disabling and re-enabling plugin
4. Reinstall plugin
```

**Plugin Crashes Chronicle:**
```
Solution:
1. Enter Safe Mode (all plugins disabled)
2. Disable problematic plugin
3. Exit Safe Mode
```

### Performance Issues

**Slow Web UI:**
```
Diagnosis:
1. Check database size: ls -lh /opt/chronicle/data/chronicle.db
2. Check available RAM: free -h
3. Check CPU usage: top

Solutions:
- Vacuum database: VACUUM;
- Add indexes (see DATABASE_SCHEMA.md)
- Increase RAM allocation (Docker)
- Consider PostgreSQL for large libraries
```

**Slow Scrobbles:**
```
Diagnosis:
1. Check scraper response times in logs
2. Check network latency to scrapers
3. Check rate limiting

Solutions:
- Adjust scraper timeouts
- Disable slow scrapers
- Increase scraper priority
```

### Network Issues

**Can't Connect from Other Devices:**
```
1. Verify bind address: 0.0.0.0 (not 127.0.0.1)
2. Check firewall allows incoming on port 8080
3. Verify network connectivity
4. Check reverse proxy configuration
```

**SSL/TLS Errors:**
```
1. Verify certificate is valid
2. Check certificate chain
3. Verify reverse proxy configuration
4. Test with: curl -v https://chronicle.example.com
```

---

## Monitoring

### Health Check Endpoint

```bash
curl http://localhost:8080/api/health
```

**Response:**
```json
{
  "status": "healthy",
  "version": "1.2.0",
  "uptime_seconds": 86400,
  "database": "connected",
  "plugins": {
    "loaded": 5,
    "failed": 0
  },
  "disk_space_mb": 15000,
  "memory_usage_mb": 256
}
```

### Monitoring Tools

**Prometheus (Future):**
```yaml
# prometheus.yml
scrape_configs:
  - job_name: 'chronicle'
    static_configs:
      - targets: ['localhost:8080']
    metrics_path: '/api/metrics'
```

**Uptime Monitoring:**
- **Uptime Robot** - Free, simple
- **Healthchecks.io** - Cron job monitoring
- **Better Uptime** - Status pages

---

## Migration Guide

### From Trakt

**Export from Trakt:**
1. Go to Trakt Settings → Data
2. Export history (CSV)
3. Export ratings (CSV)

**Import to Chronicle:**
1. Settings → Import/Export
2. Select "Trakt Import"
3. Upload CSV files
4. Map fields (if needed)
5. Start import

### From SIMKL

Similar process to Trakt.

### From Letterboxd

**Export from Letterboxd:**
1. Settings → Import & Export
2. Export your data

**Import to Chronicle:**
1. Settings → Import/Export
2. Select "Letterboxd Import"
3. Upload CSV
4. Start import

---

## Production Deployment Checklist

- [ ] Use PostgreSQL instead of SQLite
- [ ] Enable HTTPS (reverse proxy)
- [ ] Change default JWT secret
- [ ] Set strong admin password
- [ ] Enable automatic backups
- [ ] Configure firewall
- [ ] Set up monitoring
- [ ] Enable rate limiting
- [ ] Review security headers
- [ ] Set up log rotation
- [ ] Configure email notifications
- [ ] Test backup restoration
- [ ] Document custom configuration
- [ ] Set up alerts
- [ ] Configure update channel (stable)

---

**Document Status:** Complete  
**Implementation Priority:** Phase 1 (Core deployment), Phase 2 (Advanced features)

## First start: no plugins are bundled

A new Chronicle has no plugins; they come from the plugin catalog (the hosted `plugins.json`). The first administrator sees a
"Chronicle has no plugins yet" banner on the Dashboard that opens **Getting started**: it asks which media you keep, offers a
file scanner and information sources from the catalog with an Install button each, asks for any API key a plugin needs (and tests
it), and ends at the Scan page. The server needs internet access to GitHub for this. For an install without internet access, copy a
plugin's folder (its DLL and `manifest.json`) into the `plugins/` folder and set the app setting `plugins.allow_unlisted` to `true`
if it is not in the catalog.
