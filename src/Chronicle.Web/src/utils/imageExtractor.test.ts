import { describe, it, expect } from 'vitest'
import { extractSlottedImages } from './imageExtractor'

describe('extractSlottedImages', () => {
  it('shows an image whose type has no slot mapping instead of dropping it (Open Library "cover")', () => {
    const imgs = extractSlottedImages('chronicle.plugin.openlibrary', {
      additionalImages: [
        { url: 'https://covers.openlibrary.org/b/id/1-L.jpg', type: 'cover' },
        { url: 'https://covers.openlibrary.org/b/id/2-L.jpg', type: 'some-new-type' },
        { url: 'https://covers.openlibrary.org/b/id/3-L.jpg' },
      ],
    })
    expect(imgs.map(i => i.url)).toHaveLength(3)
    expect(imgs.every(i => i.slot === 'poster_url')).toBe(true)
  })

  it('still puts known types in their own slot', () => {
    const imgs = extractSlottedImages('x', {
      additionalImages: [
        { url: 'https://a/1.png', type: 'CDart' },
        { url: 'https://a/2.png', type: 'Cutout' },
        { url: 'https://a/3.jpg', type: 'Fanart' },
      ],
    })
    expect(imgs.map(i => i.slot)).toEqual(['disc_url', 'character_art_url', 'backdrop_url'])
  })

  it('puts game screenshots with the backdrops, not under Posters where they would bury the cover (IGDB "Screenshot")', () => {
    const imgs = extractSlottedImages('chronicle.plugin.igdb', {
      posterUrl: 'https://images.igdb.com/igdb/image/upload/t_cover_big_2x/co1u60.jpg',
      additionalImages: [
        { url: 'https://images.igdb.com/igdb/image/upload/t_1080p/s1.jpg', type: 'Screenshot' },
        { url: 'https://images.igdb.com/igdb/image/upload/t_1080p/a1.jpg', type: 'Fanart' },
        { url: 'https://images.igdb.com/igdb/image/upload/t_1080p/c1.jpg', type: 'Front' },
      ],
    })
    expect(imgs.find(i => i.url.endsWith('s1.jpg'))?.slot).toBe('backdrop_url')
    expect(imgs.find(i => i.url.endsWith('a1.jpg'))?.slot).toBe('backdrop_url')
    expect(imgs.find(i => i.url.endsWith('c1.jpg'))?.slot).toBe('poster_url')
    expect(imgs.find(i => i.url.endsWith('co1u60.jpg'))?.slot).toBe('poster_url')
  })
})
