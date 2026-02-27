import client from './client'
import type { ApiResponse, UserStats } from '@/types'

export async function getStats(): Promise<UserStats> {
  const { data } = await client.get<ApiResponse<UserStats>>('/stats')
  if (!data.success || !data.data) throw new Error('Failed to load stats')
  return data.data
}
