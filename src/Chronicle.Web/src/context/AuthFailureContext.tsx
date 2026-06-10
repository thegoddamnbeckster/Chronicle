import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react'
import { getAuthFailures, listPlugins, type PluginAuthFailure } from '@/api/plugins'
import { subscribeToAuthFailures } from '@/api/client'

interface AuthFailureContextValue {
  failures: PluginAuthFailure[]
  addFailure: (f: PluginAuthFailure) => void
  dismiss: (pluginId: string) => void
}

const AuthFailureContext = createContext<AuthFailureContextValue>({
  failures: [],
  addFailure: () => {},
  dismiss: () => {},
})

export function useAuthFailures() {
  return useContext(AuthFailureContext)
}

/** Polls /plugins/auth-failures on mount and every 60 s */
export function AuthFailureProvider({ children }: { children: React.ReactNode }) {
  const [failures, setFailures] = useState<PluginAuthFailure[]>([])
  // Track which pluginIds the user has dismissed this session
  const dismissed = useRef<Set<string>>(new Set())

  const mergeFailures = useCallback((incoming: PluginAuthFailure[]) => {
    setFailures(prev => {
      const next = [...prev]
      for (const f of incoming) {
        if (dismissed.current.has(f.pluginId)) continue
        if (!next.some(x => x.pluginId === f.pluginId)) {
          next.push(f)
        }
      }
      return next
    })
  }, [])

  const addFailure = useCallback((f: PluginAuthFailure) => {
    if (dismissed.current.has(f.pluginId)) return
    setFailures(prev =>
      prev.some(x => x.pluginId === f.pluginId) ? prev : [...prev, f],
    )
  }, [])

  const dismiss = useCallback((pluginId: string) => {
    dismissed.current.add(pluginId)
    setFailures(prev => prev.filter(f => f.pluginId !== pluginId))
  }, [])

  // Subscribe to real-time auth failures emitted by the API interceptor
  useEffect(() => {
    // We need to resolve plugin names; fetch them lazily once
    let pluginCache: Awaited<ReturnType<typeof listPlugins>> | null = null
    const fetchPlugins = async () => {
      if (!pluginCache) {
        try { pluginCache = await listPlugins() } catch { pluginCache = [] }
      }
      return pluginCache
    }

    const unsub = subscribeToAuthFailures(async (pluginId) => {
      if (dismissed.current.has(pluginId)) return
      const plugins = await fetchPlugins()
      const match = plugins.find(p => p.pluginId === pluginId)
      addFailure({
        pluginId,
        pluginName: match?.name ?? pluginId,
        dbId: match?.id ?? null,
      })
    })
    return unsub
  }, [addFailure])

  // Initial fetch + polling every 60 s
  useEffect(() => {
    const token = localStorage.getItem('chronicle_token')
    if (!token) return

    const poll = async () => {
      try {
        const data = await getAuthFailures()
        mergeFailures(data)
      } catch {
        // silently ignore — auth check is best-effort
      }
    }

    poll()
    const id = window.setInterval(poll, 60_000)
    return () => window.clearInterval(id)
  }, [mergeFailures])

  return (
    <AuthFailureContext.Provider value={{ failures, addFailure, dismiss }}>
      {children}
    </AuthFailureContext.Provider>
  )
}
