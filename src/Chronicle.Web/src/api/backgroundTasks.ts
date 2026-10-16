import client from './client'

export interface BackgroundTask {
  taskId: string
  displayName: string
  description: string
  cronExpression: string
  isEnabled: boolean
  isRunning: boolean
  lastRunAt: string | null        // UTC ISO-8601
  lastRunSucceeded: boolean | null
  lastErrorMessage: string | null
  nextRunAt: string | null        // UTC ISO-8601
  // Plugin branding — null for system tasks
  pluginId: string | null
  pluginName: string | null
  pluginIconUrl: string | null
  brandColorLight: string | null
  brandColorDark: string | null
  schedulable: boolean
  // False for a row that exists purely to display status for something event-triggered
  // (fires on an edit/rating/watch, not on a schedule or on demand) -- no backing
  // IScheduledTask, so Run Now would always fail with TASK_NOT_FOUND. Distinct from
  // isEnabled/schedulable: a disabled-but-real task (e.g. a plugin sync nobody turned on)
  // is still runnable.
  isRunnable: boolean
  runConfirmation: { title: string; message: string } | null
  /** Set when the owning plugin's files changed without approval and it was not loaded. */
  pluginIntegrityBlockedAt?: string | null
  /** 1 is the highest priority; every task has its own. Of two tasks that conflict the higher one runs and the other waits. */
  priority?: number
  /** A heavy database writer: conflicts with every other task. */
  exclusive?: boolean
  /** While paused or queued: the name of the higher-priority task it is waiting for. */
  waitingFor?: string | null
}

export async function getBackgroundTasks(): Promise<BackgroundTask[]> {
  const res = await client.get<{ success: true; data: BackgroundTask[] }>('/background-tasks')
  return res.data.data
}

export async function updateBackgroundTask(
  taskId: string,
  patch: { cronExpression?: string; isEnabled?: boolean },
): Promise<void> {
  await client.patch(`/background-tasks/${taskId}`, patch)
}

/** Moves a task one place up or down in the priority order (admin). */
export async function moveBackgroundTask(taskId: string, direction: 'up' | 'down'): Promise<void> {
  await client.post(`/background-tasks/${taskId}/priority`, { direction })
}

export async function runBackgroundTask(taskId: string): Promise<void> {
  await client.post(`/background-tasks/${taskId}/run`)
}
