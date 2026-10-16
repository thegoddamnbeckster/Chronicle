/**
 * Reports API helpers.
 *
 * Client-side helpers that derive charts from existing endpoints.
 */
import client from './client'
import type { ApiResponse, HistoryItem, LibraryEntry, LibraryStatus, UserStats } from '@/types'

/** Fetches up to `perPage` recent history items. */
export async function getHistoryPage(page = 1, perPage = 100): Promise<HistoryItem[]> {
  const { data } = await client.get<ApiResponse<HistoryItem[]>>('/scrobble/history', {
    params: { page, perPage },
  })
  return data.data ?? []
}

/** Fetches all library entries for a given status (or all if undefined). */
export async function getLibraryPage(status?: LibraryStatus): Promise<LibraryEntry[]> {
  const { data } = await client.get<ApiResponse<LibraryEntry[]>>('/library', {
    params: { status, perPage: 500 },
  })
  return data.data ?? []
}

export { getStats } from './stats'
export type { UserStats }

// ── Client-side aggregation helpers ───────────────────────────────────────────

/** Builds a 7-day activity array from a list of history items. */
export function buildWeeklyActivity(
  history: HistoryItem[],
): Array<{ day: string; count: number }> {
  const days: Record<string, number> = {}
  for (let i = 6; i >= 0; i--) {
    const d = new Date()
    d.setDate(d.getDate() - i)
    days[d.toLocaleDateString('en-CA')] = 0 // 'YYYY-MM-DD'
  }
  for (const h of history) {
    const key = new Date(h.timestamp).toLocaleDateString('en-CA')
    if (key in days) days[key]++
  }
  return Object.entries(days).map(([day, count]) => ({
    day: new Date(day).toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' }),
    count,
  }))
}

/** Builds library status counts from a list of library entries. */
export function buildStatusBreakdown(
  entries: LibraryEntry[],
): Array<{ status: string; count: number }> {
  const counts: Record<string, number> = {}
  for (const e of entries) {
    counts[e.status] = (counts[e.status] ?? 0) + 1
  }
  return Object.entries(counts)
    .map(([status, count]) => ({ status, count }))
    .sort((a, b) => b.count - a.count)
}

/** "Show › Season" breadcrumb for an item's ancestor chain, or '' if it has none
 *  (e.g. a standalone movie or a top-level show being tracked directly). */
export function ancestorBreadcrumb(ancestors?: { id: number; name: string }[]): string {
  return ancestors && ancestors.length > 0 ? ancestors.map(a => a.name).join(' › ') : ''
}

/**
 * Collapses a history list down to one entry per media item — the most recent
 * scrobble for that item — while preserving overall recency order. A single
 * episode reports progress repeatedly as it plays (0%, 1%, 55%, ...), which is
 * exactly what the raw list is for (the activity charts above count every ping
 * on purpose), but a "recent activity" list showing every ping as its own row
 * reads as the same episode watched many times instead of once, in progress.
 */
export function dedupeHistoryByMediaItem(history: HistoryItem[]): HistoryItem[] {
  const seen = new Set<number>()
  const result: HistoryItem[] = []
  for (const h of history) {
    if (seen.has(h.mediaItemId)) continue
    seen.add(h.mediaItemId)
    result.push(h)
  }
  return result
}

/** Builds a 30-day activity array (daily counts) from history items. */
export function buildMonthlyActivity(
  history: HistoryItem[],
): Array<{ day: string; count: number }> {
  const days: Record<string, number> = {}
  for (let i = 29; i >= 0; i--) {
    const d = new Date()
    d.setDate(d.getDate() - i)
    days[d.toLocaleDateString('en-CA')] = 0
  }
  for (const h of history) {
    const key = new Date(h.timestamp).toLocaleDateString('en-CA')
    if (key in days) days[key]++
  }
  return Object.entries(days).map(([day, count]) => ({
    day: new Date(day).toLocaleDateString('en-US', { month: 'short', day: 'numeric' }),
    count,
  }))
}
