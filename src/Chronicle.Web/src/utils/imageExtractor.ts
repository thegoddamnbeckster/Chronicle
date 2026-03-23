/**
 * Shared utility for extracting image URLs from plugin metadata objects.
 * Used by both MediaDetailPage (page-level lightbox) and PluginMetadataBox.
 */

/** Keys whose string values are treated as image URLs. */
export const IMAGE_KEYS = new Set([
  'posterurl', 'backdropurl', 'posterpath', 'stillpath',
  'thumbnailurl', 'imageurl',
])

/** Keys that hold arrays of image objects (e.g. additionalImages). */
export const IMAGE_ARRAY_KEYS = new Set(['additionalimages', 'images'])

export interface ImageEntry {
  url: string
  label: string
}

const LABEL_MAP: Record<string, string> = {
  rating: 'Rating', voteaverage: 'Rating',
  genres: 'Genres', cast: 'Cast',
  directors: 'Director(s)', crew: 'Crew',
  gueststars: 'Guest Stars', guestStars: 'Guest Stars',
  airdate: 'Air Date', episodecount: 'Episodes',
  runtimeminutes: 'Duration', tags: 'Tags',
  overview: 'About', backdropurl: 'Backdrop',
  posterurl: 'Poster', posterpath: 'Season Poster',
  stillpath: 'Still',
}

export function toLabel(key: string): string {
  const lower = key.toLowerCase()
  if (LABEL_MAP[lower]) return LABEL_MAP[lower]
  return key
    .replace(/_/g, ' ')
    .replace(/([A-Z])/g, ' $1')
    .replace(/^\s/, '')
    .replace(/\b\w/g, c => c.toUpperCase())
    .trim()
}

export function buildImageUrl(key: string, value: string): string {
  const lower = key.toLowerCase()
  if ((lower === 'posterpath' || lower === 'stillpath') && value.startsWith('/'))
    return `https://image.tmdb.org/t/p/w500${value}`
  return value
}

export function isImageUrl(value: unknown): value is string {
  if (typeof value !== 'string') return false
  const v = value.toLowerCase()
  return v.startsWith('http') && (
    v.includes('.jpg') || v.includes('.jpeg') || v.includes('.png') ||
    v.includes('.webp') || v.includes('.gif') || v.includes('image.tmdb') ||
    v.includes('coverartarchive') || v.includes('musicbrainz.org/img') ||
    v.endsWith('/')
  )
}

/**
 * Extract all displayable image entries from a single plugin's metadata object.
 * @param metadata  The raw metadata record from the plugin.
 * @param skipKeys  Optional set of lowercase keys to ignore.
 */
export function extractImages(
  metadata: Record<string, unknown>,
  skipKeys: ReadonlySet<string> = new Set(),
): ImageEntry[] {
  const images: ImageEntry[] = []

  for (const [key, value] of Object.entries(metadata)) {
    const lower = key.toLowerCase()
    if (skipKeys.has(lower)) continue
    if (value === null || value === undefined) continue

    if (IMAGE_ARRAY_KEYS.has(lower) && Array.isArray(value)) {
      for (const img of value as Record<string, unknown>[]) {
        const url = (img.url ?? img.thumbnailUrl ?? '') as string
        if (url) images.push({ url, label: (img.type as string) ?? key })
      }
      continue
    }

    if (IMAGE_KEYS.has(lower) && typeof value === 'string') {
      images.push({ url: buildImageUrl(key, value), label: toLabel(key) })
      continue
    }

    if (typeof value === 'string' && isImageUrl(value)) {
      images.push({ url: value, label: toLabel(key) })
    }
  }

  return images
}
