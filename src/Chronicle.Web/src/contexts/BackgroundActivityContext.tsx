import { createContext, useCallback, useContext, useRef, useState } from 'react'

// ── Types ─────────────────────────────────────────────────────────────────────

export type JobStatus = 'running' | 'done' | 'failed'

export interface Job {
  id: string
  label: string
  status: JobStatus
  detail?: string
}

interface BackgroundActivityContextValue {
  jobs: Job[]
  addJob: (label: string) => string
  completeJob: (id: string, detail?: string) => void
  failJob: (id: string, detail?: string) => void
}

// ── Context ───────────────────────────────────────────────────────────────────

const BackgroundActivityContext = createContext<BackgroundActivityContextValue | null>(null)

// ── Provider ──────────────────────────────────────────────────────────────────

const DISMISS_DELAY_MS = 5_000

export function BackgroundActivityProvider({ children }: { children: React.ReactNode }) {
  const [jobs, setJobs] = useState<Job[]>([])
  const timers = useRef<Map<string, ReturnType<typeof setTimeout>>>(new Map())

  const removeJob = useCallback((id: string) => {
    setJobs(prev => prev.filter(j => j.id !== id))
    timers.current.delete(id)
  }, [])

  const scheduleRemoval = useCallback((id: string) => {
    // Clear any existing timer for this job (e.g. completeJob called twice)
    const existing = timers.current.get(id)
    if (existing) clearTimeout(existing)
    const t = setTimeout(() => removeJob(id), DISMISS_DELAY_MS)
    timers.current.set(id, t)
  }, [removeJob])

  const addJob = useCallback((label: string): string => {
    const id = crypto.randomUUID()
    setJobs(prev => [...prev, { id, label, status: 'running' }])
    return id
  }, [])

  const completeJob = useCallback((id: string, detail?: string) => {
    setJobs(prev => prev.map(j => j.id === id ? { ...j, status: 'done', detail } : j))
    scheduleRemoval(id)
  }, [scheduleRemoval])

  const failJob = useCallback((id: string, detail?: string) => {
    setJobs(prev => prev.map(j => j.id === id ? { ...j, status: 'failed', detail } : j))
    scheduleRemoval(id)
  }, [scheduleRemoval])

  return (
    <BackgroundActivityContext.Provider value={{ jobs, addJob, completeJob, failJob }}>
      {children}
    </BackgroundActivityContext.Provider>
  )
}

// ── Hook ──────────────────────────────────────────────────────────────────────

export function useBackgroundActivity(): BackgroundActivityContextValue {
  const ctx = useContext(BackgroundActivityContext)
  if (!ctx) throw new Error('useBackgroundActivity must be used within BackgroundActivityProvider')
  return ctx
}
