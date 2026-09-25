/**
 * Smaller-image URL for a tile. Cast/people tiles are ~150px wide but their photo URLs are the
 * providers' full-size originals (TMDB "original" is several MB, Wikimedia originals can be tens of
 * MB), so a page of tiles took minutes to fill in. TMDB and Wikimedia both serve resized copies from
 * the same URL scheme; any other URL is returned unchanged.
 */
const TMDB_SIZE = /^(https?:\/\/image\.tmdb\.org\/t\/p\/)(original|w\d+|h\d+)(\/.+)$/i
const WIKIMEDIA_ORIGINAL = /^(https?:\/\/upload\.wikimedia\.org\/wikipedia\/(?:commons|[a-z-]+))\/((?:[0-9a-f]\/[0-9a-f]{2})\/([^/]+\.(?:jpe?g|png)))$/i

export function thumbnailUrl(url: string | null | undefined, width = 342): string | null | undefined {
  if (!url) return url
  const tmdb = TMDB_SIZE.exec(url)
  if (tmdb) {
    // TMDB only offers a fixed ladder of widths; 342 is the smallest that still looks sharp on a retina tile.
    return `${tmdb[1]}w342${tmdb[3]}`
  }
  const wiki = WIKIMEDIA_ORIGINAL.exec(url)
  if (wiki) return `${wiki[1]}/thumb/${wiki[2]}/${width}px-${wiki[3]}`
  return url
}
