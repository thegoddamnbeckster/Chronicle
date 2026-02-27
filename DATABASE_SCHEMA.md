# Chronicle Database Schema
## Version 1.0
**Date:** 2026-01-11  
**Status:** Design Phase

---

## Overview

The Chronicle database is designed with flexibility and extensibility as core principles. Rather than creating type-specific tables for TV shows, movies, music, etc., the schema uses a generalized approach where media types are configurable and data is stored in flexible structures.

**Key Design Principles:**
1. **Generic data model** - No hardcoded media types
2. **JSON for flexibility** - Type-specific data in JSON columns
3. **Additive migrations** - Schema changes don't break old versions
4. **Proper indexing** - Performance for large datasets
5. **Referential integrity** - Foreign keys with cascading rules

**Database Support:**
- **SQLite** (default) - Single file, zero configuration
- **PostgreSQL** (production) - Better performance for multi-user

---

## Schema Version Tracking

### `schema_version`

Tracks applied database migrations.

```sql
CREATE TABLE schema_version (
    version INTEGER PRIMARY KEY,
    applied_date TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    description TEXT NOT NULL,
    checksum TEXT -- SHA256 of migration file
);
```

**Indexes:** None (small table)

**Notes:**
- Each migration increments version
- Cannot skip versions
- Rollback uses down migrations

---

## User Management

### `users`

Core user accounts.

```sql
CREATE TABLE users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT NOT NULL UNIQUE,
    email TEXT UNIQUE,
    password_hash TEXT NOT NULL, -- bcrypt, cost 12
    display_name TEXT,
    avatar_url TEXT, -- URL only, no file storage
    bio TEXT,
    timezone TEXT DEFAULT 'UTC',
    locale TEXT DEFAULT 'en-US',
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_login_at TIMESTAMP,
    is_active BOOLEAN NOT NULL DEFAULT 1,
    is_admin BOOLEAN NOT NULL DEFAULT 0,
    privacy_settings JSON, -- {profile_public: bool, history_public: bool, etc}
    preferences JSON -- {theme: "dark", items_per_page: 50, etc}
);

CREATE UNIQUE INDEX idx_users_username ON users(username);
CREATE UNIQUE INDEX idx_users_email ON users(email);
CREATE INDEX idx_users_is_active ON users(is_active);
```

**Privacy Settings Example:**
```json
{
  "profile_public": true,
  "history_public": false,
  "stats_public": true,
  "currently_watching_public": true,
  "allow_friend_requests": true
}
```

**Preferences Example:**
```json
{
  "theme": "dark",
  "items_per_page": 50,
  "default_media_type": "tv",
  "language": "en",
  "date_format": "YYYY-MM-DD"
}
```

### `api_tokens`

Authentication tokens for API access (scrobblers).

```sql
CREATE TABLE api_tokens (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    token TEXT NOT NULL UNIQUE, -- UUID or JWT
    name TEXT NOT NULL, -- "Kodi Living Room", "MPC-HC Desktop"
    scopes TEXT NOT NULL, -- Comma-separated: "scrobble,read,write"
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at TIMESTAMP, -- NULL = never expires
    last_used_at TIMESTAMP,
    is_active BOOLEAN NOT NULL DEFAULT 1,
    
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX idx_api_tokens_token ON api_tokens(token);
CREATE INDEX idx_api_tokens_user_id ON api_tokens(user_id);
CREATE INDEX idx_api_tokens_is_active ON api_tokens(is_active);
```

**Notes:**
- Users can have multiple tokens
- Revokable individually
- Track last usage for security auditing

---

## Media Type System

### `media_types`

Defines configurable media types (TV, Movies, Music, Books, etc.).

```sql
CREATE TABLE media_types (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE, -- "tv", "movie", "music", "book", etc
    display_name TEXT NOT NULL, -- "TV Shows", "Movies", etc
    icon TEXT, -- Icon identifier
    color TEXT, -- Hex color for UI
    hierarchy_levels JSON NOT NULL, -- ["show", "season", "episode"]
    interaction_verb TEXT NOT NULL, -- "watch", "listen", "read"
    session_noun TEXT NOT NULL, -- "watch", "scrobble", "reading session"
    progress_unit TEXT NOT NULL, -- "episode", "track", "page"
    metadata_schema JSON, -- Optional: Define expected fields
    is_enabled BOOLEAN NOT NULL DEFAULT 1,
    is_system BOOLEAN NOT NULL DEFAULT 0, -- True for built-in types
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX idx_media_types_name ON media_types(name);
CREATE INDEX idx_media_types_is_enabled ON media_types(is_enabled);
```

**Hierarchy Levels Example (TV):**
```json
["show", "season", "episode"]
```

**Hierarchy Levels Example (Music):**
```json
["artist", "album", "track"]
```

**Metadata Schema Example:**
```json
{
  "fields": [
    {"name": "runtime", "type": "integer", "required": false},
    {"name": "genres", "type": "array", "required": false},
    {"name": "rating", "type": "string", "required": false}
  ]
}
```

**Notes:**
- New media types added via INSERT or plugin
- User-created types have `is_system = 0`
- Disabling type hides from UI but preserves data

---

## Media Items

### `media_groups`

Abstract grouping for media with multiple versions.

```sql
CREATE TABLE media_groups (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    media_type_id INTEGER NOT NULL,
    name TEXT NOT NULL, -- "Blade Runner", "Star Wars: A New Hope"
    description TEXT,
    metadata JSON, -- Year, genres, etc
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (media_type_id) REFERENCES media_types(id) ON DELETE RESTRICT
);

CREATE INDEX idx_media_groups_media_type_id ON media_groups(media_type_id);
CREATE INDEX idx_media_groups_name ON media_groups(name);
```

**Metadata Example:**
```json
{
  "year": 1982,
  "genres": ["Sci-Fi", "Thriller"],
  "poster_url": "https://image.tmdb.org/...",
  "backdrop_url": "https://image.tmdb.org/..."
}
```

**Notes:**
- Used when media has multiple versions
- Single-version media can skip groups
- Groups enable aggregate stats across versions

### `media_items`

Individual pieces of media (specific version, episode, track, etc.).

```sql
CREATE TABLE media_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    media_type_id INTEGER NOT NULL,
    media_group_id INTEGER, -- NULL if single version
    parent_id INTEGER, -- For hierarchical items (episode → season)
    name TEXT NOT NULL,
    sort_name TEXT, -- For alphabetical sorting (strips "The", etc)
    description TEXT,
    metadata JSON NOT NULL, -- Type-specific data
    hierarchy_level INTEGER NOT NULL DEFAULT 0, -- 0=top, 1=child, 2=grandchild
    hierarchy_position INTEGER, -- Episode number, track number, etc
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (media_type_id) REFERENCES media_types(id) ON DELETE RESTRICT,
    FOREIGN KEY (media_group_id) REFERENCES media_groups(id) ON DELETE CASCADE,
    FOREIGN KEY (parent_id) REFERENCES media_items(id) ON DELETE CASCADE
);

CREATE INDEX idx_media_items_media_type_id ON media_items(media_type_id);
CREATE INDEX idx_media_items_media_group_id ON media_items(media_group_id);
CREATE INDEX idx_media_items_parent_id ON media_items(parent_id);
CREATE INDEX idx_media_items_name ON media_items(name);
CREATE INDEX idx_media_items_sort_name ON media_items(sort_name);
```

**Metadata Examples:**

**Movie:**
```json
{
  "version": "Director's Cut",
  "year": 1992,
  "runtime": 117,
  "rating": "R",
  "poster_url": "https://...",
  "backdrop_url": "https://...",
  "tmdb_id": 78,
  "imdb_id": "tt0083658"
}
```

**TV Episode:**
```json
{
  "season_number": 1,
  "episode_number": 5,
  "air_date": "2024-10-15",
  "runtime": 42,
  "poster_url": "https://...",
  "tvdb_id": 123456
}
```

**Music Track:**
```json
{
  "duration": 245,
  "track_number": 3,
  "disc_number": 1,
  "isrc": "USRC17607839",
  "musicbrainz_id": "abc-123-def"
}
```

**Notes:**
- `hierarchy_level`: 0=show/album, 1=season/disc, 2=episode/track
- `parent_id`: Points to parent in hierarchy (episode → season → show)
- `metadata`: Flexible JSON for type-specific fields

### `media_external_ids`

Links media to external provider IDs.

```sql
CREATE TABLE media_external_ids (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    media_item_id INTEGER NOT NULL,
    provider TEXT NOT NULL, -- "tmdb", "tvdb", "imdb", "musicbrainz"
    external_id TEXT NOT NULL, -- Provider's ID
    url TEXT, -- Direct link to provider page
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE(media_item_id, provider)
);

CREATE INDEX idx_media_external_ids_media_item_id ON media_external_ids(media_item_id);
CREATE INDEX idx_media_external_ids_provider_external_id ON media_external_ids(provider, external_id);
```

**Example Rows:**
```
media_item_id=1, provider="tmdb", external_id="78"
media_item_id=1, provider="imdb", external_id="tt0083658"
```

**Notes:**
- Multiple providers per item
- Enables cross-referencing
- Used for imports/exports

### `media_relationships`

Defines relationships between media (sequels, adaptations, etc.).

```sql
CREATE TABLE media_relationships (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    from_media_id INTEGER NOT NULL,
    to_media_id INTEGER NOT NULL,
    relationship_type TEXT NOT NULL, -- "sequel", "prequel", "adaptation", "spin-off", "remake"
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (from_media_id) REFERENCES media_items(id) ON DELETE CASCADE,
    FOREIGN KEY (to_media_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE(from_media_id, to_media_id, relationship_type)
);

CREATE INDEX idx_media_relationships_from_media_id ON media_relationships(from_media_id);
CREATE INDEX idx_media_relationships_to_media_id ON media_relationships(to_media_id);
CREATE INDEX idx_media_relationships_type ON media_relationships(relationship_type);
```

**Relationship Types:**
- `sequel` / `prequel` - Chronological order
- `adaptation` - Same story, different medium (book → movie)
- `spin-off` - Related universe
- `remake` - Different version of same story
- `shared_universe` - MCU, Star Wars, etc

**Notes:**
- Bidirectional relationships require two rows
- Enables discovery ("If you liked X, watch Y")

---

## User Library & Sessions

### `user_libraries`

Tracks which media items users have added to their library.

```sql
CREATE TABLE user_libraries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    media_item_id INTEGER NOT NULL,
    status TEXT NOT NULL, -- "plan_to_watch", "watching", "completed", "on_hold", "dropped"
    added_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    notes TEXT,
    is_favorite BOOLEAN NOT NULL DEFAULT 0,
    custom_fields JSON, -- User-defined fields
    
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE(user_id, media_item_id)
);

CREATE INDEX idx_user_libraries_user_id ON user_libraries(user_id);
CREATE INDEX idx_user_libraries_media_item_id ON user_libraries(media_item_id);
CREATE INDEX idx_user_libraries_status ON user_libraries(status);
CREATE INDEX idx_user_libraries_is_favorite ON user_libraries(is_favorite);
```

**Custom Fields Example:**
```json
{
  "watched_with": "Jane",
  "location": "Home Theater",
  "rewatch_value": 9
}
```

**Notes:**
- Status values configurable per media type
- `is_favorite` for quick filtering
- `custom_fields` for user-defined tracking

### `watch_sessions`

Rewatch cycles for series/collections.

```sql
CREATE TABLE watch_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    media_item_id INTEGER NOT NULL, -- Top-level item (show, album, book series)
    session_number INTEGER NOT NULL, -- 1st watch, 2nd rewatch, etc
    status TEXT NOT NULL, -- "in_progress", "completed", "abandoned"
    started_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    completed_at TIMESTAMP,
    notes TEXT,
    
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE(user_id, media_item_id, session_number)
);

CREATE INDEX idx_watch_sessions_user_id ON watch_sessions(user_id);
CREATE INDEX idx_watch_sessions_media_item_id ON watch_sessions(media_item_id);
CREATE INDEX idx_watch_sessions_status ON watch_sessions(status);
```

**Example:**
```
User watches Star Trek TOS:
- Session 1 (completed 2023-05-10)
- Session 2 (in_progress, started 2025-11-20)
```

**Notes:**
- Enables "I'm on my 3rd rewatch" tracking
- Kodi shows unwatched for current session
- Stats can aggregate or separate by session

---

## Interaction Tracking (Scrobbles)

### `interaction_events`

Core scrobbling table - records all watch/listen/read events.

```sql
CREATE TABLE interaction_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    media_item_id INTEGER NOT NULL,
    session_id INTEGER, -- NULL if not session-based
    event_type TEXT NOT NULL, -- "start", "progress", "pause", "finish"
    progress_data JSON, -- {percentage: 85, duration: 3600, etc}
    scrobbler_source TEXT, -- "kodi", "mpc-hc", "manual", etc
    device_name TEXT, -- "Living Room Kodi", "Desktop MPC"
    timestamp TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_private BOOLEAN NOT NULL DEFAULT 0, -- Hide from groups/public
    
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    FOREIGN KEY (session_id) REFERENCES watch_sessions(id) ON DELETE SET NULL
);

CREATE INDEX idx_interaction_events_user_id ON interaction_events(user_id);
CREATE INDEX idx_interaction_events_media_item_id ON interaction_events(media_item_id);
CREATE INDEX idx_interaction_events_session_id ON interaction_events(session_id);
CREATE INDEX idx_interaction_events_timestamp ON interaction_events(timestamp);
CREATE INDEX idx_interaction_events_user_timestamp ON interaction_events(user_id, timestamp DESC);
```

**Progress Data Examples:**

**Video:**
```json
{
  "percentage": 85,
  "duration_seconds": 3600,
  "position_seconds": 3060
}
```

**Music:**
```json
{
  "play_count": 1,
  "duration_seconds": 245,
  "skipped": false
}
```

**Book:**
```json
{
  "page": 142,
  "total_pages": 350,
  "percentage": 40.6
}
```

**Notes:**
- High-volume table - partition if needed
- `event_type` enables partial views (started but didn't finish)
- `is_private` hides from group tracking

### `currently_watching`

Real-time presence - what users are actively watching/listening to.

```sql
CREATE TABLE currently_watching (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    media_item_id INTEGER NOT NULL,
    started_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_heartbeat TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    progress_data JSON,
    device_name TEXT,
    
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE(user_id) -- Only one "currently watching" per user
);

CREATE INDEX idx_currently_watching_user_id ON currently_watching(user_id);
CREATE INDEX idx_currently_watching_last_heartbeat ON currently_watching(last_heartbeat);
```

**Cleanup Job:**
- Delete rows where `last_heartbeat < NOW() - 5 minutes`
- Runs every minute

**Notes:**
- Updated by scrobbler heartbeat
- Cleared after 5 minutes of inactivity
- Powers "currently watching" status display

---

## Groups & Social Features

### `groups`

Family units, friend groups, etc.

```sql
CREATE TABLE groups (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    description TEXT,
    group_type TEXT NOT NULL, -- "family", "friends", "custom"
    owner_user_id INTEGER NOT NULL,
    privacy TEXT NOT NULL, -- "private", "invite_only", "public"
    settings JSON, -- Permission model, etc
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (owner_user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE INDEX idx_groups_owner_user_id ON groups(owner_user_id);
CREATE INDEX idx_groups_group_type ON groups(group_type);
```

**Settings Example:**
```json
{
  "permission_model": "hierarchical",
  "admins_see_all": true,
  "members_see_members": true,
  "shared_stats": true
}
```

### `group_members`

Users in groups with roles.

```sql
CREATE TABLE group_members (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    group_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    role TEXT NOT NULL, -- "admin", "member", "limited"
    joined_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    permissions JSON, -- {can_see_history: true, can_see_stats: true}
    
    FOREIGN KEY (group_id) REFERENCES groups(id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    UNIQUE(group_id, user_id)
);

CREATE INDEX idx_group_members_group_id ON group_members(group_id);
CREATE INDEX idx_group_members_user_id ON group_members(user_id);
```

**Permissions Example:**
```json
{
  "can_see_history": true,
  "can_see_stats": true,
  "can_edit_lists": false
}
```

### `group_invitations`

Pending group invitations.

```sql
CREATE TABLE group_invitations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    group_id INTEGER NOT NULL,
    from_user_id INTEGER NOT NULL,
    to_user_id INTEGER,
    to_email TEXT, -- If inviting non-user
    role TEXT NOT NULL DEFAULT 'member',
    invite_code TEXT NOT NULL UNIQUE,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at TIMESTAMP NOT NULL,
    accepted_at TIMESTAMP,
    declined_at TIMESTAMP,
    
    FOREIGN KEY (group_id) REFERENCES groups(id) ON DELETE CASCADE,
    FOREIGN KEY (from_user_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (to_user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE INDEX idx_group_invitations_group_id ON group_invitations(group_id);
CREATE INDEX idx_group_invitations_to_user_id ON group_invitations(to_user_id);
CREATE UNIQUE INDEX idx_group_invitations_invite_code ON group_invitations(invite_code);
```

**Notes:**
- 7-day expiration default
- Can invite by username or email
- Invite code for link-based invites

---

## Lists & Collections

### `user_lists`

Custom user-created lists.

```sql
CREATE TABLE user_lists (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    description TEXT,
    is_public BOOLEAN NOT NULL DEFAULT 0,
    is_collaborative BOOLEAN NOT NULL DEFAULT 0, -- Allow others to add
    icon TEXT,
    sort_order TEXT NOT NULL DEFAULT 'manual', -- "manual", "title", "date_added"
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE INDEX idx_user_lists_user_id ON user_lists(user_id);
CREATE INDEX idx_user_lists_is_public ON user_lists(is_public);
```

### `user_list_items`

Items in lists.

```sql
CREATE TABLE user_list_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    list_id INTEGER NOT NULL,
    media_item_id INTEGER NOT NULL,
    position INTEGER NOT NULL, -- For manual sorting
    notes TEXT,
    added_by_user_id INTEGER NOT NULL,
    added_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (list_id) REFERENCES user_lists(id) ON DELETE CASCADE,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    FOREIGN KEY (added_by_user_id) REFERENCES users(id) ON DELETE SET NULL,
    UNIQUE(list_id, media_item_id)
);

CREATE INDEX idx_user_list_items_list_id ON user_list_items(list_id);
CREATE INDEX idx_user_list_items_media_item_id ON user_list_items(media_item_id);
```

---

## Ratings & Reviews

### `user_ratings`

User ratings for media.

```sql
CREATE TABLE user_ratings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    media_item_id INTEGER NOT NULL,
    rating DECIMAL(3,1), -- 1.0 to 10.0, or NULL
    rating_type TEXT NOT NULL DEFAULT 'numeric', -- "numeric", "thumbs", "stars"
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE(user_id, media_item_id)
);

CREATE INDEX idx_user_ratings_user_id ON user_ratings(user_id);
CREATE INDEX idx_user_ratings_media_item_id ON user_ratings(media_item_id);
```

**Notes:**
- Ratings are personal, not aggregated
- `rating_type` allows different scales
- NULL rating = "not rated yet"

### `user_reviews`

Text reviews.

```sql
CREATE TABLE user_reviews (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    media_item_id INTEGER NOT NULL,
    title TEXT,
    content TEXT NOT NULL,
    contains_spoilers BOOLEAN NOT NULL DEFAULT 0,
    is_public BOOLEAN NOT NULL DEFAULT 0,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE(user_id, media_item_id)
);

CREATE INDEX idx_user_reviews_user_id ON user_reviews(user_id);
CREATE INDEX idx_user_reviews_media_item_id ON user_reviews(media_item_id);
CREATE INDEX idx_user_reviews_is_public ON user_reviews(is_public);
```

**Notes:**
- One review per user per item
- Markdown support in `content`
- Spoiler warnings enforced in UI

---

## Plugin & Configuration

### `plugins`

Installed plugins (metadata scrapers, media types, etc.).

```sql
CREATE TABLE plugins (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    version TEXT NOT NULL,
    plugin_type TEXT NOT NULL, -- "scraper", "media_type", "exporter", "notifier"
    is_enabled BOOLEAN NOT NULL DEFAULT 1,
    is_system BOOLEAN NOT NULL DEFAULT 0, -- Built-in plugins
    priority INTEGER NOT NULL DEFAULT 100, -- Lower = higher priority
    settings JSON, -- Plugin-specific configuration (encrypted secrets)
    supported_media_types JSON, -- [{"type": "movie", "fields": ["title", "poster_url"]}]
    installed_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    -- Update info
    update_source TEXT, -- "github", "custom_url", "builtin"
    update_url TEXT, -- GitHub repo or custom endpoint
    update_channel TEXT DEFAULT 'stable', -- "stable", "beta", "nightly"
    auto_update BOOLEAN DEFAULT 0,
    last_update_check TIMESTAMP,
    
    -- Compatibility
    min_chronicle_version TEXT, -- "1.0.0"
    max_chronicle_version TEXT  -- "2.0.0" or NULL
);

CREATE UNIQUE INDEX idx_plugins_name ON plugins(name);
CREATE INDEX idx_plugins_plugin_type ON plugins(plugin_type);
CREATE INDEX idx_plugins_is_enabled ON plugins(is_enabled);
```

**Supported Media Types Example:**
```json
[
  {
    "media_type": "movie",
    "fields": ["title", "description", "poster_url", "rating", "cast"],
    "default_priority": 1
  },
  {
    "media_type": "tv",
    "fields": ["title", "description", "poster_url", "network"],
    "default_priority": 1
  }
]
```

**Settings Example (TMDB):**
```json
{
  "api_key": "encrypted:AES256:abc123...",
  "language": "en-US",
  "include_adult": false,
  "rate_limit": 40
}
```

**Notes:**
- `is_system` prevents accidental deletion of core plugins
- `priority` determines default scraper order
- `settings` JSON encrypted for sensitive data (API keys)
- `supported_media_types` auto-populates scraper priorities on install
- Each plugin type has different settings schema

### `plugin_versions`

Track plugin version history for rollback capability.

```sql
CREATE TABLE plugin_versions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    plugin_name TEXT NOT NULL,
    version TEXT NOT NULL,
    file_path TEXT NOT NULL, -- /plugins/backups/plugin-v1.2.0.dll
    installed_at TIMESTAMP NOT NULL,
    uninstalled_at TIMESTAMP, -- When rolled back or replaced
    
    FOREIGN KEY (plugin_name) REFERENCES plugins(name) ON DELETE CASCADE,
    UNIQUE(plugin_name, version)
);

CREATE INDEX idx_plugin_versions_name_version ON plugin_versions(plugin_name, version DESC);
```

**Notes:**
- Keeps last 3 versions for rollback
- Enables "Rollback to v1.2.0" functionality
- Automatic cleanup of old versions

### `app_settings`

Global application settings.

```sql
CREATE TABLE app_settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL, -- JSON string
    description TEXT,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);
```

**Example Rows:**
```
key="app.name", value="Chronicle"
key="app.port", value="8080"
key="app.timezone", value="UTC"
key="backup.retention_days", value="30"
key="updates.channel", value="stable"
```

**Notes:**
- Simple key-value store
- Value always JSON for consistency
- No complex queries needed (small table)

---

## Statistics & Aggregates

### `user_stats_cache`

Pre-computed statistics for performance.

```sql
CREATE TABLE user_stats_cache (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    stat_key TEXT NOT NULL, -- "total_episodes_watched", "total_time_minutes"
    stat_value TEXT NOT NULL, -- JSON value
    media_type_id INTEGER, -- NULL for global stats
    computed_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (media_type_id) REFERENCES media_types(id) ON DELETE CASCADE,
    UNIQUE(user_id, stat_key, media_type_id)
);

CREATE INDEX idx_user_stats_cache_user_id ON user_stats_cache(user_id);
CREATE INDEX idx_user_stats_cache_media_type_id ON user_stats_cache(media_type_id);
```

**Example Stats:**
```
stat_key="total_episodes_watched", stat_value="3452"
stat_key="total_time_minutes", stat_value="124800"
stat_key="most_watched_genre", stat_value="Sci-Fi"
```

**Notes:**
- Regenerated nightly or on-demand
- Improves dashboard load times
- Can be invalidated and rebuilt

---

## Notifications

### `notifications`

User notifications (new episodes, friend activity, etc.).

```sql
CREATE TABLE notifications (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    notification_type TEXT NOT NULL, -- "new_episode", "friend_request", "update_available"
    title TEXT NOT NULL,
    message TEXT NOT NULL,
    link_url TEXT, -- Optional: Click to navigate
    is_read BOOLEAN NOT NULL DEFAULT 0,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE INDEX idx_notifications_user_id ON notifications(user_id);
CREATE INDEX idx_notifications_is_read ON notifications(is_read);
CREATE INDEX idx_notifications_created_at ON notifications(created_at DESC);
```

**Notification Types:**
- `new_episode` - Show you're watching has new episode
- `friend_request` - Someone sent friend request
- `group_invitation` - Invited to group
- `update_available` - New Chronicle version
- `backup_failed` - Backup job failed

---

## Backup & Import/Export

### `backup_history`

Tracks backup operations.

```sql
CREATE TABLE backup_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    backup_type TEXT NOT NULL, -- "manual", "scheduled", "pre_update"
    file_path TEXT NOT NULL,
    file_size_bytes INTEGER NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    deleted_at TIMESTAMP, -- When auto-cleanup removed it
    checksum TEXT -- SHA256 of backup file
);

CREATE INDEX idx_backup_history_created_at ON backup_history(created_at DESC);
```

**Notes:**
- Auto-cleanup based on retention policy
- `deleted_at` tracks when removed
- Checksum for integrity verification

### `import_jobs`

Tracks import operations (from Trakt, SIMKL, etc.).

```sql
CREATE TABLE import_jobs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    source TEXT NOT NULL, -- "trakt", "simkl", "csv"
    status TEXT NOT NULL, -- "pending", "running", "completed", "failed"
    total_items INTEGER,
    processed_items INTEGER NOT NULL DEFAULT 0,
    failed_items INTEGER NOT NULL DEFAULT 0,
    error_log JSON, -- [{item: "...", error: "..."}]
    started_at TIMESTAMP,
    completed_at TIMESTAMP,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE INDEX idx_import_jobs_user_id ON import_jobs(user_id);
CREATE INDEX idx_import_jobs_status ON import_jobs(status);
```

---

## Audit Log

### `audit_log`

Security audit trail.

```sql
CREATE TABLE audit_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER,
    action TEXT NOT NULL, -- "login", "create_token", "delete_user", "change_password"
    entity_type TEXT, -- "user", "media_item", "group"
    entity_id INTEGER,
    ip_address TEXT,
    user_agent TEXT,
    details JSON, -- Action-specific details
    timestamp TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_audit_log_user_id ON audit_log(user_id);
CREATE INDEX idx_audit_log_action ON audit_log(action);
CREATE INDEX idx_audit_log_timestamp ON audit_log(timestamp DESC);
```

**Notes:**
- Logs security-relevant events
- Helps investigate suspicious activity
- Retention: 90 days default

---

## Database Maintenance

### Indexes Summary

**High-Priority Indexes (Performance Critical):**
- `interaction_events(user_id, timestamp)` - User history queries
- `media_items(media_type_id, parent_id)` - Hierarchy traversal
- `user_libraries(user_id, status)` - Library filtering
- `api_tokens(token)` - Authentication lookups

**Regular Maintenance:**
- `VACUUM` monthly (SQLite) - Reclaim space
- `ANALYZE` weekly - Update query planner stats
- Archive old `interaction_events` > 2 years
- Clean up `currently_watching` stale entries

### Backup Strategy

**Automated Backups:**
- Before every update
- Daily (3am, off-peak)
- Retention: Last 30 days

**Manual Backups:**
- User-initiated via UI
- Before major changes
- Never auto-deleted

**Backup Locations:**
- `/data/backups/` - Local storage
- Optional: Remote (S3, NAS, etc)

---

## Migration Examples

### Migration 001: Initial Schema

**Up Migration (`001_initial_schema.sql`):**
```sql
-- Create users table
CREATE TABLE users (...);

-- Create media_types table
CREATE TABLE media_types (...);

-- etc...

INSERT INTO schema_version (version, description) 
VALUES (1, 'Initial schema');
```

**Down Migration (`001_initial_schema.down.sql`):**
```sql
-- Reverse order
DROP TABLE IF EXISTS users;
DROP TABLE IF EXISTS media_types;
-- etc...

DELETE FROM schema_version WHERE version = 1;
```

### Migration 002: Add Groups

**Up Migration:**
```sql
-- Add groups tables
CREATE TABLE groups (...);
CREATE TABLE group_members (...);
CREATE TABLE group_invitations (...);

INSERT INTO schema_version (version, description) 
VALUES (2, 'Add groups and family tracking');
```

**Down Migration:**
```sql
DROP TABLE IF EXISTS group_invitations;
DROP TABLE IF EXISTS group_members;
DROP TABLE IF EXISTS groups;

DELETE FROM schema_version WHERE version = 2;
```

### Migration 003: Add Session Support

**Up Migration:**
```sql
-- Add watch_sessions table
CREATE TABLE watch_sessions (...);

-- Add session_id to interaction_events
ALTER TABLE interaction_events ADD COLUMN session_id INTEGER REFERENCES watch_sessions(id);
CREATE INDEX idx_interaction_events_session_id ON interaction_events(session_id);

INSERT INTO schema_version (version, description) 
VALUES (3, 'Add rewatch session tracking');
```

**Down Migration:**
```sql
-- SQLite doesn't support DROP COLUMN easily
-- Would need to recreate table without column
-- Or leave column but mark deprecated

DROP TABLE IF EXISTS watch_sessions;

DELETE FROM schema_version WHERE version = 3;
```

---

## PostgreSQL-Specific Optimizations

When using PostgreSQL instead of SQLite:

### Partitioning

**Partition `interaction_events` by Year:**
```sql
CREATE TABLE interaction_events (
    -- columns...
) PARTITION BY RANGE (timestamp);

CREATE TABLE interaction_events_2024 PARTITION OF interaction_events
    FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');

CREATE TABLE interaction_events_2025 PARTITION OF interaction_events
    FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
```

### Full-Text Search

**Add Full-Text Search for Media:**
```sql
ALTER TABLE media_items ADD COLUMN search_vector tsvector;

CREATE INDEX idx_media_items_search ON media_items USING GIN(search_vector);

-- Update trigger
CREATE FUNCTION media_search_update() RETURNS trigger AS $$
BEGIN
    NEW.search_vector := to_tsvector('english', COALESCE(NEW.name,'') || ' ' || COALESCE(NEW.description,''));
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER media_search_update_trigger 
BEFORE INSERT OR UPDATE ON media_items
FOR EACH ROW EXECUTE FUNCTION media_search_update();
```

### JSONB Instead of JSON

PostgreSQL's JSONB is more efficient:
```sql
-- Instead of: metadata JSON
-- Use: metadata JSONB

-- Enables indexed queries:
CREATE INDEX idx_media_items_metadata_year ON media_items((metadata->>'year'));
```

---

## Additional Tables

### Scraper Configuration

**`scraper_priorities` - Field-level scraper assignment:**

```sql
CREATE TABLE scraper_priorities (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    media_type_id INTEGER NOT NULL,
    plugin_id INTEGER NOT NULL,
    metadata_field TEXT, -- NULL=all fields, or specific like "poster_url"
    priority INTEGER NOT NULL,
    is_enabled BOOLEAN DEFAULT 1,
    fallback_behavior TEXT DEFAULT 'continue',
    
    FOREIGN KEY (media_type_id) REFERENCES media_types(id),
    FOREIGN KEY (plugin_id) REFERENCES plugins(id),
    UNIQUE(media_type_id, plugin_id, metadata_field)
);
```

### Background Jobs

**`scheduled_jobs` - Job configuration:**

```sql
CREATE TABLE scheduled_jobs (
    job_name TEXT PRIMARY KEY,
    schedule TEXT NOT NULL, -- Cron expression
    is_enabled BOOLEAN DEFAULT 1,
    last_run_at TIMESTAMP,
    last_run_status TEXT,
    next_run_at TIMESTAMP,
    settings JSON
);
```

**`job_history` - Execution history:**

```sql
CREATE TABLE job_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_name TEXT NOT NULL,
    started_at TIMESTAMP NOT NULL,
    completed_at TIMESTAMP,
    status TEXT NOT NULL,
    error TEXT,
    details JSON
);
```

### File Management

**`media_file_locations` - File path tracking (optional):**

```sql
CREATE TABLE media_file_locations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    media_item_id INTEGER NOT NULL,
    file_path TEXT NOT NULL, -- UNC, local, or URL
    file_type TEXT, -- "video", "subtitle", "audio"
    verified_at TIMESTAMP,
    is_missing BOOLEAN DEFAULT 0,
    
    FOREIGN KEY (media_item_id) REFERENCES media_items(id)
);
```

**Note:** Chronicle stores paths only, does NOT serve files.

### Dashboard & UI

**`dashboard_widgets` - User dashboard configuration:**

```sql
CREATE TABLE dashboard_widgets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    widget_type TEXT NOT NULL,
    media_type_id INTEGER,
    position INTEGER NOT NULL,
    size TEXT DEFAULT 'medium',
    settings JSON,
    is_enabled BOOLEAN DEFAULT 1,
    
    FOREIGN KEY (user_id) REFERENCES users(id),
    FOREIGN KEY (media_type_id) REFERENCES media_types(id)
);
```

**`ui_config` - Global UI configuration:**

```sql
CREATE TABLE ui_config (
    component_name TEXT PRIMARY KEY,
    layout JSON NOT NULL,
    styles JSON,
    is_enabled BOOLEAN DEFAULT 1,
    is_system BOOLEAN DEFAULT 1
);
```

**`ui_config_backups` - UI configuration backups:**

```sql
CREATE TABLE ui_config_backups (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    backup_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    config_snapshot JSON NOT NULL,
    created_by TEXT,
    description TEXT
);
```

### Webhooks

**`webhooks` - External integrations:**

```sql
CREATE TABLE webhooks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    url TEXT NOT NULL,
    events JSON NOT NULL, -- ["scrobble", "series_completed"]
    is_enabled BOOLEAN DEFAULT 1,
    secret TEXT,
    
    FOREIGN KEY (user_id) REFERENCES users(id)
);
```

### Plugin Management

**`plugin_versions` - Version history for rollback:**

```sql
CREATE TABLE plugin_versions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    plugin_name TEXT NOT NULL,
    version TEXT NOT NULL,
    file_path TEXT NOT NULL,
    installed_at TIMESTAMP NOT NULL,
    uninstalled_at TIMESTAMP,
    
    FOREIGN KEY (plugin_name) REFERENCES plugins(name),
    UNIQUE(plugin_name, version)
);
```

**`scraper_stats` - Performance tracking:**

```sql
CREATE TABLE scraper_stats (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    plugin_id INTEGER NOT NULL,
    media_type_id INTEGER NOT NULL,
    success_count INTEGER DEFAULT 0,
    failure_count INTEGER DEFAULT 0,
    avg_response_time_ms INTEGER,
    last_success_at TIMESTAMP,
    last_failure_at TIMESTAMP,
    
    FOREIGN KEY (plugin_id) REFERENCES plugins(id),
    FOREIGN KEY (media_type_id) REFERENCES media_types(id),
    UNIQUE(plugin_id, media_type_id)
);
```

### Federation (Phase 4+)

**`federated_instances` - Other Chronicle instances:**

```sql
CREATE TABLE federated_instances (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_url TEXT NOT NULL UNIQUE,
    instance_name TEXT,
    public_key TEXT,
    is_trusted BOOLEAN DEFAULT 0,
    added_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
```

**`federated_friends` - Cross-instance connections:**

```sql
CREATE TABLE federated_friends (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    local_user_id INTEGER NOT NULL,
    remote_instance_id INTEGER NOT NULL,
    remote_username TEXT NOT NULL,
    permissions JSON,
    status TEXT DEFAULT 'pending',
    
    FOREIGN KEY (local_user_id) REFERENCES users(id),
    FOREIGN KEY (remote_instance_id) REFERENCES federated_instances(id),
    UNIQUE(local_user_id, remote_instance_id, remote_username)
);
```

---

## Estimated Row Counts

For planning purposes, estimated row counts for active user:

| Table | Single User (1 year) | Power User (1 year) | Multi-User (100 users) |
|-------|----------------------|---------------------|-------------------------|
| users | 1 | 1 | 100 |
| media_items | 500 | 5,000 | 50,000 |
| interaction_events | 2,000 | 20,000 | 2,000,000 |
| user_libraries | 200 | 2,000 | 20,000 |
| user_stats_cache | 50 | 100 | 10,000 |

**Storage Estimates:**
- SQLite: 100MB per active user per year
- PostgreSQL: 80MB per active user per year (better compression)

---

## Schema Evolution Guidelines

**Safe Changes (Can Deploy Anytime):**
- Add new table
- Add new column with DEFAULT
- Add new index
- Create new view

**Risky Changes (Requires Coordination):**
- Remove column (deprecate first)
- Rename column (alias then migrate)
- Change data type (create new, migrate, drop old)
- Remove table (ensure not in use)

**Breaking Changes (Major Version Only):**
- Remove deprecated columns
- Change primary keys
- Restructure relationships

---

## Document Version

**Version:** 1.0  
**Last Updated:** 2026-01-11  
**Author:** Michael Beck with assistance from Anthropic Claude  
**Status:** Design Phase - Not Yet Implemented

**Related Documents:**
- Chronicle_Design_Document.md - Overall architecture and features
