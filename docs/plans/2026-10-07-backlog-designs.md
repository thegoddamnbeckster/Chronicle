# Backlog designs — 2026-10-07

Design proposals for the backlog items that had no implementation. Nothing here is built yet.
Each section ends with the decisions it needs from the owner. Recommended defaults are stated
so a "go with your defaults" answer is enough.

Ground rules applied throughout (from CLAUDE.md and standing project preferences):
- Behaviour is configured in the database, never hardcoded per media type.
- Lossless ingestion: nothing a provider or scan reports is dropped.
- Everything Chronicle writes (including scratch files) stays in the install folder.
- New recurring work is an `IScheduledTask`, so it appears on Settings → Background Tasks with
  last/next run and Run Now for free.
- Dev and prod are the same live database, so destructive operations need a typed confirmation.

---

## 1. Database backups, maintenance and size

### Findings
- SQLite is the default (WAL mode, set at startup in `Program.cs`); Postgres is selectable.
- Migrations apply automatically at startup. `__EFMigrationsHistory` records what is applied.
- `background_tasks` already stores last run, next run and result per task.
- The only backups today are ad-hoc `.db` copies made by hand.

### Backup
- **Method:** `VACUUM INTO '<file>'` gives a consistent snapshot while the API is running
  (a plain file copy of a WAL database can be torn). The snapshot is zipped.
- **Location:** `<install>\backups\db\chronicle-yyyyMMdd-HHmmss.zip`. The VACUUM scratch file
  is written in the same folder and deleted afterwards (no temp/ProgramData).
- **Retention:** keep the newest 10 (setting `backup.retain`, default 10). Pre-restore safety
  backups are kept separately and are not counted.
- **Triggers:** task `database_backup` (default daily 02:00) plus a "Back up now" button.
- **Zip contents:** the `.db`, plus `manifest.json` (app version, latest migration id, created
  time, row counts of key tables, SHA-256 of the db). The manifest makes validation cheap.

### Restore
1. User uploads a zip, or picks an existing backup.
2. Validate without touching the live DB: manifest present and hash matches; open the extracted
   db read-only; `PRAGMA integrity_check` returns `ok`; its latest migration is known to this
   build. A backup from a newer build is refused; an older one is accepted and migrates on
   restart.
3. Typed confirmation ("RESTORE") because dev is prod.
4. Write a safety backup of the current DB, stage the validated file as
   `chronicle.db.restore-pending`, and ask the host to restart.
5. At startup, before any DbContext opens, `Program.cs` sees the pending file, swaps it in
   (keeping the old one as `chronicle.db.pre-restore-<timestamp>`), then migrates normally.
6. The UI polls `/api/health`, then reloads.

Open point: step 4 needs something to restart the process. Under `RunTestEnvironment.ps1` it
is a script; an installed service would be the service manager. Proposal: the API exits with a
dedicated exit code, and the supervisor (script or service recovery setting) restarts on it.
I need to know how you run Chronicle outside dev.

Postgres: `pg_dump` is not bundled, so the page says so and offers no backup there. Backups are
scoped to SQLite for now.

### Maintenance (SQLite)
| Task | Statements | Default |
|---|---|---|
| Light | `PRAGMA optimize`, `ANALYZE`, `PRAGMA wal_checkpoint(TRUNCATE)` | weekly |
| Heavy | `REINDEX`, `VACUUM` (needs free disk about equal to the DB size, takes an exclusive lock) | monthly, manual-friendly |

Both are `IScheduledTask`s, so last-run times need no new storage. Heavy checks free disk space
first and refuses with a clear message if short.

### Size monitoring
- Page shows file size, WAL size, free pages (reclaimable by VACUUM), and the largest tables.
- Warning thresholds are `app_settings` (`db.warn_size_mb`), shown as a banner on the Database
  page and as a notification (section 4).
- SQLite itself is not near a hard limit at Chronicle's scale; the practical limits are VACUUM
  space and query time. So "free space" tools (VACUUM, purge old logs/refresh-log rows) come
  first.
- **SQLite → Postgres migration** is a separate, larger project (row-copy tool with
  verification). Recommend deferring until size monitoring shows a real need.

### UI
New Settings → Database page: status card (size, free pages, last backup, last maintenance),
backup list (download, restore, delete), upload box, "Back up now", "Run maintenance", the
retention and threshold settings.

### Decisions needed
1. How is Chronicle restarted in production (script, Windows service, Docker)?
2. Backup time and the 10-backup default OK?
3. Defer the Postgres migration tool?

---

## 2. Password reset

### Findings
- Admin set-password exists (`UsersController.ResetUserPassword`). There is no self-service
  path, no token storage and no SMTP settings.
- Users already have free-form contacts (`user_contacts`, `kind = "email"`, `IsPrimary`).
- JWTs are stateless (24h); `DeactivatedUserCache` blocks removed users immediately.

### Flow A — email (when SMTP is configured)
1. Login page "Forgot password?" → enter username or email.
2. `POST /auth/forgot-password` always returns the same generic 200 (no account enumeration).
   If the user exists and has a primary email contact, send a link.
3. Link opens `/reset-password#token=…`. The token goes in the URL fragment so it never reaches
   server logs or referrers.
4. `POST /auth/reset-password {token, newPassword}` sets the password.

### Flow B — admin-issued token (no SMTP)
Admin → Manage Users → "Create reset token" → shown once with an expiry → admin hands it over
→ the user enters it on the same reset page. Same table, same validation.

### Token rules
- 32 random bytes, base64url. Only the SHA-256 hash is stored.
- Single use, expires after 1 hour (`auth.reset_token_minutes`).
- Issuing a new token invalidates the user's earlier ones.
- Rate limit: 5 requests per hour per IP and per account.
- Using it also sets `password_changed_at` and **invalidates existing sessions** (new
  `OnTokenValidated` check against that timestamp, alongside the deactivated-user check — per
  the CLAUDE.md rule that every auth path consults both).

### Data
New table `password_reset_tokens`: `id`, `user_id`, `token_hash`, `created_at`, `expires_at`,
`used_at`, `issued_by_user_id` (null for self-service), `delivery` (`email`|`admin`). New
nullable `users.password_changed_at`.

### SMTP settings (Settings → Email, admin)
`app_settings` keys: host, port, security (none/STARTTLS/TLS), username, from address, and
password (encrypted with the same protector plugin settings use). A "Send test email" button.
Also a required **public base URL** setting for building the link. MailKit as the client.

### Recovery gap
If the only admin forgets their password and there is no SMTP, nothing above helps. Add a local
command, `Chronicle.API --reset-admin-password <username>`, that requires file-system access to
the install (so it is only usable by someone who owns the machine) and prints a one-time token.

### Decisions needed
1. Allow email to any contact marked email, or primary only (recommended: primary only)?
2. 1-hour expiry and 5/hour rate limit OK?
3. Add the `--reset-admin-password` command? (Recommended: yes.)

---

## 3. Scan: type-mismatch correction and related files

### Findings
- Scanning takes a `MediaTypeId` per request/scan folder; grouping lives in
  `ScanGroupingService`.
- Sidecar handling is already there but hardcoded: a fixed extension set and a fixed list of
  folder names (`theme-music`, `extras`, `featurettes`, …) are dropped from grouping and not
  attached to anything. Both lists go against the no-hardcoding rule.
- The scheduled scan auto-imports groups above the confidence threshold, so a wrong type is
  imported silently.

### 3a. Type-mismatch correction
Make detection data-driven instead of "TV looks like S01E01":
- Add `media_types.scan_hints_json` (editable on the Media Types page, section 5):
  filename regexes, folder-name regexes and extensions that suggest this type. Seeded with
  defaults for Movies, TV and Music so behaviour is unchanged out of the box.
- During grouping, score every group against every active type's hints. If another type
  beats the scan folder's type by a margin (`scan.mismatch_margin`), the group is flagged
  `SuggestedMediaTypeId` + reason (e.g. "3 files match `S\d+E\d+`").
- **Interactive scan:** a badge on the group ("Looks like TV Shows") with a one-click
  "Re-match as TV Shows" that re-runs matching under that type.
- **Scheduled scan:** a setting `scan.mismatch_action` = `flag` (default; imports nothing for
  that group and raises a notification) | `reclassify` (import under the suggested type) |
  `ignore`.

### 3b. Related files
Goal: `Dark Matter (2024)\theme.mp3` belongs to Dark Matter and is not a stray Music item.

- New table `media_item_related_files`: `id`, `media_item_id`, `path`, `kind`
  (subtitle / artwork / theme / extra / booklet / other), `size_bytes`, `discovered_at`,
  `missing_since`. Lossless: nothing about the files is dropped.
- Move the sidecar extension and folder-name sets into settings/DB (seeded with today's
  values) so the user can extend them.
- Grouping change: sidecar-classified files and files in sidecar folders are no longer
  discarded; they attach to the nearest ancestor group (longest path prefix).
- The **"treat all files in a matched item's folder as related"** option goes beyond sidecars:
  any file inside a confidently matched group's folder that did not itself form a
  confident group is bundled. Exposed as a checkbox on the scan page and as
  `scan_folders.bundle_related_files` (so scheduled scans have a stored choice). Default off,
  as in the original backlog wording.
- Rescans reconcile: files that vanish get `missing_since` (reuse the existing
  `MissingSourceReconciliationService` pattern).
- UI: scan results show a "+N related files" count per group (not separate rows); the media
  detail page gets a "Related files" section grouped by kind.

### Decisions needed
1. `scan.mismatch_action` default `flag`, OK? (Silent reclassifying could mis-file things.)
2. Related-file bundling default off, per scan folder?
3. Confirm the new tables/columns above; each is an EF migration.

---

## 4. Live library updates and notifications

### Findings
- The library uses react-query with a 60-second `staleTime`; it refreshes only when the user
  acts. Only `NowPlayingBanner` polls.
- Nothing pushes changes to the browser, and there is no notification store.
- `ScheduledScanService` already knows how many groups it imported.

### Live updates: a change feed
- Server keeps an in-memory, monotonically increasing revision counter and a ring buffer of
  `(revision, itemId, kind)` for media-item adds/changes/deletes. One EF `SaveChanges`
  interceptor records them, so every writer (enrichment, scans, sync, merge, art pinning)
  is covered in one place, in line with how `ResolveAsync` already centralises resolution.
- `GET /api/v1/library/changes?since=<rev>` returns changed ids, or `reset: true` if the client
  is older than the buffer or the server restarted (the client then refetches everything).
- Client polls every 15 s, only while the tab is visible, and invalidates just the affected
  react-query entries (library cards, the open detail page). A poster change then appears
  without a reload.
- Transport can later become SSE without changing the client contract. Polling first because
  it needs no new infrastructure and no auth workaround (EventSource cannot send an
  Authorization header).

### Notifications
- New table `notifications`: `id`, `user_id` (null = all admins), `kind`, `title`, `body`,
  `link`, `created_at`, `read_at`.
- Producers: scheduled scan imported N items (the missing "notify when new items found"),
  background task failed, plugin update available (`PluginUpdateCheckService` already flags
  it), DB size threshold crossed, type-mismatch flagged (section 3).
- UI: bell in the header with an unread count, carried by the same 15 s poll; click opens a
  list; mark read / mark all read. Retention 90 days (a maintenance task).
- Per-user preference to mute kinds (stored in `UserPreferences`).

### Decisions needed
1. Poll interval 15 s OK?
2. Which notification kinds do you want on by default?

---

## 5. Media type registration

### Findings (this changes the backlog item)
- **Plugin-driven registration already exists.** A plugin's `MediaTypeSupport` carries
  `DisplayName`, `HierarchyLevels`, `HierarchyLabels` and more, and Chronicle upserts the type
  into `media_types` at startup. So "installing a plugin registers its type" works today.
- What is missing: a way for the **user** to register a type with no plugin, a page to manage
  types at all (there is no media-types controller), and the "no plugin for this type — want
  one?" prompt.
- `MediaType` already holds `InteractionVerb`, `ProgressUnit` and hierarchy labels, which is
  what the no-hardcoding fix in the backlog needs.

### Design
- **Settings → Media Types (admin):** list with item count and the installed plugins that
  support each type. Add / edit / deactivate; delete only when it has no items. Fields: name,
  display name, description, hierarchy levels and labels, interaction verb, progress unit,
  collections support, plus `scan_hints_json` from section 3.
- **Coverage:** for each type, installed plugins that declare it. If none, show
  "No plugin handles this type" and a button into the plugin catalog filtered to plugins that
  declare it (requires `supported_media_types` in each plugin manifest; the catalog already
  reads `manifest.json`).
- **Plugin install:** if the plugin declares a type that does not exist, create it (already
  done at startup) and surface a notification offering to scan existing media with it.
- **Edits to plugin-registered types:** plugin-supplied values are defaults; user edits are
  kept and not overwritten by the next startup upsert (add `is_user_modified` per field set, or
  simplest: skip the upsert for any type with `is_user_modified = true`).
- **Labels:** Dashboard and History read the verb from the type (fixes the "Watch Time" /
  "Watch History" hardcoding); "Listens" appears for music with no code change.
- `FileScanService`'s `"audiobooks"` name check becomes a media-type property (for example
  `hierarchy_scan_strategy`), seeded for audiobooks.

### Decisions needed
1. Should plugin updates be allowed to overwrite user-edited type values? (Recommended: no.)
2. Add `supported_media_types` to manifests, which means a manifest change for every plugin?

---

## Suggested order
1. Media Types page + label fix (small, unblocks 3a and removes hardcoding).
2. Backups, then maintenance and size.
3. Password reset (+ SMTP settings).
4. Notifications + change feed.
5. Scan type-mismatch and related files (largest; depends on 1 and 4).
