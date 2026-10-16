import type { MediaItem } from '@/types'

/**
 * The order an item's children are listed in: a movie collection oldest release first; everything else by series
 * position (preferring the precise, possibly fractional seriesPosition over the floored number), unpositioned
 * items after the positioned ones, then by name with numbers read as numbers ("E2" before "E10"). One function so
 * the grid on a detail page and Previous/Next derived from the same parent can never disagree.
 */
export function sortChildItems(children: MediaItem[], byYear: boolean): MediaItem[] {
  return [...children].sort((a, b) => {
    if (byYear) {
      const ya = a.year ?? 9999
      const yb = b.year ?? 9999
      return ya !== yb ? ya - yb : a.name.localeCompare(b.name)
    }
    const pa = a.seriesPosition ?? a.number
    const pb = b.seriesPosition ?? b.number
    if (pa != null && pb != null) return pa - pb
    if (pa != null) return -1
    if (pb != null) return 1
    return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' })
  })
}
