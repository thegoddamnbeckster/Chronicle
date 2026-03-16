# Background Tasks Page — Design Document

**Date:** 2026-03-15
**Status:** Approved
**Scope:** Full-stack feature — scheduler infrastructure, API, React UI

---

## Overview

A dedicated Settings page (`/settings/background-tasks`) that gives users visibility into Chronicle's background tasks, lets them configure schedules, and provides a "Run Now" button. The existing `MetadataRefreshService` and `DuplicateCleanupService` are refactored to integrate with a new central scheduler.

---

## Requirements

- View all registered background tasks with live status, last run outcome, and next scheduled run
- Run any task immediately on demand
- Configure each task's schedule via a visual builder (default) or raw cron expression (advanced)
- Prevent a task from running more than once concurrently, by any trigger path
- All times displayed in the user's local timezone (UTC stored, converted in browser)
- User-facing error messages are friendly, descriptive, and actionable

---

## Backend

### `IScheduledTask` Interface

New interface in `Chronicle.Services`:

```csharp
public interface IScheduledTask
{
    string TaskId { get; }        // unique key, e.g. "metadata_refresh"
    string DisplayName { get; }   // e.g. "Metadata Refresh"
    string Description { get; }   // one-sentence description for UI
    string DefaultCron { get; }   // seed cron, e.g. "0 */4 * * *"
    Task ExecuteAsync(CancellationToken ct);
}
```

`MetadataRefreshService` and `DuplicateCleanupService` are refactored to implement `IScheduledTask` instead of extending `BackgroundService` directly. Their core logic moves into `ExecuteAsync`.

---

### DB Table: `background_tasks`

New migration adds this table:

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `task_id` | TEXT | No | Primary key, e.g. `metadata_refresh` |
| `display_name` | TEXT | No | |
| `description` | TEXT | No | |
| `cron_expression` | TEXT | No | 5-field Cronos-compatible cron |
| `is_enabled` | INTEGER | No | 0 = disabled, 1 = enabled |
| `last_run_at` | TEXT | Yes | UTC ISO-8601 |
| `last_run_succeeded` | INTEGER | Yes | 1 = success, 0 = failed, NULL = never run |
| `last_error_message` | TEXT | Yes | Plain-English error for UI display |
| `next_run_at` | TEXT | Yes | UTC ISO-8601, recalculated after each run |

Seeded on first startup by `TaskSchedulerService` from each task's `DefaultCron`.

---

### `TaskSchedulerService : BackgroundService`

Central scheduler in `Chronicle.Services`. Responsibilities:

- **Startup:** Seeds `background_tasks` table for any registered `IScheduledTask` not yet present. Calculates `next_run_at` for any task where it is null.
- **Tick loop:** Wakes every 30 seconds. For each enabled task where `next_run_at <= UtcNow`:
  - If task is already in the live-state dictionary as running → skip, log warning, recalculate `next_run_at`, continue.
  - Otherwise: mark running, fire `ExecuteAsync` via `Task.Run` (non-blocking tick), on completion persist `last_run_at` / `last_run_succeeded` / `last_error_message`, recalculate `next_run_at`.
- **Live state:** `ConcurrentDictionary<string, bool>` for is-running status per task. This is in-memory only; DB columns are the durable record.
- **Manual trigger:** `TriggerNowAsync(taskId)` — same execution path as the tick, same skip-if-running guard.
- **Concurrency guarantee:** A task cannot run more than once simultaneously regardless of trigger source (scheduler tick or Run Now API).
- **Error isolation:** A task that throws does not crash the scheduler. Exception is caught, message persisted as `last_error_message`, task returns to idle.
- **Bad cron guard:** If Cronos cannot parse a stored cron expression at startup, the task is disabled and a startup error is logged.

**NuGet dependency:** `Cronos` (cron parsing and next-occurrence calculation).

---

### `BackgroundTasksController`

New controller at `Chronicle.API/Controllers/BackgroundTasksController.cs`.

```
GET    /api/v1/background-tasks          → list all tasks (live state merged with DB row)
PATCH  /api/v1/background-tasks/{id}     → update cron_expression and/or is_enabled (Admin)
POST   /api/v1/background-tasks/{id}/run → trigger immediately (Admin)
```

**Response shape (GET list item):**
```json
{
  "taskId": "metadata_refresh",
  "displayName": "Metadata Refresh",
  "description": "Refreshes metadata for all library items from active plugins.",
  "cronExpression": "0 */4 * * *",
  "isEnabled": true,
  "isRunning": false,
  "lastRunAt": "2026-03-15T14:30:00Z",
  "lastRunSucceeded": true,
  "lastErrorMessage": null,
  "nextRunAt": "2026-03-15T18:30:00Z"
}
```

**PATCH body:**
```json
{ "cronExpression": "0 2 * * *", "isEnabled": true }
```

**Error responses:**
- `404` — task ID not found
- `409` — Run Now on a task already running: `"This task is already running. Wait for it to finish before running it again."`
- `400` (PATCH) — invalid cron: `"The cron expression '{value}' is not valid. A cron expression has five fields: minute, hour, day-of-month, month, day-of-week. Example: 0 */4 * * * (every 4 hours)."`
- `202 Accepted` — Run Now accepted

---

## Frontend

### Route & Nav

- Route: `/settings/background-tasks`
- Added to `App.tsx` and Layout's Settings `NavGroup` (alphabetically between "Background Tasks" and "Library", i.e. before "Library")

---

### Page Layout

One card per task. Cards are stacked vertically, matching the style of `PluginsPage` cards.

**Card contents:**
- Task name (bold heading) + description (muted subtext)
- Status badge: `Idle` (grey) / `Running` (blue, pulsing) / `Success` (green) / `Failed` (red)
- Last run: relative time (e.g. "2 hours ago") with absolute local datetime on hover tooltip
- Next run: same treatment
- Enable/disable toggle (right side of card header)
- "Run Now" button — disabled + spinner while `isRunning = true`; on click calls POST, then polls GET every 3 seconds until `isRunning` returns false
- "Edit Schedule" button — expands inline schedule editor below the card stats

---

### Schedule Editor (inline, per-card)

**Visual builder (default view):**

| Control | When shown |
|---|---|
| Frequency dropdown: `Minutes / Hours / Daily / Weekly / Monthly` | Always |
| "Every N" number input | Always |
| Time-of-day picker (HH:MM) | Daily, Weekly, Monthly |
| Day-of-week checkboxes (Mon–Sun) | Weekly |
| Day-of-month number input (1–31) | Monthly |

Live preview text below the form: e.g. "Runs every 4 hours" or "Runs every Monday at 2:00 AM".

**Advanced toggle** (uses existing `AdvancedToggle` component, same pattern as Service page):
- Raw cron input (5-field)
- Inline validation: shows "Next run: [local datetime]" when valid; red error message when invalid
- Cron ↔ visual builder are synced: editing cron updates visual fields if parseable; editing visual fields updates cron string

**Save / Cancel buttons.** Calls PATCH. On validation error from API, shows inline friendly error message.

---

### Error Messages (user-facing)

| Scenario | Message |
|---|---|
| Invalid cron expression (frontend) | "This isn't a valid cron expression. A cron expression has five fields: minute, hour, day-of-month, month, day-of-week. Example: `0 */4 * * *` (every 4 hours)." |
| Day-of-month out of range | "Day of month must be between 1 and 31." |
| "Every N" is zero or negative | "Interval must be at least 1." |
| No day-of-week selected (weekly) | "Select at least one day of the week." |
| Run Now while running (should not reach API due to disabled button, but defensive) | "This task is already running. Wait for it to finish before running it again." |
| Task failure (last_error_message on card) | Shown verbatim from API — written as plain English by the service layer |
| API unreachable | "Could not reach the Chronicle API. Check that the service is running." |

---

## Data Flow

```
Scheduler tick (every 30s)
  └─ check next_run_at <= UtcNow
       └─ skip if running
       └─ Task.Run → ExecuteAsync
            └─ success: update DB (last_run_at, succeeded=true, next_run_at)
            └─ failure: update DB (last_run_at, succeeded=false, last_error_message)

Run Now (POST /api/v1/background-tasks/{id}/run)
  └─ 409 if running
  └─ 202 Accepted → same Task.Run path as scheduler
  └─ UI polls GET every 3s until isRunning = false

Schedule update (PATCH)
  └─ validate cron via Cronos (400 if invalid)
  └─ persist to DB
  └─ recalculate next_run_at from UtcNow
```

---

## Testing

### Unit Tests (`Chronicle.Tests.Unit`)
- `TaskSchedulerService`: tick skips running tasks, fires due tasks, updates DB correctly, recalculates `next_run_at`
- `TaskSchedulerService`: bad cron at startup disables task and logs error
- `TaskSchedulerService`: `TriggerNowAsync` returns 409-equivalent when task is already running
- `TaskSchedulerService`: exception in `ExecuteAsync` is caught, persisted, does not crash scheduler
- Cron ↔ visual builder translation: round-trip for each frequency type produces correct cron string

### Integration Tests (`Chronicle.Tests.Integration`)
- `GET /api/v1/background-tasks` returns seeded tasks with correct shape
- `PATCH /api/v1/background-tasks/{id}` persists schedule change and recalculates `next_run_at`
- `PATCH` with invalid cron returns 400 with friendly message
- `POST .../run` returns 202
- `POST .../run` while already running returns 409 with friendly message
- Existing `MetadataRefreshService` and `DuplicateCleanupService` unit tests remain valid — they now test `ExecuteAsync` directly

---

## Files to Create / Modify

### New files
- `src/Chronicle.Services/IScheduledTask.cs`
- `src/Chronicle.Services/TaskSchedulerService.cs`
- `src/Chronicle.API/Controllers/BackgroundTasksController.cs`
- `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx`
- `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.module.css`
- `src/Chronicle.Web/src/api/backgroundTasks.ts`
- `src/Chronicle.Data/Migrations/{timestamp}_AddBackgroundTasksTable.cs`

### Modified files
- `src/Chronicle.Services/MetadataRefreshService.cs` — implement `IScheduledTask`, remove `BackgroundService` inheritance
- `src/Chronicle.Services/DuplicateCleanupService.cs` — same refactor
- `src/Chronicle.API/Program.cs` — register `TaskSchedulerService`, register `IScheduledTask` implementations
- `src/Chronicle.Web/src/App.tsx` — add `/settings/background-tasks` route
- `src/Chronicle.Web/src/components/layout/Layout.tsx` — add nav link
- `src/Chronicle.Data/ChronicleDbContext.cs` — add `BackgroundTasks` DbSet
- `src/Chronicle.Core/Models/BackgroundTask.cs` — new EF entity
