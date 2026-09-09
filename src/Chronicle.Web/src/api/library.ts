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

// Single-item counterpart to getLibrary() -- for a page that only needs one item's own entry
// (e.g. the media detail page), not the whole catalog. Root-caused live (2026-09-08): the
// detail page used to call getLibrary() with no args, which fetches literally every trackable
// item's library row (~88,500 entries, ~140MB of JSON at this catalog's size) just to find one
// match client-side -- for all practical purposes never completing, so a completed/rated item
// displayed as if it had never been tracked. Returns null (not an error) when the item isn't
// tracked yet -- that's the normal, expected "not in library" state.
export async function getLibraryEntryForMedia(mediaItemId: number): Promise<LibraryEntry | null> {
  const { data } = await client.get<ApiResponse<LibraryEntry | null>>(`/library/by-media/${mediaItemId}`)
  return data.data ?? null
}

// Batched counterpart to getLibraryEntryForMedia, for a small known set of ids (e.g. a media
// detail page's own children -- episodes within a season) that each need their own
// progress/status. An id with no library entry simply isn't present in the result.
export async function getLibraryEntriesForMediaIds(mediaItemIds: number[]): Promise<LibraryEntry[]> {
  if (mediaItemIds.length === 0) return []
  const { data } = await client.get<ApiResponse<LibraryEntry[]>>('/library/by-media', {
    params: { ids: mediaItemIds.join(',') },
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
