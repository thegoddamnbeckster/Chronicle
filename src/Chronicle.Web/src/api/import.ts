import client from './client'
import type { ApiResponse, ImportProvider, ImportAuthStart, ImportPollResult, ImportResult, SyncResult, SyncJobStatus } from '@/types'

export async function getImportProviders(): Promise<ImportProvider[]> {
  const { data } = await client.get<ApiResponse<ImportProvider[]>>('/import/providers')
  return data.data ?? []
}

export async function startAuth(pluginId: string): Promise<ImportAuthStart> {
  const { data } = await client.post<ApiResponse<ImportAuthStart>>(`/import/${pluginId}/auth/start`)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to start auth')
  return data.data
}

export async function pollAuth(pluginId: string, pollCode: string): Promise<ImportPollResult> {
  const { data } = await client.get<ApiResponse<ImportPollResult>>(
    `/import/${pluginId}/auth/poll/${pollCode}`,
  )
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Poll failed')
  return data.data
}

export async function getAuthStatus(pluginId: string): Promise<boolean> {
  const { data } = await client.get<ApiResponse<{ authenticated: boolean }>>(
    `/import/${pluginId}/auth/status`,
  )
  return data.data?.authenticated ?? false
}

export async function importHistory(
  pluginId: string,
  since?: string,
): Promise<ImportResult> {
  const { data } = await client.post<ApiResponse<ImportResult>>(
    `/import/${pluginId}/history`,
    null,
    { params: since ? { since } : undefined },
  )
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Import failed')
  return data.data
}

export async function importRatings(pluginId: string): Promise<ImportResult> {
  const { data } = await client.post<ApiResponse<ImportResult>>(`/import/${pluginId}/ratings`)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Import failed')
  return data.data
}

export async function importWatchlist(pluginId: string): Promise<ImportResult> {
  const { data } = await client.post<ApiResponse<ImportResult>>(`/import/${pluginId}/watchlist`)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Import failed')
  return data.data
}

/**
 * Fire a sync job and return the jobId immediately.
 * The caller is responsible for polling getSyncJobStatus() until done.
 */
export async function startSyncJob(pluginId: string, fullSync: boolean): Promise<string> {
  const { data } = await client.post<ApiResponse<{ jobId: string }>>(
    `/sync/${pluginId}`,
    null,
    { params: { fullSync } },
  )
  if (!data.success || !data.data?.jobId)
    throw new Error(data.error?.message ?? 'Failed to start sync')
  return data.data.jobId
}

/** Poll a sync job for its current status. */
export async function getSyncJobStatus(pluginId: string, jobId: string): Promise<SyncJobStatus> {
  const { data } = await client.get<ApiResponse<SyncJobStatus>>(
    `/sync/${pluginId}/job/${jobId}`,
  )
  if (!data.success || !data.data)
    throw new Error(data.error?.message ?? 'Failed to get sync status')
  return data.data
}

/**
 * Convenience wrapper used by PluginsPage: fire-and-forget + poll loop in one call.
 * BackgroundTasksPage uses startSyncJob + getSyncJobStatus directly instead.
 */
export async function triggerSync(pluginId: string, fullSync: boolean): Promise<SyncResult> {
  const jobId = await startSyncJob(pluginId, fullSync)

  for (;;) {
    await new Promise(r => setTimeout(r, 3_000))
    const snap = await getSyncJobStatus(pluginId, jobId)
    if (snap.status === 'complete') {
      if (!snap.summary) throw new Error('Sync completed but returned no summary')
      return snap.summary
    }
    if (snap.status === 'failed') throw new Error(snap.error ?? 'Sync failed')
    // 'running' → keep polling
  }
}
