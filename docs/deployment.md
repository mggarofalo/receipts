# Deployment Guide

Deploy Receipts on a Raspberry Pi (or any Linux host) using Docker Compose with Nginx Proxy Manager (NPM) for HTTPS termination.

## Prerequisites

- **Raspberry Pi 4/5** (4GB+ RAM) or any ARM64/AMD64 Linux host
- **Docker** and **Docker Compose** (v2+)
- **Nginx Proxy Manager** running on the same host or network
- A **domain name** pointed to your public IP (A record)

### Install Docker (Raspberry Pi OS)

```bash
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER
# Log out and back in for group change to take effect
```

## Quick Start

The `docker-compose.yml` is fully self-contained — no `.env` file or manual secret generation required. Secrets are auto-generated on first run.

```bash
# 1. Clone the repository (or just copy docker-compose.yml)
git clone https://github.com/mggarofalo/Receipts.git
cd Receipts

# 2. Start the application
docker compose up -d

# 3. Get the auto-generated admin password
docker compose exec app cat /secrets/admin_password

# 4. Verify
docker compose ps
curl http://localhost:8080/api/health
```

The app will be available at `http://<your-host-ip>:8080`.

## How It Works

On first `docker compose up`:

1. **init** container generates random secrets (`pg_password`, `jwt_key`, `admin_password`) to a shared `secrets` volume, then exits
2. **db** (PostgreSQL) starts using `POSTGRES_PASSWORD_FILE` to read the generated password
3. **app** reads secrets from the volume, runs migrations and seeding, then starts the API
4. **app** downloads the embedding model (~1.34 GB) into the `app-data` volume in the background

On subsequent starts, the init container detects existing secrets and skips generation. Secrets persist in the `secrets` Docker volume.

### First start and the embedding model

The embedding model is not shipped inside the image — that kept the image at 2.54 GB and re-pushed an unchanging 1.34 GB blob on every release. It is fetched once into `app-data` at `/data/models` instead, verified against a pinned upstream revision and SHA-256 before use.

The API serves traffic normally throughout. Until the download finishes, only the semantic features degrade: description normalization and similarity search fall back rather than fail. Progress and any failures appear in `docker compose logs -f app`. A failed download is retried with backoff and never stops the container.

Two settings control this:

| Variable | Default | Purpose |
|----------|---------|---------|
| `Embeddings__ModelPath` | `/data/models/BgeLargeEnV15` | Where the model is stored |
| `Embeddings__AutoDownload` | `true` | Set `false` for air-gapped hosts |

For an air-gapped deployment, run `dotnet run scripts/download-onnx-model.cs` on a connected machine, copy the resulting directory into the `app-data` volume at `/data/models/BgeLargeEnV15`, and set `Embeddings__AutoDownload=false`.

## Configuration

Edit values directly in `docker-compose.yml`. No `.env` file needed.

### App Service

| Variable | Default | Description |
|----------|---------|-------------|
| `PUID` | `1654` | User ID for the app process |
| `PGID` | `1654` | Group ID for the app process |
| `TZ` | `America/New_York` | Timezone ([tz database](https://en.wikipedia.org/wiki/List_of_tz_database_time_zones)) |
| `POSTGRES_HOST` | `db` | Database hostname |
| `POSTGRES_PORT` | `5432` | Database port |
| `POSTGRES_USER` | `receipts` | Database username |
| `POSTGRES_DB` | `receipts` | Database name |
| `Jwt__Issuer` | `receipts-api` | JWT issuer claim |
| `Jwt__Audience` | `receipts-app` | JWT audience claim |
| `AdminSeed__Email` | `admin@example.com` | Initial admin account email |
| `AdminSeed__FirstName` | `Admin` | Admin first name |
| `AdminSeed__LastName` | `User` | Admin last name |
| `IMAGE_TAG` | `latest` | Docker image tag (used by operations scripts) |

### Auto-Generated Secrets

These are generated automatically and stored as files in the `secrets` volume:

| File | Used As | Description |
|------|---------|-------------|
| `/secrets/pg_password` | `POSTGRES_PASSWORD` | PostgreSQL password |
| `/secrets/jwt_key` | `Jwt__Key` | JWT signing key |
| `/secrets/admin_password` | `AdminSeed__Password` | Initial admin password |

### PUID / PGID

Match the PUID/PGID to your host user to avoid permission issues with bind mounts:

```bash
# Find your user's UID and GID
id $USER
# uid=1000(pi) gid=1000(pi) ...

# Then set in docker-compose.yml:
# - PUID=1000
# - PGID=1000
```

## Secrets Management

### View secrets

```bash
docker compose exec app cat /secrets/pg_password
docker compose exec app cat /secrets/jwt_key
docker compose exec app cat /secrets/admin_password
```

### Rotate secrets

```bash
# Stop the stack
docker compose down

# Remove the secrets volume (this deletes all secrets)
docker volume rm receipts_secrets

# Start again (new secrets are generated)
docker compose up -d
```

> **Note:** Rotating `pg_password` also requires resetting the PostgreSQL data volume since the password is set on first database initialization.

### Full reset (secrets + database)

```bash
docker compose down -v   # removes all volumes
docker compose up -d     # fresh start with new secrets and empty database
```

## Nginx Proxy Manager Setup

NPM provides HTTPS termination with automatic Let's Encrypt certificates.

### Create a Proxy Host

1. Open NPM admin panel (typically `http://<pi-ip>:81`)
2. **Proxy Hosts** → **Add Proxy Host**
3. Configure:
   - **Domain Names**: `receipts.yourdomain.com`
   - **Scheme**: `http`
   - **Forward Hostname/IP**: `receipts-app` (or `localhost` if on same Docker network)
   - **Forward Port**: `8080`
   - **Websockets Support**: **ON** (required for SignalR real-time updates)
4. **SSL** tab:
   - **SSL Certificate**: Request a new certificate
   - **Force SSL**: ON
   - **HTTP/2 Support**: ON
   - **HSTS Enabled**: ON

### Custom Nginx Configuration (Advanced tab)

```nginx
# Security headers
add_header X-Content-Type-Options "nosniff" always;
add_header X-Frame-Options "SAMEORIGIN" always;
add_header Referrer-Policy "strict-origin-when-cross-origin" always;

# Increase body size for receipt uploads
client_max_body_size 10M;
```

### Network Configuration

If NPM runs on the same host, add the NPM container to the `receipts-net` network, or use the host IP. The app container is named `receipts-app` and listens on port 8080.

## Operations

### Update to a new version

```bash
dotnet run scripts/update.cs              # Update to latest
dotnet run scripts/update.cs -- v1.2.3    # Update to specific version
```

The update script automatically backs up the database before updating and rolls back if the health check fails.

### Roll back

```bash
dotnet run scripts/rollback.cs -- v1.1.0     # Roll back to a specific tag
dotnet run scripts/rollback.cs -- sha-abc123 # Roll back to a specific build
```

### Backup and restore

```bash
dotnet run scripts/backup.cs              # Backup to ./backups/
dotnet run scripts/backup.cs -- /mnt/usb  # Backup to external drive
dotnet run scripts/restore.cs -- backups/receipts_20260308_120000.sql.gz
```

Backups older than 7 days are automatically pruned. For automated daily backups, add a cron job:

```bash
crontab -e
# Add:
0 3 * * * cd /home/pi/Receipts && dotnet run scripts/backup.cs >> /var/log/receipts-backup.log 2>&1
```

### View logs

```bash
docker compose logs -f app          # Follow app logs
docker compose logs -f db           # Follow database logs
docker compose logs --tail=100 app  # Last 100 lines
```

### Restart services

```bash
docker compose restart app           # Restart app only
docker compose restart               # Restart all services
docker compose down && docker compose up -d  # Full restart
```

## Troubleshooting

### App won't start

```bash
docker compose logs app | head -50   # Check startup logs
docker compose exec db pg_isready -U receipts  # Check DB connectivity
```

### Secrets missing

If the app fails with "secrets not found", check that the init container ran successfully:

```bash
docker compose logs init
```

If the secrets volume is corrupted, remove it and restart:

```bash
docker compose down
docker volume rm receipts_secrets
docker compose up -d
```

### Database connection errors

Ensure `POSTGRES_USER` matches between the `app` and `db` services in `docker-compose.yml`. The database must be healthy before the app starts (enforced by `depends_on`).

### Port already in use

Change the host port mapping in `docker-compose.yml` under `app.ports` (e.g., `"8081:8080"`).

### Out of memory

The app is limited to 1GB RAM by default. Check usage:

```bash
docker stats --no-stream
```

If the host is running low, reduce PostgreSQL memory settings in `docker-compose.yml` (e.g., `shared_buffers=64MB`).

### SignalR WebSocket issues

Ensure **Websockets Support** is enabled in your NPM proxy host configuration. SignalR falls back to long-polling if WebSockets are unavailable, but real-time updates will be delayed.

## Security

- All containers drop **all capabilities** (`cap_drop: ALL`) with only minimal capabilities added back
- App container uses `gosu` for clean privilege de-escalation (root → app user)
- `no-new-privileges` security option prevents privilege escalation
- Secrets are auto-generated and stored in a Docker volume (never in files on disk or environment)
- Database is only accessible on the **internal Docker network** (not exposed to host)
- Docker images are scanned for vulnerabilities with **Trivy** in CI
- Log rotation configured (50MB max, 3 files)

### Firewall (recommended)

Only expose the ports NPM needs:

```bash
sudo ufw default deny incoming
sudo ufw allow ssh
sudo ufw allow 80/tcp    # HTTP (NPM redirect to HTTPS)
sudo ufw allow 443/tcp   # HTTPS (NPM)
sudo ufw enable
```

Do **not** expose port 8080 externally — NPM proxies to it internally.

### Application Rate Limiting

The API enforces rate limiting at the application level (defense-in-depth with CloudFlare and NPM):

| Policy | Endpoints | Limit | Window |
|--------|-----------|-------|--------|
| Global | All endpoints | 100 requests | 1 minute (sliding) |
| `auth` | Login | 5 requests | 1 minute |
| `auth-sensitive` | Refresh, change password | 10 requests | 1 minute |
| `api-key` | API key operations | 10 requests | 1 minute |

Rate limits are configurable in `appsettings.json` under the `RateLimiting` section — no code changes required.

Clients receive HTTP 429 with a `Retry-After` header when limits are exceeded. All rate limit violations are logged to the auth audit trail.

### Fail2ban (recommended)

Fail2ban automatically blocks IPs with repeated auth failures at the firewall level.

**Install:**

```bash
sudo apt install fail2ban
```

**Create jail** (`/etc/fail2ban/jail.d/receipts-app.conf`):

```ini
[receipts-app]
enabled = true
port = http,https
filter = receipts-app
logpath = /var/log/syslog
backend = systemd
maxretry = 5
findtime = 600
bantime = 3600
ignoreip = 127.0.0.1 192.168.0.0/16
```

**Create filter** (`/etc/fail2ban/filter.d/receipts-app.conf`):

```ini
[Definition]
failregex = ^.*LoginFailed.*IpAddress=<HOST>.*$
            ^.*RateLimitExceeded.*IpAddress=<HOST>.*$
ignoreregex =
```

**Enable and start:**

```bash
sudo systemctl enable fail2ban
sudo systemctl start fail2ban
```

**Monitor:**

```bash
sudo fail2ban-client status receipts-app     # Show jail status
sudo fail2ban-client get receipts-app banned  # List banned IPs
sudo fail2ban-client set receipts-app unbanip 1.2.3.4  # Unban an IP
```

## Resource Usage

Target for Raspberry Pi 4 (4GB RAM):

| Service | Memory Limit | Typical Usage |
|---------|-------------|---------------|
| App (API + SPA) | 1 GB | ~200-400 MB |
| PostgreSQL | uncapped | ~100-200 MB |
| **Total** | — | ~300-600 MB |

Disk: Docker images ~660 MB, plus ~1.4 GB in the `app-data` volume for the embedding model (downloaded once on first start, not re-pulled on upgrades). Database grows with usage.
