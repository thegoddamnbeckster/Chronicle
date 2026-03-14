import client from './client'
import type { ApiResponse } from '@/types'

export interface FilesystemEntry {
  name: string
  path: string
}

export interface FilesystemListing {
  path: string | null
  parent: string | null
  directories: FilesystemEntry[]
}

export async function listDirectory(path: string): Promise<FilesystemListing> {
  const params = path ? { path } : {}
  const { data } = await client.get<ApiResponse<FilesystemListing>>('/filesystem', { params })
  if (!data.success || !data.data)
    throw new Error(data.error?.message ?? 'Failed to list directory')
  return data.data
}
