import axios from 'axios'
import client from './client'
import type { ApiResponse, MediaItem, MediaTypeOption } from '@/types'

export async function getMediaTypes(): Promise<MediaTypeOption[]> {
  const { data } = await client.get<ApiResponse<MediaTypeOption[]>>('/media/types')
  return data.data ?? []
}

export async function searchMedia(query: string, mediaTypeId?: number, page = 1): Promise<MediaItem[]> {
  const { data } = await client.get<ApiResponse<MediaItem[]>>('/media/search', {
    params: { query, mediaTypeId, page, perPage: 20 },
  })
  return data.data ?? []
}

export async function getMedia(id: number): Promise<MediaItem> {
  const { data } = await client.get<ApiResponse<MediaItem>>(`/media/${id}`)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Media not found')
  return data.data
}

export async function getMediaChildren(id: number): Promise<MediaItem[]> {
  const { data } = await client.get<ApiResponse<MediaItem[]>>(`/media/${id}/children`)
  return data.data ?? []
}

export async function createMedia(payload: {
  mediaTypeId: number
  parentId?: number
  name: string
  year?: number
  overview?: string
  posterUrl?: string
  runtimeMinutes?: number
  hierarchyLevel?: number
  number?: number
}): Promise<MediaItem> {
  const { data } = await client.post<ApiResponse<MediaItem>>('/media', payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to create media')
  return data.data
}

export async function updateMedia(id: number, payload: Partial<MediaItem>): Promise<MediaItem> {
  const { data } = await client.patch<ApiResponse<MediaItem>>(`/media/${id}`, payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to update media')
  return data.data
}

export async function deleteMedia(id: number): Promise<void> {
  await client.delete(`/media/${id}`)
}

export async function refreshMedia(id: number): Promise<MediaItem> {
  try {
    const { data } = await client.post<ApiResponse<MediaItem>>(`/media/${id}/refresh`)
    if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Refresh failed')
    return data.data
  } catch (err: unknown) {
    if (axios.isAxiosError(err) && err.response?.status === 409) {
      const apiResp = err.response.data as ApiResponse<unknown>
      if (apiResp?.error?.code === 'NO_PROVIDER_CONFIGURED') {
        throw new Error('No metadata provider configured. Add an API key in Settings → Plugins.')
      }
    }
    throw err
  }
}
