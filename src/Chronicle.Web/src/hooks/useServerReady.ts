import { useState, useEffect, useRef } from 'react'

/**
 * Polls GET /api/health every second and reflects whether the Chronicle API is
 * currently reachable. Keeps polling for the lifetime of the component (not just
 * until the first success) so a login page left open across a server restart
 * notices the server going away and flips back to "Connecting…" instead of
 * staying latched on stale "ready" state — without this, a tab that had already
 * seen one healthy check would let a submit through mid-restart and surface
 * whatever raw error the request failed with (e.g. a proxy-level 500) instead
 * of the friendly connecting state this hook exists to show.
 */
export function useServerReady(): boolean {
  const [ready, setReady] = useState(false)
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null)

  useEffect(() => {
    async function check() {
      try {
        const res = await fetch('/api/health', { method: 'GET' })
        setReady(res.ok)
      } catch {
        // API not reachable — keep polling, and stop reporting ready if we
        // previously were (server restarted or dropped out from under us).
        setReady(false)
      }
    }

    check()
    intervalRef.current = setInterval(check, 1000)

    return () => {
      if (intervalRef.current) clearInterval(intervalRef.current)
    }
  }, [])

  return ready
}
