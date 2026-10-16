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
  /** "tv", "movie", "music"...: providers of this family also serve the type. */
  providerFamily: string | null
  castHeading: string | null
  /** The one type that holds the people credited on other media. */
  isPersonType: boolean
  /** The type used when a client does not say what kind of item it means. */
  isDefaultType: boolean
  /** The type new items get under when only the provider family is known (Kodi's movie scraper adding a film). */
  isFamilyDefault: boolean
  /** True when a cast entry is an actor playing a character, false when the entry's role is the contribution. */
  castAreActors: boolean
  /** Other names clients use for this type, comma-separated ("film,movie"). */
  aliases: string | null
  /** How imported plays are filed: show/season/episode, author/series/book or artist/album/track. */
  importShape: string | null
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
  providerFamily?: string
  castHeading?: string
  isPersonType?: boolean
  isDefaultType?: boolean
  isFamilyDefault?: boolean
  castAreActors?: boolean
  /** Omit to leave unchanged; an empty string clears. */
  aliases?: string
  importShape?: string
}

export const IMPORT_SHAPES = [
  { value: '', label: 'Flat (each play is one item)' },
  { value: 'episodic', label: 'Show / season / episode' },
  { value: 'author-series-work', label: 'Author / series / book' },
  { value: 'artist-album-track', label: 'Artist / album / track' },
]

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
