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
})
