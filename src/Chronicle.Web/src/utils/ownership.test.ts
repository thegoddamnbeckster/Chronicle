import { describe, it, expect } from 'vitest'
import { formatPlaytime, ownershipSummary, platformName } from './ownership'
import type { MediaOwnership } from '@/types'

const own = (over: Partial<MediaOwnership>): MediaOwnership => ({
  source: 'steam', platform: 'PC', playtimeMinutes: 0, lastPlayedAt: null, acquiredAt: null,
  ownedViaSubscription: false, isWishlist: false, tags: [], ...over,
})

describe('formatPlaytime', () => {
  it.each([
    [0, 'not played'], [4, '4 min'], [59, '59 min'], [60, '1 h'], [70, '1 h 10 min'], [599, '9 h 59 min'], [600, '10.0 hours'], [38220, '637.0 hours'],
  ])('%i minutes reads %s', (m, text) => expect(formatPlaytime(m)).toBe(text))
})

describe('platformName', () => {
  it('names the platforms it knows and capitalises the rest', () => {
    expect(platformName('steam')).toBe('Steam')
    expect(platformName('psn')).toBe('PlayStation')
    expect(platformName('gog')).toBe('Gog')
    expect(platformName('')).toBe('Unknown')
  })
})

describe('ownershipSummary', () => {
  it('says a game never started is owned, never played', () => {
    expect(ownershipSummary(own({ playtimeMinutes: 0 }))).toBe('Steam: owned, never played')
  })
  it('says hours and the last played date for a played game', () => {
    const t = ownershipSummary(own({ playtimeMinutes: 4, lastPlayedAt: '2026-11-04T12:00:00Z' }))
    expect(t).toMatch(/^Steam: 4 min, last played .*2026/)
  })
  it('does not invent hours the platform did not give', () => {
    expect(ownershipSummary(own({ playtimeMinutes: null }))).toBe('Steam: owned')
  })
  it('says a wishlist entry is a wish, not an ownership', () => {
    expect(ownershipSummary(own({ isWishlist: true, playtimeMinutes: null }))).toBe('On your Steam wishlist')
  })
  it('says when access comes through a subscription', () => {
    expect(ownershipSummary(own({ source: 'xbox', playtimeMinutes: 90, ownedViaSubscription: true }))).toBe('Xbox: 1 h 30 min, through a subscription')
  })
})
