import client from './client'
import type { ApiResponse, LibraryEntry, LibraryStatus } from '@/types'

export async function getLibrary(status?: LibraryStatus, page = 1): Promise<LibraryEntry[]> {
  const { data } = await client.get<ApiResponse<LibraryEntry[]>>('/library', {
    params: { status, page, perPage: 50 },
  })
  return data.data ?? []
}

export async function addToLibrary(mediaItemId: number, status: LibraryStatus = 'PlanToWatch'): Promise<LibraryEntry> {
  const { data } = await client.post<ApiResponse<LibraryEntry>>('/library', { mediaItemId, status })
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to add to library')
  return data.data
}

export async function updateLibraryEntry(
  id: number,
  payload: { status?: LibraryStatus; userRating?: number; notes?: string },
): Promise<LibraryEntry> {
  const { data } = await client.patch<ApiResponse<LibraryEntry>>(`/library/${id}`, payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to update entry')
  return data.data
}

export async function removeFromLibrary(id: number): Promise<void> {
  await client.delete(`/library/${id}`)
}
