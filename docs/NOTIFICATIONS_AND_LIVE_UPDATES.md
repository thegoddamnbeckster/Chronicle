# Notifications and live updates

Two small features that share one background poll from the web UI.

## Live library updates

Open pages used to show whatever they loaded until something the person did refreshed them (a new poster arriving from a
background refresh stayed invisible). Now the shell asks `GET /api/v1/library/changes?since=<revision>&epoch=<id>` every
15 seconds while the tab is visible (and immediately when it becomes visible) and refreshes just what changed.

* **Where changes come from.** One EF Core `SaveChanges` interceptor (`LibraryChangeInterceptor`) records every added,
  changed or deleted `MediaItem` and every `UserLibrary` row, after the save so new ids are known. Enrichment, scans, sync,
  merges, art pinning and a person rating something are all covered by that one hook. The record
  (`LibraryChangeFeed`) is a bounded in-memory buffer (5000 events).
* **Per person.** A change to someone's library entry (status, rating) is reported only to that person.
* **What the client does.** The item's own page refreshes the moment its item changes; the library grid refreshes at most
  every 30 seconds however busy background work is. The first ask only takes a baseline.
* **When it cannot say exactly.** A different `epoch` (the server restarted), a client that waited longer than the buffer
  holds, or a bulk operation (`MarkAllChanged`, used after the library reset endpoints because `ExecuteDelete` bypasses
  change tracking) answers `reset: true`, and the client refetches the library and item pages once.
* **Not a keep-alive.** The poll carries `X-Chronicle-Background`, so an abandoned tab still times out (see
  `docs/SECURITY.md`).

## Notifications (the bell)

A row per recipient in `notifications`: kind, title, optional body, an in-app link, read time. The bell in the header shows
the unread count (carried by the same poll) and a panel of recent notices; clicking one marks it read and opens the page it
is about. Notices older than `notifications.retain_days` (default 90) are removed nightly.

| Kind | Raised by | Who |
|---|---|---|
| `scan.imported` | the nightly scheduled scan, per folder that imported something | administrators |
| `task.failed` | any background task that ends with an error (one unread notice per task) | administrators |
| `plugin.update` | the plugin update check, once per new version | administrators |
| `database.size` | the nightly backup, when the database is over its warning size | administrators |

Each person can switch kinds off under Preferences -> Notifications (`mutedNotificationKinds`). A producer can also give a
*dedupe key*: the same thing is not announced again while the earlier notice is unread (or, for "announce once" things such
as a plugin version, ever). Links must be in-app paths (`/settings/...`); anything else is dropped.

## API

| Method | Path | |
|---|---|---|
| GET | `/api/v1/library/changes` | `since`, `epoch` -> `{ epoch, revision, reset, changes[], unreadNotifications }` |
| GET | `/api/v1/notifications` | `limit`, `unreadOnly` -> `{ unread, items[] }` (the caller's own) |
| GET | `/api/v1/notifications/kinds` | what can be switched off |
| POST | `/api/v1/notifications/{id}/read`, `/read-all` | |
| DELETE | `/api/v1/notifications/{id}`, `/read` | one, or everything already read |

Another person's notification id behaves exactly like a missing one (404).
