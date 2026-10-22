import { describe, it, expect } from 'vitest'
import { sourceUrlFor } from './sourceUrl'

const TMDB = [
  { match: '^movie:(\\d+)', url: 'https://www.themoviedb.org/movie/$1' },
  { match: '^person:(\\d+)', url: 'https://www.themoviedb.org/person/$1' },
]

describe('sourceUrlFor', () => {
  it('uses the first rule that matches and fills in the capture group', () => {
    expect(sourceUrlFor('person:1796481', TMDB)).toBe('https://www.themoviedb.org/person/1796481')
    expect(sourceUrlFor('movie:550', TMDB)).toBe('https://www.themoviedb.org/movie/550')
  })

  it('$0 is the whole id', () => {
    expect(sourceUrlFor('OL7630410A', [{ match: '^OL\\d+A$', url: 'https://openlibrary.org/authors/$0' }])).toBe('https://openlibrary.org/authors/OL7630410A')
  })

  it('is null when nothing matches, or there is no id or no rules', () => {
    expect(sourceUrlFor('tv:1', TMDB)).toBeNull()
    expect(sourceUrlFor(null, TMDB)).toBeNull()
    expect(sourceUrlFor('movie:1', [])).toBeNull()
    expect(sourceUrlFor('movie:1', undefined)).toBeNull()
  })

  it('ignores an invalid regex and keeps looking', () => {
    expect(sourceUrlFor('movie:5', [{ match: '(', url: 'https://x/$1' }, ...TMDB])).toBe('https://www.themoviedb.org/movie/5')
  })

  it('never returns a non-http address', () => {
    expect(sourceUrlFor('x:1', [{ match: '^x:(\\d+)', url: 'javascript:alert($1)' }])).toBeNull()
  })
})
