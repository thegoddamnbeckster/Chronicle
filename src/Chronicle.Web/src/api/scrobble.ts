import client from './client'
import type { ApiResponse, HistoryItem } from '@/types'

export async function scrobble(payload: {
  mediaItemId: number
  progressPercent?: number
  timestamp?: string
  deviceName?: string
}): Promise<void> {
  await client.post('/scrobble', payload)
}

export async function getHistory(page = 1): Promise<HistoryItem[]> {
  const { data } = await client.get<ApiResponse<HistoryItem[]>>('/scrobble/history', {
    params: { page, perPage: 20 },
  })
  return data.data ?? []
}
