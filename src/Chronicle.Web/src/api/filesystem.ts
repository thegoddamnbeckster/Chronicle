import client, { ApiError } from './client'
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
    throw new ApiError(data.error?.message ?? 'Failed to list directory', 0, data.error?.code)
  return data.data
}
