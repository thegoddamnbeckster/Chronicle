/**
 * Reports API helpers.
 *
 * The reports page derives charts from existing endpoints:
 *   - /stats            → totals
 *   - /scrobble/history → per-event timestamps for activity charts
 *   - /library          → status breakdown
 *
 * All derived computations happen client-side so we don't need extra
 * backend endpoints until the IReportPlugin system is in place.
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
