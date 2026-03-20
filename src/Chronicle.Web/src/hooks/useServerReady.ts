import { useState, useEffect, useRef } from 'react'

/**
 * Polls GET /api/health every second until the Chronicle API responds.
 * Returns `true` once the server is reachable, `false` while it is starting up.
 */
export function useServerReady(): boolean {
  const [ready, setReady] = useState(false)
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null)

  useEffect(() => {
    // Try immediately, then every second until success
    async function check() {
      try {
        const res = await fetch('/api/health', { method: 'GET' })
        if (res.ok) {
          setReady(true)
          if (intervalRef.current) clearInterval(intervalRef.current)
        }
      } catch {
        // API not yet reachable — keep polling
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
