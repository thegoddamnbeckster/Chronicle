import client from './client'
import type { ApiResponse, FileScanStatus, FileScanResult } from '@/types'

export async function getScanStatus(): Promise<FileScanStatus> {
  const { data } = await client.get<ApiResponse<FileScanStatus>>('/scan/status')
  return data.data ?? { available: false, supportedMediaTypeNames: [] }
}

export async function runScan(payload: {
  path: string
  recursive: boolean
  mediaTypeId: number
  confidenceThreshold: number
}): Promise<FileScanResult> {
  const { data } = await client.post<ApiResponse<FileScanResult>>('/scan', payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Scan failed')
  return data.data
}
