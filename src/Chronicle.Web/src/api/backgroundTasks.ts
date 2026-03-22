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

export async function runBackgroundTask(taskId: string): Promise<void> {
  await client.post(`/background-tasks/${taskId}/run`)
}
