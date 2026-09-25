import { describe, it, expect } from 'vitest'
import { thumbnailUrl } from './thumbnailUrl'

describe('thumbnailUrl', () => {
  it('shrinks a TMDB original to a tile-sized width', () => {
    expect(thumbnailUrl('https://image.tmdb.org/t/p/original/abc.jpg')).toBe('https://image.tmdb.org/t/p/w342/abc.jpg')
    expect(thumbnailUrl('https://image.tmdb.org/t/p/w1280/abc.jpg')).toBe('https://image.tmdb.org/t/p/w342/abc.jpg')
  })

  it('turns a Wikimedia original into its resized thumb URL', () => {
    expect(thumbnailUrl('https://upload.wikimedia.org/wikipedia/commons/1/12/Phil_Davis_2016.jpg', 220))
      .toBe('https://upload.wikimedia.org/wikipedia/commons/thumb/1/12/Phil_Davis_2016.jpg/220px-Phil_Davis_2016.jpg')
  })

  it('leaves an already-thumbed Wikimedia URL, any other host, and empty values alone', () => {
    const thumbed = 'https://upload.wikimedia.org/wikipedia/commons/thumb/1/12/X.jpg/220px-X.jpg'
    expect(thumbnailUrl(thumbed)).toBe(thumbed)
    expect(thumbnailUrl('https://example.com/a.jpg')).toBe('https://example.com/a.jpg')
    expect(thumbnailUrl(null)).toBeNull()
  })
})
