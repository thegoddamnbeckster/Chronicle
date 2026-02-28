import client from './client'
import type { ApiResponse, ImportProvider, ImportAuthStart, ImportPollResult, ImportResult } from '@/types'

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
