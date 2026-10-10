import client from './client'
import type { ApiResponse } from '@/types'

export interface TableSize {
  name: string
  bytes: number
  rows: number | null
}

export interface DatabaseStatus {
  supported: boolean
  provider: string
  note: string | null
  databaseFile: string | null
  databaseBytes: number
  walBytes: number
  pageSize: number
  pageCount: number
  freePages: number
  reclaimableBytes: number
  freeDiskBytes: number
  latestMigration: string | null
  backupDirectory: string | null
  backupCount: number
  retainCount: number
  lastBackupAtUtc: string | null
  warnSizeBytes: number
  overWarnSize: boolean
  largestTables: TableSize[]
}

/** "scheduled" | "manual" | "pre-restore" | "uploaded" */
export type BackupKind = string

export interface BackupInfo {
  fileName: string
  kind: BackupKind
  sizeBytes: number
  createdAtUtc: string
  latestMigration: string | null
  databaseBytes: number | null
  rowCounts: Record<string, number> | null
}

export interface BackupValidation {
  valid: boolean
  error: string | null
}

export interface MaintenanceResult {
  summary: string
  elapsed: string
  bytesBefore: number
  bytesAfter: number
}

/** The word an admin must type to restore. Mirrors DatabaseController.RestoreConfirmation. */
export const RESTORE_CONFIRMATION = 'RESTORE'

export async function getDatabaseStatus(): Promise<DatabaseStatus> {
  const { data } = await client.get<ApiResponse<DatabaseStatus>>('/database/status')
  return data.data!
}

export async function saveDatabaseSettings(settings: { backupsToKeep?: number; warnSizeMb?: number }): Promise<DatabaseStatus> {
  const { data } = await client.put<ApiResponse<DatabaseStatus>>('/database/settings', settings)
  return data.data!
}

export async function listBackups(): Promise<BackupInfo[]> {
  const { data } = await client.get<ApiResponse<BackupInfo[]>>('/database/backups')
  return data.data ?? []
}

export async function createBackup(): Promise<BackupInfo> {
  const { data } = await client.post<ApiResponse<BackupInfo>>('/database/backups', null, { timeout: 0 })
  return data.data!
}

export async function deleteBackup(fileName: string): Promise<void> {
  await client.delete(`/database/backups/${encodeURIComponent(fileName)}`)
}

export async function validateBackup(fileName: string): Promise<BackupValidation> {
  const { data } = await client.post<ApiResponse<BackupValidation>>(`/database/backups/${encodeURIComponent(fileName)}/validate`, null, { timeout: 0 })
  return data.data!
}

/**
 * Uploads a backup zip as the raw request body (the server refuses multipart on purpose - see
 * DatabaseController.Upload) and reports progress as a 0-100 percentage.
 */
export async function uploadBackup(file: File, onProgress?: (percent: number) => void): Promise<BackupInfo> {
  const { data } = await client.post<ApiResponse<BackupInfo>>('/database/backups/upload', file, {
    headers: { 'Content-Type': 'application/zip' },
    timeout: 0,
    onUploadProgress: e => {
      if (onProgress && e.total) onProgress(Math.round((e.loaded / e.total) * 100))
    },
  })
  return data.data!
}

/** Downloads through the API client (the session key is a header, which a plain link cannot send). */
export async function downloadBackup(fileName: string): Promise<void> {
  const { data } = await client.get<Blob>(`/database/backups/${encodeURIComponent(fileName)}/download`, {
    responseType: 'blob',
    timeout: 0,
  })
  const url = URL.createObjectURL(data)
  try {
    const a = document.createElement('a')
    a.href = url
    a.download = fileName
    document.body.appendChild(a)
    a.click()
    a.remove()
  } finally {
    URL.revokeObjectURL(url)
  }
}

export async function restoreBackup(fileName: string, confirm: string): Promise<void> {
  await client.post(`/database/backups/${encodeURIComponent(fileName)}/restore`, { confirm }, { timeout: 0 })
}

export async function runMaintenance(kind: 'quick' | 'full'): Promise<MaintenanceResult> {
  const { data } = await client.post<ApiResponse<MaintenanceResult>>(`/database/maintenance/${kind}`, null, { timeout: 0 })
  return data.data!
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '-'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = bytes
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v >= 10 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}`
}
