import { useEffect } from 'react'
import { useQueryClient, type QueryClient } from '@tanstack/react-query'
import { getChanges, type ChangesResponse } from '@/api/notifications'

export const UNREAD_KEY = ['notifications', 'unread'] as const

/** How often the open tab asks what changed. Quick enough to feel live, cheap enough to ignore. */
export const POLL_MS = 15_000

/** The library grid is large; refresh it at most this often however busy background work is. */
export const LIBRARY_REFRESH_MIN_MS = 30_000

interface Baseline { epoch: string; revision: number }

/**
 * Applies one answer from the change feed to the query cache. Pure with respect to the network, so it can be
 * tested without a timer: item pages refresh the moment their item changes; the library grid is refreshed (at most
 * every LIBRARY_REFRESH_MIN_MS); a reset (server restarted, or too long away) refetches everything once.
 */
export function applyChanges(
  qc: QueryClient,
  res: ChangesResponse,
  state: { lastLibraryRefresh: number },
  now: number,
): void {
  qc.setQueryData(UNREAD_KEY, res.unreadNotifications)

  if (res.reset) {
    void qc.invalidateQueries({ queryKey: ['library'] })
    void qc.invalidateQueries({ queryKey: ['media'] })
    state.lastLibraryRefresh = now
    return
  }
  if (res.changes.length === 0) return

  for (const c of res.changes) void qc.invalidateQueries({ queryKey: ['media', c.itemId] })

  if (now - state.lastLibraryRefresh >= LIBRARY_REFRESH_MIN_MS) {
    void qc.invalidateQueries({ queryKey: ['library'] })
    state.lastLibraryRefresh = now
  }
}

/**
 * Keeps the open library and item pages current without a reload, and keeps the notification bell's unread count
 * fresh. Mount once inside the signed-in shell. Polls only while the tab is visible, and again the moment it
 * becomes visible. Any failure is ignored: a missed poll just means waiting for the next one (a 401 is handled by
 * the API client as usual).
 */
export function useLiveUpdates(enabled: boolean): void {
  const qc = useQueryClient()

  useEffect(() => {
    if (!enabled) return
    let baseline: Baseline | null = null
    let stopped = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const state = { lastLibraryRefresh: Date.now() }

    async function poll() {
      if (stopped) return
      if (document.visibilityState === 'visible') {
        try {
          const res = await getChanges(baseline?.revision, baseline?.epoch)
          if (stopped) return
          if (baseline) applyChanges(qc, res, state, Date.now())
          else qc.setQueryData(UNREAD_KEY, res.unreadNotifications)
          baseline = { epoch: res.epoch, revision: res.revision }
        } catch {
          /* try again next time */
        }
      }
      if (!stopped) timer = setTimeout(poll, POLL_MS)
    }

    function onVisible() {
      if (document.visibilityState === 'visible') {
        if (timer) clearTimeout(timer)
        void poll()
      }
    }

    document.addEventListener('visibilitychange', onVisible)
    void poll()
    return () => {
      stopped = true
      if (timer) clearTimeout(timer)
      document.removeEventListener('visibilitychange', onVisible)
    }
  }, [enabled, qc])
}
