/**
 * One display form for an external id, shared by every screen that lists them (Duplicates page,
 * merge dialog). The same id is stored in several shapes depending on which write path recorded it
 * -- "movie:4985", "tmdb:4985", "simkl:movie:58746", "wikipedia:en:The_Longest_Yard_(1974_film)",
 * bare "tt0071771" -- so showing them raw made two cards for one film look like they disagreed.
 * Returns { label, value }: the source in a fixed short form and the id with any self-describing
 * prefix stripped.
 */
export interface ExternalIdDisplay {
  label: string
  value: string
}

const SOURCE_LABELS: Record<string, string> = {
  tmdb: 'TMDB', imdb: 'IMDb', tvdb: 'TVDB', musicbrainz: 'MusicBrainz', simkl: 'Simkl',
  trakt: 'Trakt', hardcover: 'Hardcover', igdb: 'IGDB', fanarttv: 'Fanart.tv', wikipedia: 'Wikipedia',
  tvmaze: 'TVmaze',
}

// Leading tokens that only describe the id's kind, never part of the id itself.
const KIND_PREFIXES = ['movie', 'tv', 'show', 'person', 'episode', 'series']

export function formatExternalId(source: string, externalId: string): ExternalIdDisplay {
  const src = source.toLowerCase()
  const label = SOURCE_LABELS[src] ?? source

  let value = externalId
  // Wikipedia ids are "wikipedia:{lang}:{Title}" -- keep the language and title.
  if (src === 'wikipedia') {
    value = value.replace(/^wikipedia:/i, '')
    return { label, value }
  }
  // Strip "<source>:" and then any kind prefix, in any order they were written.
  let changed = true
  while (changed) {
    changed = false
    const m = /^([a-z]+):(.+)$/i.exec(value)
    if (m && (m[1].toLowerCase() === src || KIND_PREFIXES.includes(m[1].toLowerCase()))) {
      value = m[2]
      changed = true
    }
  }
  return { label, value }
}

/** De-duplicated display list: the same id recorded under two shapes shows once. */
export function displayExternalIds(
  ids: { source: string; externalId: string }[],
): (ExternalIdDisplay & { key: string })[] {
  const seen = new Set<string>()
  const out: (ExternalIdDisplay & { key: string })[] = []
  for (const e of ids) {
    const d = formatExternalId(e.source, e.externalId)
    const key = `${d.label}:${d.value}`
    if (seen.has(key)) continue
    seen.add(key)
    out.push({ ...d, key })
  }
  return out
}
