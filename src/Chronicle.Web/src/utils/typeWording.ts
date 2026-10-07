import type { LibraryStatus, MediaTypeOption } from '@/types'

/** The four forms of an action word the interface needs: "Plan to ___", "___ing", "Re___ing", "Un___". */
interface VerbWords { act: string; doing: string; again: string; not: string }

/**
 * Wording for the action words the interface knows. This is a table of VERBS, not of media types: a type chooses its
 * verb (Settings -> Media Types), and any type using "listened" reads "Listening" - whatever it is called. A verb not in
 * the table gets neutral wording rather than a guess at its grammar.
 */
const KNOWN_VERBS: Record<string, VerbWords> = {
  watched:  { act: 'Watch',  doing: 'Watching',  again: 'Rewatching',   not: 'Unwatched' },
  listened: { act: 'Listen', doing: 'Listening', again: 'Re-listening', not: 'Unlistened' },
  read:     { act: 'Read',   doing: 'Reading',   again: 'Re-reading',   not: 'Unread' },
  played:   { act: 'Play',   doing: 'Playing',   again: 'Replaying',    not: 'Unplayed' },
}

/** Does the interface have specific wording for this action word? */
export function isKnownVerb(verb: string | undefined): boolean {
  return !!verb && verb.toLowerCase() in KNOWN_VERBS
}

/** A status worded for ONE type, from the action word that type uses. */
export function statusLabel(status: LibraryStatus, verb: string | undefined): string {
  const v = (verb ?? 'watched').toLowerCase()
  const known = KNOWN_VERBS[v]
  switch (status) {
    case 'PlanToWatch': return known ? `Plan to ${known.act}` : 'Planned'
    case 'Watching':    return known ? known.doing : 'In progress'
    case 'Rewatching':  return known ? known.again : 'Repeating'
    case 'Unwatched':   return known ? known.not : `Not yet ${v}`
    case 'Completed':   return 'Completed'
    case 'Dropped':     return 'Dropped'
    case 'OnHold':      return 'On Hold'
    default:            return status
  }
}

/** A status worded for views that mix every type (filters, settings, dashboards): no action word at all. */
export function neutralStatusLabel(status: LibraryStatus | string): string {
  switch (status) {
    case 'Unwatched':   return 'Not started'
    case 'Watching':    return 'In progress'
    case 'PlanToWatch': return 'Planned'
    case 'Rewatching':  return 'Repeating'
    case 'Completed':   return 'Completed'
    case 'Dropped':     return 'Dropped'
    case 'OnHold':      return 'On Hold'
    default:            return String(status)
  }
}

/** "Season" -> "Seasons", "Category" -> "Categories", "Series" stays "Series". */
export function pluralise(label: string): string {
  const l = label.trim()
  if (!l) return l
  if (/s$/i.test(l) && /(ies|series|species|news)$/i.test(l)) return l
  if (/[^aeiou]y$/i.test(l)) return l.slice(0, -1) + 'ies'
  if (/(s|x|z|ch|sh)$/i.test(l)) return l + 'es'
  return l + 's'
}

/**
 * The heading for the children of an item: the plural of the next level's name for that item's type
 * ("Seasons" under a Show, "Tracks" under an Album). A bucket type (a movie collection) holds more of its own top
 * level, so its children are named after that. Falls back to "Items" when the type does not say.
 */
export function childrenLabel(type: MediaTypeOption | undefined, ancestorCount: number): string {
  const labels = type?.hierarchyLabels ?? []
  const childLevel = ancestorCount + 1
  if (childLevel < labels.length) return pluralise(labels[childLevel])
  if (type?.supportsCollections && labels.length > 0) return pluralise(labels[0])
  return 'Items'
}
