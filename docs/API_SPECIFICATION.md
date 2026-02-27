# Chronicle API Specification

**Version:** 1.0  
**Last Updated:** 2026-01-12  
**Author:** Michael Beck with Anthropic Claude

---

## Overview

Chronicle provides a comprehensive REST API for programmatic access to all features. The API is used by:
- Web frontend
- Mobile apps
- Scrobbler clients (Kodi, Plex, etc.)
- Third-party integrations
- Automation scripts

**Base URL:** `http://localhost:8080/api/v1`

**API Documentation:** Available at `/swagger` when running Chronicle

---

## Authentication

### JWT Token Authentication (Web/Mobile)

**Login:**
```http
POST /api/v1/auth/login
Content-Type: application/json

{
  "username": "michael",
  "password": "secure_password"
}
```

**Response:**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "refresh_token": "dGVzdCByZWZyZXNoIHRva2Vu...",
  "expires_at": "2026-01-13T15:30:00Z",
  "user": {
    "id": 5,
    "username": "michael",
    "email": "michael@example.com",
    "role": "admin"
  }
}
```

**Using Token:**
```http
GET /api/v1/users/me
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

**Refresh Token:**
```http
POST /api/v1/auth/refresh
Content-Type: application/json

{
  "refresh_token": "dGVzdCByZWZyZXNoIHRva2Vu..."
}
```

### API Key Authentication (Scrobblers/Scripts)

**Create API Key:**
```http
POST /api/v1/auth/api-keys
Authorization: Bearer {jwt_token}
Content-Type: application/json

{
  "name": "Kodi Living Room",
  "permissions": ["scrobble", "read"],
  "expires_at": null
}
```

**Response:**
```json
{
  "id": 42,
  "token": "chr_live_a1b2c3d4e5f6g7h8i9j0",
  "name": "Kodi Living Room",
  "permissions": ["scrobble", "read"],
  "created_at": "2026-01-12T15:30:00Z",
  "expires_at": null
}
```

**Using API Key:**
```http
POST /api/v1/scrobble
X-API-Key: chr_live_a1b2c3d4e5f6g7h8i9j0
Content-Type: application/json

{...}
```

---

## Response Format

### Success Response

```json
{
  "success": true,
  "data": {
    "id": 123,
    "title": "Blade Runner"
  }
}
```

### Error Response

```json
{
  "success": false,
  "error": {
    "code": "MEDIA_NOT_FOUND",
    "message": "Media item with ID 999 not found",
    "details": {
      "media_id": 999
    }
  }
}
```

### Pagination

```json
{
  "success": true,
  "data": [...],
  "pagination": {
    "page": 2,
    "per_page": 50,
    "total_items": 247,
    "total_pages": 5,
    "has_next": true,
    "has_prev": true
  }
}
```

---

## Error Codes

| Code | HTTP Status | Description |
|------|-------------|-------------|
| `UNAUTHORIZED` | 401 | Missing or invalid authentication |
| `FORBIDDEN` | 403 | Insufficient permissions |
| `NOT_FOUND` | 404 | Resource not found |
| `VALIDATION_ERROR` | 400 | Invalid input data |
| `RATE_LIMITED` | 429 | Too many requests |
| `SERVER_ERROR` | 500 | Internal server error |
| `MEDIA_NOT_FOUND` | 404 | Media item not found |
| `USER_NOT_FOUND` | 404 | User not found |
| `DUPLICATE_ENTRY` | 409 | Resource already exists |
| `PLUGIN_ERROR` | 500 | Plugin operation failed |

---

## Core Endpoints

### Health Check

```http
GET /api/health
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
  }
}
```

---

## Authentication Endpoints

### Register User

```http
POST /api/v1/auth/register
Content-Type: application/json

{
  "username": "newuser",
  "email": "user@example.com",
  "password": "secure_password"
}
```

**Response:** Same as login

### Login

See [Authentication](#authentication) section above.

### Logout

```http
POST /api/v1/auth/logout
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "message": "Logged out successfully"
}
```

### Get Current User

```http
GET /api/v1/users/me
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "id": 5,
    "username": "michael",
    "email": "michael@example.com",
    "role": "admin",
    "created_at": "2025-01-01T00:00:00Z",
    "settings": {
      "timezone": "America/Edmonton",
      "theme": "dark"
    }
  }
}
```

---

## Scrobbling Endpoints

### Scrobble Media

```http
POST /api/v1/scrobble
Authorization: Bearer {token}
Content-Type: application/json

{
  "media_type": "tv",
  "media_id": 12345,
  "progress_percent": 85.5,
  "timestamp": "2026-01-12T20:30:00Z",
  "device_name": "Kodi Living Room",
  "session_id": "session_abc123"
}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "scrobble_id": 98765,
    "marked_watched": false,
    "progress_saved": true
  }
}
```

### Get Scrobble History

```http
GET /api/v1/scrobble/history?page=1&per_page=50
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": 98765,
      "media_type": "tv",
      "media_id": 12345,
      "media_title": "Breaking Bad - S01E01",
      "progress_percent": 100,
      "timestamp": "2026-01-12T20:30:00Z",
      "device_name": "Kodi Living Room"
    }
  ],
  "pagination": {...}
}
```

### Currently Watching

```http
GET /api/v1/scrobble/currently-watching
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "user_id": 5,
      "username": "michael",
      "media_type": "movie",
      "media_id": 78,
      "media_title": "Blade Runner",
      "progress_percent": 32.5,
      "started_at": "2026-01-12T20:00:00Z",
      "estimated_finish": "2026-01-12T21:47:00Z"
    }
  ]
}
```

---

## Media Endpoints

### Search Media

```http
GET /api/v1/media/search?query=blade+runner&type=movie
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": 78,
      "title": "Blade Runner",
      "year": 1982,
      "media_type": "movie",
      "poster_url": "https://image.tmdb.org/t/p/w500/...",
      "description": "A blade runner must pursue...",
      "rating": 8.1
    }
  ]
}
```

### Get Media Details

```http
GET /api/v1/media/78
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "id": 78,
    "media_type": "movie",
    "title": "Blade Runner",
    "year": 1982,
    "runtime": 117,
    "description": "A blade runner must pursue...",
    "poster_url": "https://...",
    "backdrop_url": "https://...",
    "genres": ["Sci-Fi", "Thriller"],
    "directors": ["Ridley Scott"],
    "cast": [
      {"name": "Harrison Ford", "character": "Rick Deckard"}
    ],
    "ratings": {
      "imdb": 8.1,
      "tmdb": 7.9
    },
    "external_ids": {
      "imdb": "tt0083658",
      "tmdb": 78
    },
    "versions": [
      {
        "id": 78,
        "name": "Theatrical Cut",
        "year": 1982,
        "runtime": 117
      },
      {
        "id": 79,
        "name": "Final Cut",
        "year": 2007,
        "runtime": 117
      }
    ]
  }
}
```

### Add Media to Library

```http
POST /api/v1/library
Authorization: Bearer {token}
Content-Type: application/json

{
  "media_id": 78,
  "status": "plan_to_watch",
  "private": false
}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "library_id": 456,
    "media_id": 78,
    "status": "plan_to_watch",
    "added_at": "2026-01-12T15:30:00Z"
  }
}
```

### Update Media Status

```http
PATCH /api/v1/library/456
Authorization: Bearer {token}
Content-Type: application/json

{
  "status": "watching",
  "rating": 9,
  "notes": "Amazing cinematography"
}
```

### Remove from Library

```http
DELETE /api/v1/library/456
Authorization: Bearer {token}
```

---

## User Library Endpoints

### Get User Library

```http
GET /api/v1/users/5/library?status=watching&page=1
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "library_id": 456,
      "media": {
        "id": 78,
        "title": "Blade Runner",
        "poster_url": "https://..."
      },
      "status": "watching",
      "rating": 9,
      "progress": {
        "current": 1,
        "total": 1
      },
      "added_at": "2026-01-12T15:30:00Z",
      "updated_at": "2026-01-12T20:30:00Z"
    }
  ],
  "pagination": {...}
}
```

### Get Watch History

```http
GET /api/v1/users/5/history?page=1&per_page=50
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": 98765,
      "media": {
        "id": 12345,
        "title": "Breaking Bad - S01E01",
        "poster_url": "https://..."
      },
      "watched_at": "2026-01-12T20:30:00Z",
      "progress_percent": 100,
      "device_name": "Kodi Living Room"
    }
  ],
  "pagination": {...}
}
```

---

## Statistics Endpoints

### Get User Statistics

```http
GET /api/v1/users/5/stats
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "total_watch_time_minutes": 125460,
    "total_episodes_watched": 1247,
    "total_movies_watched": 342,
    "total_books_read": 87,
    "average_rating": 7.8,
    "most_watched_genre": "Sci-Fi",
    "completion_rate": 0.82,
    "this_week": {
      "episodes": 15,
      "movies": 2,
      "watch_time_minutes": 1320
    },
    "this_month": {
      "episodes": 67,
      "movies": 8,
      "watch_time_minutes": 5640
    }
  }
}
```

### Get Watch Time Over Time

```http
GET /api/v1/users/5/stats/timeline?period=week&weeks=12
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "period": "week",
    "timeline": [
      {
        "week": "2026-W01",
        "minutes": 1320,
        "episodes": 15,
        "movies": 2
      },
      {
        "week": "2026-W02",
        "minutes": 960,
        "episodes": 10,
        "movies": 1
      }
    ]
  }
}
```

---

## Session Endpoints

### List Watch Sessions

```http
GET /api/v1/media/12345/sessions
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "session_id": 1,
      "session_name": "First Watch",
      "status": "completed",
      "started_at": "2023-01-15T00:00:00Z",
      "completed_at": "2023-05-12T00:00:00Z",
      "episodes_watched": 79,
      "total_episodes": 79
    },
    {
      "session_id": 2,
      "session_name": "Rewatch",
      "status": "in_progress",
      "started_at": "2025-11-20T00:00:00Z",
      "completed_at": null,
      "episodes_watched": 15,
      "total_episodes": 79
    }
  ]
}
```

### Create Watch Session

```http
POST /api/v1/media/12345/sessions
Authorization: Bearer {token}
Content-Type: application/json

{
  "session_name": "Summer 2026 Rewatch"
}
```

### Mark Episode in Session

```http
POST /api/v1/sessions/2/episodes/12346
Authorization: Bearer {token}
Content-Type: application/json

{
  "watched": true,
  "watched_at": "2026-01-12T20:30:00Z"
}
```

---

## Version Endpoints

### List Media Versions

```http
GET /api/v1/media-groups/blade-runner/versions
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "group_id": "blade-runner",
    "group_name": "Blade Runner",
    "versions": [
      {
        "version_id": 78,
        "version_name": "Theatrical Cut",
        "year": 1982,
        "runtime": 117,
        "watch_count": 2,
        "is_preferred": false
      },
      {
        "version_id": 79,
        "version_name": "Final Cut",
        "year": 2007,
        "runtime": 117,
        "watch_count": 5,
        "is_preferred": true
      }
    ]
  }
}
```

### Set Preferred Version

```http
PATCH /api/v1/media/79/preferred
Authorization: Bearer {token}
Content-Type: application/json

{
  "preferred": true
}
```

---

## Group Endpoints

### Create Group

```http
POST /api/v1/groups
Authorization: Bearer {token}
Content-Type: application/json

{
  "name": "Family",
  "type": "family",
  "description": "Our family viewing"
}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "group_id": 10,
    "name": "Family",
    "type": "family",
    "created_at": "2026-01-12T15:30:00Z",
    "owner_id": 5
  }
}
```

### Add Member to Group

```http
POST /api/v1/groups/10/members
Authorization: Bearer {token}
Content-Type: application/json

{
  "user_id": 12,
  "role": "member",
  "permissions": ["view", "scrobble"]
}
```

### Get Group Activity

```http
GET /api/v1/groups/10/activity?page=1
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "user_id": 12,
      "username": "jane",
      "activity_type": "watched",
      "media": {
        "id": 78,
        "title": "Blade Runner"
      },
      "timestamp": "2026-01-12T20:30:00Z"
    }
  ],
  "pagination": {...}
}
```

---

## Calendar Endpoints

### Get Upcoming Releases

```http
GET /api/v1/calendar/upcoming?days=7&media_types=tv,movie
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "media_id": 12346,
      "media_type": "tv",
      "title": "Breaking Bad - S01E02",
      "air_date": "2026-01-15T00:00:00Z",
      "poster_url": "https://..."
    },
    {
      "media_id": 5678,
      "media_type": "movie",
      "title": "New Movie Release",
      "release_date": "2026-01-16T00:00:00Z",
      "poster_url": "https://..."
    }
  ]
}
```

### Get Historical Calendar

```http
GET /api/v1/calendar/history?month=2026-01
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "2026-01-01": [
      {
        "media_id": 100,
        "title": "Movie Title",
        "watched_at": "2026-01-01T20:00:00Z"
      }
    ],
    "2026-01-05": [
      {
        "media_id": 200,
        "title": "Episode Title",
        "watched_at": "2026-01-05T19:30:00Z"
      }
    ]
  }
}
```

---

## Plugin Endpoints

### List Plugins

```http
GET /api/v1/plugins
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "plugin_id": 1,
      "name": "TMDB Scraper",
      "version": "2.1.0",
      "type": "metadata_provider",
      "is_enabled": true,
      "is_system": true,
      "supported_media_types": ["movie", "tv"]
    }
  ]
}
```

### Get Plugin Settings

```http
GET /api/v1/plugins/1/settings
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "plugin_id": 1,
    "settings_schema": {
      "settings": [
        {
          "key": "api_key",
          "label": "API Key",
          "type": "password",
          "required": true
        }
      ]
    },
    "current_values": {
      "api_key": "***hidden***",
      "language": "en-US"
    }
  }
}
```

### Update Plugin Settings

```http
PATCH /api/v1/plugins/1/settings
Authorization: Bearer {token}
Content-Type: application/json

{
  "api_key": "new_api_key_value",
  "language": "fr-FR"
}
```

### Enable/Disable Plugin

```http
PATCH /api/v1/plugins/1
Authorization: Bearer {token}
Content-Type: application/json

{
  "is_enabled": false
}
```

---

## Webhook Endpoints

### Create Webhook

```http
POST /api/v1/webhooks
Authorization: Bearer {token}
Content-Type: application/json

{
  "name": "Discord Notification",
  "url": "https://discord.com/api/webhooks/...",
  "events": ["scrobble", "completed_series"],
  "is_enabled": true,
  "secret": "optional_signing_secret"
}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "webhook_id": 25,
    "name": "Discord Notification",
    "url": "https://discord.com/api/webhooks/...",
    "events": ["scrobble", "completed_series"],
    "is_enabled": true,
    "created_at": "2026-01-12T15:30:00Z"
  }
}
```

### Test Webhook

```http
POST /api/v1/webhooks/25/test
Authorization: Bearer {token}
```

**Payload Sent:**
```json
{
  "event": "test",
  "timestamp": "2026-01-12T15:30:00Z",
  "data": {
    "message": "Test webhook from Chronicle"
  },
  "signature": "sha256_hmac_if_secret_configured"
}
```

---

## Import/Export Endpoints

### Export Data

```http
POST /api/v1/export
Authorization: Bearer {token}
Content-Type: application/json

{
  "format": "json",
  "include": ["library", "history", "ratings"]
}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "export_id": "exp_abc123",
    "download_url": "/api/v1/exports/exp_abc123/download",
    "expires_at": "2026-01-13T15:30:00Z"
  }
}
```

### Import Data

```http
POST /api/v1/import
Authorization: Bearer {token}
Content-Type: multipart/form-data

file: [trakt-export.csv]
source: "trakt"
```

**Response:**
```json
{
  "success": true,
  "data": {
    "import_id": "imp_xyz789",
    "status": "processing",
    "items_found": 1247,
    "items_imported": 0,
    "status_url": "/api/v1/imports/imp_xyz789"
  }
}
```

### Check Import Status

```http
GET /api/v1/imports/imp_xyz789
Authorization: Bearer {token}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "import_id": "imp_xyz789",
    "status": "completed",
    "items_found": 1247,
    "items_imported": 1245,
    "items_failed": 2,
    "completed_at": "2026-01-12T15:45:00Z"
  }
}
```

---

## Admin Endpoints

### List All Users

```http
GET /api/v1/admin/users?page=1
Authorization: Bearer {admin_token}
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": 5,
      "username": "michael",
      "email": "michael@example.com",
      "role": "admin",
      "created_at": "2025-01-01T00:00:00Z",
      "last_login": "2026-01-12T15:00:00Z"
    }
  ],
  "pagination": {...}
}
```

### Create User

```http
POST /api/v1/admin/users
Authorization: Bearer {admin_token}
Content-Type: application/json

{
  "username": "newuser",
  "email": "new@example.com",
  "password": "secure_password",
  "role": "user"
}
```

### Delete User

```http
DELETE /api/v1/admin/users/12
Authorization: Bearer {admin_token}
```

### Get System Settings

```http
GET /api/v1/admin/settings
Authorization: Bearer {admin_token}
```

### Update System Settings

```http
PATCH /api/v1/admin/settings
Authorization: Bearer {admin_token}
Content-Type: application/json

{
  "enable_registration": false,
  "require_email_verification": true
}
```

### View Audit Log

```http
GET /api/v1/admin/audit?page=1&event_type=failed_login
Authorization: Bearer {admin_token}
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": 9876,
      "timestamp": "2026-01-12T14:25:00Z",
      "user_id": 5,
      "event_type": "failed_login",
      "ip_address": "192.168.1.100",
      "details": {
        "username": "michael",
        "reason": "invalid_password"
      },
      "severity": "warning"
    }
  ],
  "pagination": {...}
}
```

---

## Rate Limiting

**Headers in Response:**
```
X-RateLimit-Limit: 100
X-RateLimit-Remaining: 95
X-RateLimit-Reset: 1704729000
```

**When Limited:**
```
HTTP/1.1 429 Too Many Requests
Retry-After: 45

{
  "success": false,
  "error": {
    "code": "RATE_LIMITED",
    "message": "Rate limit exceeded",
    "retry_after": 45
  }
}
```

---

## Pagination

**Query Parameters:**
- `page` - Page number (default: 1)
- `per_page` - Items per page (default: 50, max: 100)

**Example:**
```http
GET /api/v1/library?page=2&per_page=50
```

**Response includes:**
```json
{
  "pagination": {
    "page": 2,
    "per_page": 50,
    "total_items": 247,
    "total_pages": 5,
    "has_next": true,
    "has_prev": true
  }
}
```

---

## Filtering & Sorting

**Query Parameters:**
- `filter[field]` - Filter by field value
- `sort` - Sort field (prefix with `-` for descending)

**Examples:**
```http
GET /api/v1/library?filter[status]=watching&sort=-updated_at
GET /api/v1/media/search?query=blade&filter[year]=1982&sort=rating
```

---

## Webhooks

**Event Payload:**
```json
{
  "event": "scrobble",
  "timestamp": "2026-01-12T20:30:00Z",
  "user": {
    "id": 5,
    "username": "michael"
  },
  "data": {
    "media_id": 12345,
    "media_title": "Breaking Bad - S01E01",
    "progress_percent": 100
  },
  "signature": "sha256=abc123..."
}
```

**Verifying Signature:**
```python
import hmac
import hashlib

secret = "webhook_secret"
payload = request.body
signature = request.headers['X-Chronicle-Signature']

expected = 'sha256=' + hmac.new(
    secret.encode(),
    payload,
    hashlib.sha256
).hexdigest()

if signature == expected:
    # Valid webhook
    pass
```

---

## Example Workflows

### Complete Scrobble Flow

```javascript
// 1. Authenticate
const loginResp = await fetch('/api/v1/auth/login', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    username: 'michael',
    password: 'secure_password'
  })
});
const { token } = await loginResp.json();

// 2. Search for media
const searchResp = await fetch('/api/v1/media/search?query=blade+runner', {
  headers: { 'Authorization': `Bearer ${token}` }
});
const { data: results } = await searchResp.json();
const mediaId = results[0].id;

// 3. Add to library
await fetch('/api/v1/library', {
  method: 'POST',
  headers: {
    'Authorization': `Bearer ${token}`,
    'Content-Type': 'application/json'
  },
  body: JSON.stringify({
    media_id: mediaId,
    status: 'watching'
  })
});

// 4. Scrobble
await fetch('/api/v1/scrobble', {
  method: 'POST',
  headers: {
    'Authorization': `Bearer ${token}`,
    'Content-Type': 'application/json'
  },
  body: JSON.stringify({
    media_type: 'movie',
    media_id: mediaId,
    progress_percent: 100,
    timestamp: new Date().toISOString()
  })
});
```

---

## Client Libraries

### Official (Future)
- JavaScript/TypeScript
- Python
- Go
- C#

### Community
- Ruby
- PHP
- Java
- Rust

---

**Document Status:** Complete  
**Implementation Priority:** Phase 1 (Core endpoints), Phase 2 (Advanced features)
