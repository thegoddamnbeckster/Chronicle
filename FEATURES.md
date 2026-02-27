# Chronicle Features

**Version:** 1.0  
**Last Updated:** 2026-01-12  
**Author:** Michael Beck with Anthropic Claude

---

## Core Features

### 1. Universal Media Tracking

Chronicle tracks ANY type of media through a flexible, plugin-based system.

**Built-In Media Types:**
- Movies
- TV Shows
- Music (albums, tracks)
- Books
- Anime
- Podcasts

**Custom Media Types** (user-definable):
- Board games
- Video games
- Audiobooks
- Comics/Manga
- Cooking recipes
- Fitness workouts
- *Anything with hierarchical structure*

**Key Capabilities:**
- Hierarchical organization (Show → Season → Episode)
- Custom terminology per type ("watch" vs "listen" vs "read" vs "play")
- Progress tracking (episodes watched, pages read, tracks heard)
- Status tracking (watching, completed, dropped, on-hold, plan-to-watch)
- Rating system (1-10, 5-star, or custom)
- Personal notes and reviews

---

### 2. Rewatch Sessions

**The Problem Chronicle Solves:**

Existing trackers (Trakt, SIMKL) don't properly handle rewatching. When you rewatch a series, it just marks episodes as "watched again" but doesn't track complete viewing cycles.

**Chronicle's Solution:**

Session-based tracking that creates distinct viewing cycles:

```
Star Trek: The Original Series
├── Session 1 (Completed - 2023-05-12)
│   ├── All 79 episodes watched
│   └── Started: 2023-01-15, Finished: 2023-05-12
├── Session 2 (In Progress)
│   ├── Currently on S02E10
│   └── Started: 2025-11-20
└── Session 3 (Planned)
```

**Benefits:**
- Track complete viewing cycles separately
- See watch history per session
- Statistics: "5 complete rewatches + 1 in progress"
- Kodi integration: Shows episodes as "unwatched" for current session
- Historical record: When you watched each cycle

**Use Cases:**
- Annual rewatches (Lord of the Rings every December)
- Introducing friends/family to shows you love
- Background shows you cycle through
- Comfort shows during difficult times

---

### 3. Version Management

**The Problem:**

Media often exists in multiple versions:
- Theatrical Cut vs Director's Cut vs Extended Edition
- Remastered editions
- Fan edits (Despecialized Star Wars, Tolkien Edit)
- Different releases (4K HDR, Blu-ray, DVD)

Current trackers force you to choose one and lose tracking of others.

**Chronicle's Solution:**

Hierarchical version system:

```
Blade Runner (Media Group)
├── Theatrical Cut (1982) - Watched 2x
├── Director's Cut (1992) - Watched 5x ⭐ Preferred
├── Final Cut (2007) - Watched 3x
└── Workprint (1982) - Watched 1x
```

**Features:**
- Track each version independently
- Mark preferred version
- Aggregate stats across all versions
- OR separate stats per version
- Community version registry (share fan edit info)

**Metadata Per Version:**
- Runtime differences
- Added/removed scenes
- Quality (4K, 1080p, etc.)
- Source (Blu-ray, streaming, etc.)
- Personal notes

---

### 4. Scrobbling

Automatic tracking from media players.

**Supported Players:**
- **Kodi** - Native addon (Phase 1)
- **Plex** - Webhook integration (Phase 2)
- **Jellyfin** - Plugin (Phase 2)
- **Emby** - Plugin (Phase 2)
- **VLC** - Extension (Phase 3)
- **MPC-HC/BE** - Plugin (Phase 3)
- **mpv** - Lua script (Phase 3)
- **Web browsers** - Extension (Phase 4)

**Scrobble Events:**
- **Start** - Beginning playback
- **Pause** - Paused
- **Resume** - Resumed from pause
- **Stop** - Stopped playback
- **Complete** - Finished (>80% watched)

**Smart Detection:**
- Identifies media by filename/metadata
- Handles various naming schemes
- Fallback to manual selection if uncertain
- Duplicate detection (don't double-scrobble)

**Currently Watching:**
Real-time presence:
- "Michael is watching Blade Runner (32% complete, 19m remaining)"
- Updates every 30 seconds
- Clears after 5 minutes of inactivity
- Privacy controls (public/friends/private)

---

### 5. Multi-User & Family Groups

**User Types:**
- **Admin** - Full control
- **User** - Standard access
- **Limited** - View-only or restricted

**Family Units:**
- Parent/guardian accounts see all family activity
- Children accounts see only their own
- Shared statistics ("Family watched 47 episodes this week")
- Privacy controls per member

**Friend Groups:**
- Equal access between members
- Shared activity feed
- Group statistics and leaderboards
- "Watched together" tracking

**Use Cases:**
- Parents monitoring children's viewing
- Couples sharing tracking
- Roommates tracking together
- Friend groups competing/comparing

**Permissions:**
- View history
- Add to library
- Scrobble
- Modify metadata
- Admin settings

---

### 6. Statistics & Analytics

**Personal Stats:**
- Total watch time (all time, yearly, monthly, weekly)
- Episodes/movies watched per day/week/month
- Most watched genres
- Most watched actors/directors
- Completion rate (% of started media completed)
- Average rating given
- Longest binge session
- Most rewatched content

**Comparative Stats:**
- Compare with friends
- Group statistics
- Leaderboards (most watched, most diverse, etc.)
- "Who's watching what" activity feed

**Trends:**
- Watch time over time (graphs)
- Genre preferences over time
- Seasonal viewing patterns
- Year-in-review summaries

**Export:**
- CSV, JSON, Excel
- Infographics (Year in Review)
- Share on social media

---

### 7. Customizable Dashboard

Widget-based home page that users configure to their preferences.

**Built-In Widgets:**

1. **Recent Activity** - Last 10 things watched/listened to
2. **Continue Watching** - In-progress media
3. **Upcoming Releases** - New episodes/albums/books this week
4. **Calendar View** - Release schedule
5. **Quick Stats** - Watch time this week/month
6. **Top Rated** - Your highest-rated media
7. **Random Recommendation** - Surprise me
8. **Currently Watching (Friends)** - What friends are watching now
9. **Activity Feed** - Friend activity
10. **Trending** - Popular with your friend group

**Media-Type Specific Widgets** (from plugins):
- TV: "Next episode to watch"
- Music: "Recently added albums"
- Books: "Reading progress"
- Games: "Backlog tracker"

**Customization:**
- Drag & drop arrangement
- Resize widgets (small/medium/large/full)
- Configure widget settings
- Enable/disable individual widgets
- Multiple dashboard layouts (switch between them)

**Example Layout:**

```
┌─────────────────┬─────────────────┐
│ Continue        │ Upcoming        │
│ Watching        │ Releases        │
│                 │                 │
├─────────────────┴─────────────────┤
│ Recent Activity                   │
│                                   │
├──────────────────┬────────────────┤
│ Quick Stats      │ Friends        │
│                  │ Activity       │
└──────────────────┴────────────────┘
```

---

### 8. Advanced Search & Filtering

**Search Capabilities:**
- Full-text search (titles, descriptions, people)
- Fuzzy matching (typo-tolerant)
- Advanced filters (genre, year, rating, status)
- Boolean operators (AND, OR, NOT)
- Saved searches

**Filter Examples:**
- "All sci-fi movies from 2020-2024 rated >8"
- "TV shows I'm currently watching but haven't updated in >30 days"
- "Books by Stephen King I haven't read yet"
- "Anime rated 10/10 by me"

**Saved Searches:**
- Save complex queries
- Share with friends
- Auto-update results
- Export to RSS

**Smart Lists:**
- Dynamic collections based on rules
- Auto-update as library changes
- Examples: "Top 100 Movies", "Unwatched Pixar Films", "2025 Releases"

---

### 9. Import & Export

**Import From:**
- **Trakt** - Full history, ratings, lists
- **SIMKL** - Full history, ratings
- **Letterboxd** - Movie diary, ratings
- **Goodreads** - Books read, ratings
- **MyAnimeList** - Anime/manga lists
- **CSV/JSON** - Generic import

**Export To:**
- **CSV** - All data
- **JSON** - Complete database dump
- **XML** - Compatible with other services
- **OPML** - For podcast managers
- **iCal** - Calendar of upcoming releases

**Sync:**
- Two-way sync with supported services (future)
- Automatic backup to cloud storage (future)

---

### 10. Metadata Management

**Automatic Metadata:**
- Title, description, release dates
- Poster and backdrop images
- Cast and crew
- Genres and tags
- External ratings (IMDB, TMDB, etc.)
- Runtime, episode counts

**Manual Override:**
- Edit any field
- Add custom fields
- Upload custom artwork
- Merge duplicate entries
- Split incorrect matches

**Multi-Source Aggregation:**
Priority-based scraping:
1. TMDB for basic info
2. IMDB for ratings
3. FanArt.tv for high-quality images
4. Custom sources for specialized data

**Metadata Refresh:**
- Manual refresh
- Automatic periodic refresh (new episodes, updated info)
- Batch refresh for entire library

---

### 11. Calendar & Release Tracking

**Upcoming Releases:**
- New episodes this week
- New movies releasing
- Album drops
- Book releases

**Historical Calendar:**
- What you watched each day
- "On this day" feature
- Heat map visualization

**Subscriptions:**
- Track shows you follow
- Get notifications for new episodes
- Export to iCal
- Subscribe to friends' calendars

**Notifications:**
- Email
- Push notifications (mobile app)
- Webhook (Discord, Slack, etc.)
- RSS feed

---

### 12. Collections & Lists

**Personal Lists:**
- Watch later
- Favorites
- Top 100 Movies
- Comfort shows
- Guilty pleasures
- Custom named lists

**Collaborative Lists:**
- Shared with family/friends
- Voting system
- Comments per item
- "Movie night" list with group

**Smart Collections:**
- Auto-populated based on rules
- "All Nolan films"
- "80s sci-fi rated >7"
- Updates automatically

**List Sharing:**
- Public URL
- Import friend's list
- Discover popular lists

---

### 13. Recommendations

**Based On:**
- Watch history
- Ratings
- Genres preferred
- Similar users' preferences
- Currently trending

**Recommendation Types:**
- "Because you watched X"
- "Popular with people like you"
- "New releases in your favorite genres"
- "Hidden gems"
- "Completing the series" (watch next episode)

**Filters:**
- Only from my library
- Include external suggestions
- Minimum rating threshold
- Exclude genres

---

### 14. Privacy & Data Control

**Privacy Levels:**
- **Private** - Only you see your data
- **Friends** - Friends can see activity
- **Public** - Anyone can view

**Granular Controls:**
- Per-media privacy ("hide guilty pleasure")
- Activity feed visibility
- Currently watching visibility
- Statistics visibility

**Data Ownership:**
- Your data stays on your server
- Export anytime
- Delete account = delete all data
- No third-party tracking
- No ads

**Sharing Controls:**
- Who can see your profile
- Who can send friend requests
- Who can view your lists
- Who can see currently watching

---

### 15. API & Integrations

**REST API:**
- Full access to all features
- JWT authentication
- Rate limiting
- Webhook support
- Swagger documentation

**Webhooks:**
Trigger external services when:
- Media watched
- Rating added
- New episode released
- Friend activity

**Integrations:**
- **Discord** - Rich presence, bot commands
- **Home Assistant** - Automation triggers
- **Plex/Jellyfin** - Sync libraries
- **Letterboxd** - Cross-post ratings
- **Goodreads** - Sync book progress

---

### 16. Mobile Support

**Phase 1: Responsive Web**
- Mobile-optimized interface
- Touch-friendly navigation
- Quick scrobble from phone

**Phase 4: Native Apps**
- iOS and Android apps
- Offline caching
- Push notifications
- Widgets (iOS)
- Quick actions

---

### 17. File Location Tracking

**Purpose:**
Track where media physically exists on disk.

**Storage:**
```sql
CREATE TABLE media_file_locations (
    media_item_id INTEGER,
    file_path TEXT,  -- UNC, local, URL
    file_type TEXT,  -- video, audio, subtitle
    quality TEXT,    -- 1080p, 4K, FLAC
    size_bytes INTEGER
);
```

**Examples:**
- `\\nas\movies\Blade Runner (1982).mkv` (UNC)
- `/mnt/media/movies/Blade Runner.mkv` (Linux)
- `D:\Movies\Blade Runner.mkv` (Windows)

**Use Cases:**
- Know where files are stored
- Verify files still exist (scheduled job)
- Integration with file managers
- Link to Tiny Media Manager
- Network share management

**File Verification Job:**
- Runs daily
- Checks if paths exist
- Flags missing files
- Updates last verified timestamp

**Note:** Chronicle stores paths only. File serving/sharing is through plugins (not implemented by default).

---

### 18. Backup & Restore

**Automatic Backups:**
- Before updates
- Daily at 1am (configurable)
- Before major operations

**Backup Contains:**
- Complete database
- Configuration files
- Plugin settings
- User preferences

**Backup Locations:**
- Local directory
- Network share (SMB)
- Cloud storage (future)

**Restore:**
- One-click restore
- Preview before restoring
- Selective restore (specific tables)

**Retention:**
- Keep last 30 days (configurable)
- Keep one per month (last 12 months)
- Manual backups kept forever

---

### 19. Notifications

**Notification Types:**
- New episode released
- Friend started watching
- Update available
- Backup completed
- Error/warning events

**Delivery Methods:**
- In-app notifications
- Email
- Push (mobile app)
- Webhook (Discord, Slack, custom)

**Configuration:**
- Per-notification-type settings
- Quiet hours
- Grouped notifications
- Priority levels

---

### 20. Federation (Phase 4)

**Connect Chronicle Instances:**

Allow Chronicle servers to communicate:

**Use Cases:**
- Compare stats with friends on different servers
- Share recommendations
- Follow friend's activity across instances
- Federated search

**Privacy:**
- Opt-in per instance
- Control what you share
- Revoke access anytime

**Protocol:**
- RESTful API between instances
- Public key verification
- Rate limiting
- Optional: ActivityPub compatibility

---

## Feature Comparison Matrix

| Feature | Chronicle | Trakt | SIMKL | Plex | Jellyfin |
|---------|-----------|-------|-------|------|----------|
| Self-hosted | ✅ | ❌ | ❌ | ✅ | ✅ |
| Open source | ✅ | ❌ | ❌ | ❌ | ✅ |
| Rewatch sessions | ✅ | ❌ | ❌ | ⚠️ | ⚠️ |
| Version management | ✅ | ❌ | ❌ | ❌ | ❌ |
| Custom media types | ✅ | ❌ | ❌ | ❌ | ❌ |
| Plugin scrapers | ✅ | ❌ | ❌ | ⚠️ | ⚠️ |
| Multi-user | ✅ | ✅ | ✅ | ✅ | ✅ |
| API | ✅ | ✅ | ✅ | ✅ | ✅ |
| Currently watching | ✅ | ❌ | ❌ | ✅ | ✅ |
| Statistics | ✅ | ✅ | ✅ | ✅ | ✅ |
| Universal media | ✅ | ❌ | ✅ | ❌ | ❌ |
| Data ownership | ✅ | ❌ | ❌ | ✅ | ✅ |
| Plays media | ❌ | ❌ | ❌ | ✅ | ✅ |
| File management | ❌ | ❌ | ❌ | ✅ | ✅ |

---

## Phase Breakdown

### Phase 1: MVP (v0.1 - v0.5)
- [ ] Basic scrobbling
- [ ] User accounts
- [ ] Single media type (TV)
- [ ] Simple web UI
- [ ] SQLite database
- [ ] Manual add/edit

### Phase 2: Core (v0.6 - v1.0)
- [ ] Multiple media types
- [ ] Plugin system
- [ ] Kodi scrobbler
- [ ] Version management
- [ ] Rewatch sessions
- [ ] Statistics
- [ ] Docker support

### Phase 3: Advanced (v1.1 - v2.0)
- [ ] Multi-user
- [ ] Family groups
- [ ] Custom media types
- [ ] Advanced search
- [ ] Import from Trakt/SIMKL
- [ ] Webhooks
- [ ] Calendar

### Phase 4: Ecosystem (v2.1+)
- [ ] Mobile apps
- [ ] Federation
- [ ] Recommendations (ML)
- [ ] Community features
- [ ] Plugin marketplace

---

**Document Status:** Complete  
**Next Review:** Implementation Start
