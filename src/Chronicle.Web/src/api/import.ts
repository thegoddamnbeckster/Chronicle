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

const SYNC_POLL_INTERVAL_MS = 3000

/** Start a sync and poll until it completes. Resolves with the final SyncResult. */
export async function triggerSync(pluginId: string, fullSync: boolean): Promise<SyncResult> {
  // Fire the sync — server returns 202 Accepted with a jobId immediately.
  const { data: startData } = await client.post<ApiResponse<{ jobId: string }>>(
    `/sync/${pluginId}`,
    null,
    { params: { fullSync } },
  )
  if (!startData.success || !startData.data?.jobId)
    throw new Error(startData.error?.message ?? 'Failed to start sync')

  const { jobId } = startData.data

  // Poll until the job finishes.
  for (;;) {
    await new Promise(r => setTimeout(r, SYNC_POLL_INTERVAL_MS))

    const { data: pollData } = await client.get<ApiResponse<SyncJobStatus>>(
      `/sync/${pluginId}/job/${jobId}`,
    )
    if (!pollData.success || !pollData.data)
      throw new Error(pollData.error?.message ?? 'Failed to poll sync status')

    const snap = pollData.data
    if (snap.status === 'complete') {
      if (!snap.summary) throw new Error('Sync completed but returned no summary')
      return snap.summary
    }
    if (snap.status === 'failed') {
      throw new Error(snap.error ?? 'Sync failed')
    }
    // status === 'running' → keep polling
  }
}
