import axios from 'axios'
import client, { ApiError } from './client'
import type { ApiResponse, MediaItem, MediaTypeOption, NfoDetail } from '@/types'

export async function getMediaTypes(): Promise<MediaTypeOption[]> {
  const { data } = await client.get<ApiResponse<MediaTypeOption[]>>('/media/types')
  return data.data ?? []
}

export async function searchMedia(query: string, mediaTypeId?: number, page = 1, allLevels = false): Promise<MediaItem[]> {
  const { data } = await client.get<ApiResponse<MediaItem[]>>('/media/search', {
    params: { query, mediaTypeId, page, perPage: 20, allLevels: allLevels || undefined },
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

/** Parses the rich display fields from the item's .nfo sidecar, if one was found. */
export async function getNfoDetail(id: number): Promise<NfoDetail | null> {
  try {
    const { data } = await client.get<ApiResponse<NfoDetail>>(`/media/${id}/nfo`)
    return data.data ?? null
  } catch (err: unknown) {
    if (err instanceof ApiError && err.statusCode === 404) return null
    if (axios.isAxiosError(err) && err.response?.status === 404) return null
    throw err
  }
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
  /** True when creating an intentional collection container (Add Collection page) so the
   *  server can tag it unambiguously even before it has any members or a real TMDB link. */
  isCollection?: boolean
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
    if (err instanceof ApiError && err.statusCode === 409 && err.errorCode === 'NO_PROVIDER_CONFIGURED') {
      throw new Error('No metadata provider configured. Add an API key in Settings → Plugins.')
    }
    throw err
  }
}

export async function clearMediaExternalId(id: number, source: string): Promise<void> {
  await client.delete(`/media/${id}/external-ids/${encodeURIComponent(source)}`)
}

export async function suppressMediaMatch(id: number, source: string): Promise<void> {
  await client.post(`/media/${id}/suppress/${encodeURIComponent(source)}`)
}

export async function changeMediaType(id: number, mediaTypeId: number): Promise<void> {
  await client.post(`/media/${id}/change-type`, { mediaTypeId })
}

export async function unparentFromCollection(id: number): Promise<MediaItem> {
  const { data } = await client.post<ApiResponse<MediaItem>>(`/media/${id}/unparent`)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to remove from collection')
  return data.data
}

export async function reparentToCollection(id: number, collectionId: number): Promise<MediaItem> {
  const { data } = await client.post<ApiResponse<MediaItem>>(`/media/${id}/reparent`, { collectionId })
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to add to collection')
  return data.data
}

export interface CollectionSummary {
  id: number
  name: string
  posterUrl: string | null
  itemCount: number
  mediaTypeId: number
}

export async function getCollections(): Promise<CollectionSummary[]> {
  const { data } = await client.get<ApiResponse<CollectionSummary[]>>('/media/collections')
  return data.data ?? []
}

/**
 * Pins a manually-chosen value for one canonical field (e.g. "poster_url") on an item — it
 * wins over the plugin-priority resolution walk in every future refresh/sync until cleared.
 * Returns the fully re-resolved item so callers can update their cache without a refetch.
 */
export async function setMediaOverride(
  id: number,
  field: string,
  url: string,
  sourcePluginId?: string,
  sourceType?: string,
): Promise<MediaItem> {
  const { data } = await client.put<ApiResponse<MediaItem>>(
    `/media/${id}/overrides/${encodeURIComponent(field)}`,
    { url, sourcePluginId, sourceType },
  )
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to set override')
  return data.data
}

/** Clears one field's override on an item (idempotent). Returns the re-resolved item. */
export async function clearMediaOverride(id: number, field: string): Promise<MediaItem> {
  const { data } = await client.delete<ApiResponse<MediaItem>>(
    `/media/${id}/overrides/${encodeURIComponent(field)}`,
  )
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to clear override')
  return data.data
}

/** Clears every override on an item. Returns the re-resolved item. */
export async function clearAllMediaOverrides(id: number): Promise<MediaItem> {
  const { data } = await client.delete<ApiResponse<MediaItem>>(`/media/${id}/overrides`)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to clear overrides')
  return data.data
}

export interface OverrideResetProgress {
  isRunning: boolean
  isComplete: boolean
  scope: string | null
  processed: number
  cleared: number
  error: string | null
}

/** Starts a background job clearing every image/field override for a media type. Admin only. */
export async function resetOverridesForMediaType(mediaTypeId: number): Promise<void> {
  await client.post(`/media/overrides/reset-media-type/${mediaTypeId}`)
}

/** Starts a background job clearing every image/field override library-wide. Admin only. */
export async function resetAllOverrides(confirmationToken: string): Promise<void> {
  await client.post('/media/overrides/reset-all', { confirmationToken })
}

/** Polls the state of the current (or most recent) bulk override-reset job. */
export async function getOverrideResetProgress(): Promise<OverrideResetProgress> {
  const { data } = await client.get<ApiResponse<OverrideResetProgress>>('/media/overrides/reset-progress')
  if (!data.data) throw new Error('Failed to fetch reset progress')
  return data.data
}

/**
 * Refreshes metadata for a single item from a specific plugin.
 * If `input` is provided, performs a Fix Match (overrides external ID lookup).
 * If `input` is omitted/null, re-fetches using the item's existing stored external ID.
 */
export async function refreshMediaForPlugin(
  id: number,
  pluginId: string,
  input?: string,
): Promise<MediaItem> {
  try {
    const body = input !== undefined ? { input } : undefined
    const { data } = await client.post<ApiResponse<MediaItem>>(
      `/media/${id}/refresh/${encodeURIComponent(pluginId)}`,
      body,
    )
    if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Refresh failed')
    return data.data
  } catch (err: unknown) {
    if (err instanceof ApiError && err.statusCode === 409 && err.errorCode === 'NO_PROVIDER_CONFIGURED') {
      throw new Error('No metadata provider configured. Add an API key in Settings → Plugins.')
    }
    throw err
  }
}
