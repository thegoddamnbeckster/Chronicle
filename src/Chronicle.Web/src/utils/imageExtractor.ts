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

/**
 * Renders one array element for a comma-joined summary line. Plain strings pass through
 * as-is. Cast/crew entries ({name, role} or {name, job}) render as "Name (Role)" — without
 * this, `String(obj)` on a plain object falls back to JS's default `[object Object]`.
 */
export function arrayItemToString(item: unknown): string {
  if (typeof item === 'string') return item
  if (typeof item === 'number' || typeof item === 'boolean') return String(item)
  if (item && typeof item === 'object') {
    const obj = item as Record<string, unknown>
    const name = obj.name
    if (typeof name === 'string') {
      const detail = obj.role ?? obj.job
      return typeof detail === 'string' && detail ? `${name} (${detail})` : name
    }
    return JSON.stringify(item)
  }
  return String(item)
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

// ── Slot-aware extraction (for the Additional Images / promote-to-first-class feature) ──
// Additive only — extractImages() above stays untouched for its existing callers
// (PluginMetadataBox's raw "Images" row, the page-level combined lightbox).

/** The 8 canonical artwork slots — same vocabulary as MetadataResolutionService.FieldMap
 *  on the backend (poster_url, backdrop_url, ...) and MediaItemDto.Overrides' keys. */
export type CanonicalSlot =
  | 'poster_url' | 'backdrop_url' | 'logo_url' | 'banner_url'
  | 'thumb_url' | 'clearart_url' | 'disc_url' | 'character_art_url'

export interface SlottedImageEntry extends ImageEntry {
  slot: CanonicalSlot
  pluginId: string
}

/** Display names per slot, plus the resolvedMetadata key each slot maps to. Shared by every
 *  surface that shows or promotes artwork (the Additional Images card, the type-scoped
 *  gallery, and the page-level lightbox) so a slot is never labelled two different ways. */
export const SLOT_INFO: Record<CanonicalSlot, { label: string; singular: string; resolvedKey: string }> = {
  poster_url:        { label: 'Posters',       singular: 'Poster',        resolvedKey: 'posterUrl' },
  backdrop_url:      { label: 'Backdrops',     singular: 'Backdrop',      resolvedKey: 'backdropUrl' },
  logo_url:          { label: 'Logos',         singular: 'Logo',          resolvedKey: 'logoUrl' },
  banner_url:        { label: 'Banners',       singular: 'Banner',        resolvedKey: 'bannerUrl' },
  thumb_url:         { label: 'Thumbs',        singular: 'Thumb',         resolvedKey: 'thumbUrl' },
  clearart_url:      { label: 'Clear Art',     singular: 'Clear Art',     resolvedKey: 'clearartUrl' },
  disc_url:          { label: 'Disc Art',      singular: 'Disc Art',      resolvedKey: 'discUrl' },
  character_art_url: { label: 'Character Art', singular: 'Character Art', resolvedKey: 'characterArtUrl' },
}

/** The canonical slot order used everywhere artwork slots are listed. */
export const SLOT_ORDER: CanonicalSlot[] = [
  'poster_url', 'backdrop_url', 'logo_url', 'banner_url',
  'thumb_url', 'clearart_url', 'disc_url', 'character_art_url',
]

/**
 * Builds a url -> slotted-entry lookup across every plugin's metadata. Lets a viewer that
 * only knows an image's URL (the page-level lightbox, which mixes every type together)
 * work out which slot that image could be promoted into.
 */
export function buildSlotLookup(
  pluginMetadata: Record<string, unknown> | null | undefined,
): Map<string, SlottedImageEntry> {
  const map = new Map<string, SlottedImageEntry>()
  for (const [pluginId, meta] of Object.entries(pluginMetadata ?? {})) {
    for (const img of extractSlottedImages(pluginId, meta as Record<string, unknown>)) {
      if (!map.has(img.url)) map.set(img.url, img)
    }
  }
  return map
}

/** Maps a raw AdditionalImage.Type (from Fanart.tv/MusicBrainz/FanEdit/etc.) or a top-level
 *  scalar field name to a canonical slot. Types absent here have no first-class slot and are
 *  excluded from the promote-eligible pool — they still render in the existing raw per-plugin
 *  "Images" row via extractImages(), unaffected. New artwork-supplying plugins need their
 *  Type strings added here to become promotable. */
const TYPE_TO_SLOT: Record<string, CanonicalSlot> = {
  poster: 'poster_url', posterurl: 'poster_url', front: 'poster_url',
  fanart: 'backdrop_url', backdrop: 'backdrop_url', backdropurl: 'backdrop_url',
  clearlogo: 'logo_url', logo: 'logo_url', logourl: 'logo_url',
  banner: 'banner_url', bannerurl: 'banner_url',
  thumb: 'thumb_url', thumburl: 'thumb_url', thumbnail: 'thumb_url', thumbnailurl: 'thumb_url',
  clearart: 'clearart_url', cleararturl: 'clearart_url',
  discart: 'disc_url', disc: 'disc_url', discurl: 'disc_url',
  characterart: 'character_art_url', character: 'character_art_url', characterarturl: 'character_art_url',
  // Wikipedia's own generic image type -- an article's photos/logos/icons carry no real
  // poster-vs-backdrop-vs-logo semantics of their own (unlike Fanart.tv/TMDB, which tag by
  // actual art category). Per-user request (2026-08-29): treat them the same as every other
  // plugin's images rather than excluding them from the promote-eligible pool entirely --
  // defaults into Posters, the same general-purpose bucket "front" already falls into, and
  // the user can re-pin any of them to a different slot from the picker like any other image.
  article: 'poster_url',
}

/**
 * Extracts every promote-eligible image from one plugin's raw metadata blob, tagged with its
 * canonical slot. Walks both the top-level scalar art fields (posterUrl, backdropUrl, ...)
 * and any additionalImages/images array (using each entry's own `type`). Entries whose type
 * has no canonical slot mapping are dropped.
 */
export function extractSlottedImages(
  pluginId: string,
  metadata: Record<string, unknown>,
): SlottedImageEntry[] {
  const out: SlottedImageEntry[] = []

  for (const [key, value] of Object.entries(metadata)) {
    const lower = key.toLowerCase()

    if (IMAGE_ARRAY_KEYS.has(lower) && Array.isArray(value)) {
      for (const img of value as Record<string, unknown>[]) {
        const url = (img.url ?? img.thumbnailUrl ?? '') as string
        if (!url) continue
        const type = typeof img.type === 'string' ? img.type.toLowerCase() : ''
        const slot = TYPE_TO_SLOT[type]
        if (!slot) continue
        out.push({ url, label: (img.type as string) ?? key, slot, pluginId })
      }
      continue
    }

    if (typeof value === 'string' && value && TYPE_TO_SLOT[lower]) {
      out.push({ url: value, label: toLabel(key), slot: TYPE_TO_SLOT[lower], pluginId })
    }
  }

  return out
}
