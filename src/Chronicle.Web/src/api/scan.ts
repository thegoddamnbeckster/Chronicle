import client from './client'
import type {
  ApiResponse,
  FileScanStatus,
  FileScanResult,
  ScannedFile,
  ScanPreview,
  IdentifyResult,
  ImportSummary,
  MetadataSearchResult,
  MediaItem,
} from '@/types'

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

export async function previewScan(payload: {
  path: string
  recursive: boolean
  mediaTypeId: number
}): Promise<ScanPreview> {
  const { data } = await client.post<ApiResponse<ScanPreview>>('/scan/preview', payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Preview failed')
  return data.data
}

export async function identifyFiles(payload: {
  files: ScannedFile[]
  mediaTypeId: number
}): Promise<IdentifyResult> {
  const { data } = await client.post<ApiResponse<IdentifyResult>>('/scan/identify', payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Identify failed')
  return data.data
}

export async function importApproved(payload: {
  approvals: { filePath: string; externalId: string }[]
  mediaTypeId: number
}): Promise<ImportSummary> {
  const { data } = await client.post<ApiResponse<ImportSummary>>('/scan/import', payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Import failed')
  return data.data
}

export async function importDirect(payload: {
  files: {
    filePath: string
    parsedTitle: string
    parsedYear: number | null
    suggestedExternalId: string | null
    mediaTypeHint: string
  }[]
  mediaTypeId: number
}): Promise<ImportSummary> {
  const { data } = await client.post<ApiResponse<ImportSummary>>('/scan/import-direct', payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Import failed')
  return data.data
}

export async function searchMetadata(
  query: string,
  mediaTypeHint: string,
): Promise<MetadataSearchResult[]> {
  const { data } = await client.get<ApiResponse<MetadataSearchResult[]>>('/scan/search', {
    params: { query, mediaTypeHint },
  })
  return data.data ?? []
}

export async function addFromSearch(
  externalId: string,
  mediaTypeId: number,
): Promise<MediaItem> {
  const { data } = await client.post<ApiResponse<MediaItem>>('/scan/add', { externalId, mediaTypeId })
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to add media')
  return data.data
}
