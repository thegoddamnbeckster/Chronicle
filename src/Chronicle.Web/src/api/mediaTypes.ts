import client from './client'
import type { ApiResponse } from '@/types'

export interface PluginRef { pluginId: string; name: string }

export interface MediaTypeAdmin {
  id: number
  name: string
  displayName: string
  description: string | null
  hierarchyLevels: number
  hierarchyLabels: string[]
  interactionVerb: string
  progressUnit: string
  isBuiltIn: boolean
  isActive: boolean
  supportsCollections: boolean
  isTrackable: boolean
  scanStrategy: string | null
  isUserModified: boolean
  itemCount: number
  plugins: PluginRef[]
  /** JSON describing what files of this type look like (used to spot a folder scanned as the wrong type). */
  scanHints: string | null
}

export interface MediaTypeInput {
  /** Only used when creating; the internal name cannot change afterwards. */
  name?: string
  displayName: string
  description: string
  hierarchyLevels: number
  hierarchyLabels: string[]
  interactionVerb: string
  progressUnit: string
  supportsCollections: boolean
  isTrackable: boolean
  scanStrategy: string | null
  isActive: boolean
  /** Omit to leave unchanged; an empty string clears. */
  scanHints?: string
}

export const SCAN_STRATEGIES = [
  { value: '', label: 'Automatic (by number of levels)' },
  { value: 'audiobook', label: 'Audiobook (one book per folder)' },
]

export async function listMediaTypesAdmin(): Promise<MediaTypeAdmin[]> {
  const { data } = await client.get<ApiResponse<MediaTypeAdmin[]>>('/media-types')
  return data.data ?? []
}

export async function createMediaType(input: MediaTypeInput): Promise<MediaTypeAdmin> {
  const { data } = await client.post<ApiResponse<MediaTypeAdmin>>('/media-types', input)
  return data.data!
}

export async function updateMediaType(id: number, input: MediaTypeInput): Promise<MediaTypeAdmin> {
  const { data } = await client.put<ApiResponse<MediaTypeAdmin>>(`/media-types/${id}`, input)
  return data.data!
}

export async function releaseMediaType(id: number): Promise<MediaTypeAdmin> {
  const { data } = await client.post<ApiResponse<MediaTypeAdmin>>(`/media-types/${id}/release-to-plugins`)
  return data.data!
}

export async function deleteMediaType(id: number): Promise<void> {
  await client.delete(`/media-types/${id}`)
}
