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
  ContributingExternalId,
  MediaItem,
  ScanGroupResult,
  ImportGroupPayload,
  ScanFolder,
} from '@/types'

/**
 * Translates low-level network/proxy errors into actionable plain-English
 * messages. API errors (which already carry a server-supplied message via the
 * client interceptor) are passed through unchanged.
 */
function translateScanError(err: unknown): Error {
  if (err instanceof Error) {
    // Axios reports a dropped connection (e.g. proxy timeout, server restart)
    // as "Network Error" with no HTTP response attached.
    if (err.message === 'Network Error') {
      return new Error(
        'The scan did not complete in time. Your folder may contain a very ' +
          'large number of files — try scanning a smaller subfolder first. ' +
          'If the problem persists, check the Chronicle server log.',
      )
    }
  }
  return err instanceof Error ? err : new Error('Scan failed.')
}

export async function getScanStatus(): Promise<FileScanStatus> {
  const { data } = await client.get<ApiResponse<FileScanStatus>>('/scan/status')
  return data.data ?? { available: false, supportedMediaTypeNames: [] }
}

export interface ScanProgress {
  isScanning: boolean
  currentFolder: string | null
  foldersScanned: number
  totalFolders: number
  filesFound: number
}

export async function getScanProgress(): Promise<ScanProgress> {
  const { data } = await client.get<ApiResponse<ScanProgress>>('/scan/progress')
  return data.data ?? { isScanning: false, currentFolder: null, foldersScanned: 0, totalFolders: 0, filesFound: 0 }
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
  try {
    const { data } = await client.post<ApiResponse<ScanPreview>>('/scan/preview', payload)
    if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Preview failed')
    return data.data
  } catch (err) {
    throw translateScanError(err)
  }
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

export async function previewGrouped(payload: {
  path: string
  recursive: boolean
  mediaTypeId: number
}): Promise<ScanGroupResult> {
  try {
    const { data } = await client.post<ApiResponse<ScanGroupResult>>(
      '/scan/preview-grouped', payload)
    if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Preview failed')
    return data.data
  } catch (err) {
    throw translateScanError(err)
  }
}

export interface ImportProgressState {
  isRunning: boolean
  isComplete: boolean
  total: number
  processed: number
  currentItemName: string | null
  statusMessage: string | null
  error: string | null
  result: ImportSummary | null
}

export async function importGroups(payload: {
  groups: ImportGroupPayload[]
  mediaTypeId: number
}): Promise<{ started: boolean }> {
  const { data } = await client.post<ApiResponse<{ started: boolean }>>(
    '/scan/import-groups', payload, { signal: AbortSignal.timeout(12 * 60 * 60 * 1000) })
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Import failed')
  return data.data
}

export async function getImportProgress(): Promise<ImportProgressState> {
  const { data } = await client.get<ApiResponse<ImportProgressState>>('/scan/import-progress')
  return (
    data.data ?? {
      isRunning: false,
      isComplete: false,
      total: 0,
      processed: 0,
      currentItemName: null,
      statusMessage: null,
      error: null,
      result: null,
    }
  )
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
  contributingExternalIds?: ContributingExternalId[],
): Promise<MediaItem> {
  const { data } = await client.post<ApiResponse<MediaItem>>('/scan/add',
    { externalId, mediaTypeId, contributingExternalIds })
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to add media')
  return data.data
}

// ── Scan Folders ──────────────────────────────────────────────────────────────

export interface CreateScanFolderPayload {
  path: string;
  mediaTypeId: number;
  recursive: boolean;
}

export interface UpdateScanFolderPayload {
  path: string;
  mediaTypeId: number;
  recursive: boolean;
  isEnabled: boolean;
}

export interface PathValidationResult {
  valid: boolean;
  error: string | null;
}

export async function getScanFolders(): Promise<ScanFolder[]> {
  const { data } = await client.get<ApiResponse<ScanFolder[]>>('/scan-folders')
  return data.data ?? []
}

export async function createScanFolder(payload: CreateScanFolderPayload): Promise<ScanFolder> {
  const { data } = await client.post<ApiResponse<ScanFolder>>('/scan-folders', payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to create scan folder')
  return data.data
}

export async function updateScanFolder(id: number, payload: UpdateScanFolderPayload): Promise<ScanFolder> {
  const { data } = await client.put<ApiResponse<ScanFolder>>(`/scan-folders/${id}`, payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to update scan folder')
  return data.data
}

export async function deleteScanFolder(id: number): Promise<void> {
  await client.delete(`/scan-folders/${id}`)
}

export async function validatePath(path: string): Promise<PathValidationResult> {
  const { data } = await client.post<ApiResponse<PathValidationResult>>('/scan/validate-path', { path })
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Validation failed')
  return data.data
}
