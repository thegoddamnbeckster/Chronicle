import client from './client'
import type { ApiResponse, LibraryEntry, LibraryStatus } from '@/types'

export async function getLibrary(
  status?: LibraryStatus,
  page = 1,
  perPage = 0,
  rootOnly = false,
  includeMoviesInCollections = false,
): Promise<LibraryEntry[]> {
  const params: Record<string, string | number | boolean> = { page, perPage, rootOnly }
  if (status) params.status = status
  if (includeMoviesInCollections) params.includeMoviesInCollections = true
  const { data } = await client.get<ApiResponse<LibraryEntry[]>>('/library', { params })
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

export async function clearScannerData(): Promise<{ deleted: number }> {
  const { data } = await client.post<ApiResponse<{ deleted: number }>>('/library/clear-scanner-data')
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed')
  return data.data
}

export async function nuclearReset(confirmationToken: string): Promise<{ deleted: number }> {
  const { data } = await client.post<ApiResponse<{ deleted: number }>>(
    '/library/reset', { confirmationToken })
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed')
  return data.data
}
