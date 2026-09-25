import { describe, it, expect } from 'vitest'
import { formatExternalId, displayExternalIds } from './externalId'

describe('formatExternalId', () => {
  it.each([
    ['tmdb', 'movie:4985', 'TMDB', '4985'],
    ['tmdb', 'tmdb:4985', 'TMDB', '4985'],
    ['tmdb', '4985', 'TMDB', '4985'],
    ['simkl', 'simkl:movie:58746', 'Simkl', '58746'],
    ['simkl', 'simkl:58746', 'Simkl', '58746'],
    ['imdb', 'tt0071771', 'IMDb', 'tt0071771'],
    ['tmdb', 'person:1856011', 'TMDB', '1856011'],
  ])('%s %s -> %s %s', (source, id, label, value) => {
    expect(formatExternalId(source, id)).toEqual({ label, value })
  })

  it('keeps the language and title of a Wikipedia id', () => {
    expect(formatExternalId('wikipedia', 'wikipedia:en:The_Longest_Yard_(1974_film)'))
      .toEqual({ label: 'Wikipedia', value: 'en:The_Longest_Yard_(1974_film)' })
  })
})

describe('displayExternalIds', () => {
  it('shows one id recorded under two shapes only once', () => {
    const out = displayExternalIds([
      { source: 'tmdb', externalId: 'movie:4985' },
      { source: 'tmdb', externalId: 'tmdb:4985' },
    ])
    expect(out).toHaveLength(1)
  })
})
