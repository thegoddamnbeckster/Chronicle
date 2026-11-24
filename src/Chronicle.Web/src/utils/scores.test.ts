import { describe, expect, it } from 'vitest'
import { formatNumber, formatScore, formatVotes, scoreLabel, starFills } from './scores'
import type { ItemScore } from '@/types'

const base: ItemScore = {
  key: 'imdb', displayName: 'IMDb', value: 8.2, scale: 10, display: 'outOf', normalized: 82, source: 'IMDb',
  pluginId: 'chronicle.plugin.imdb', alsoFrom: [],
}

describe('formatScore', () => {
  it('writes outOf in the source scale, never a bare number', () => {
    expect(formatScore(base)).toEqual({ value: '8.2', suffix: '/10' })
    expect(formatScore({ ...base, value: 4.2, scale: 5, normalized: 84 })).toEqual({ value: '4.2', suffix: '/5' })
    expect(formatScore({ ...base, value: 83, scale: 100, normalized: 83 })).toEqual({ value: '83', suffix: '/100' })
  })
  it('drops a trailing .0 and keeps one decimal at most', () => {
    expect(formatNumber(8)).toBe('8')
    expect(formatNumber(8.04)).toBe('8')
    expect(formatNumber(7.984)).toBe('8')
    expect(formatNumber(6.45)).toBe('6.5')
  })
  it('writes a percent as a whole number', () => {
    expect(formatScore({ ...base, display: 'percent', value: 79.84, scale: 100, normalized: 79.84 })).toEqual({ value: '80', suffix: '%' })
  })
})

describe('formatVotes', () => {
  it('abbreviates large counts', () => {
    expect(formatVotes(787753)).toBe('788K')
    expect(formatVotes(1_234_567)).toBe('1.2M')
    expect(formatVotes(1500)).toBe('1.5K')
    expect(formatVotes(950)).toBe('950')
  })
})

describe('starFills', () => {
  it('fills a row of five in proportion', () => {
    expect(starFills(4.2, 5).map(f => Math.round(f * 100) / 100)).toEqual([1, 1, 1, 1, 0.2])
    expect(starFills(5, 5)).toEqual([1, 1, 1, 1, 1])
    expect(starFills(0.4, 5)[0]).toBeCloseTo(0.4)
  })
  it('works for a different scale', () => {
    expect(starFills(8.9, 10).map(f => Math.round(f * 100) / 100)).toEqual([1, 1, 1, 1, 1, 1, 1, 1, 0.9, 0])
  })
})

describe('scoreLabel', () => {
  it('reads as a sentence', () => {
    expect(scoreLabel({ ...base, votes: 787753 })).toBe('IMDb score 8.2 out of 10, 787,753 votes')
    expect(scoreLabel({ ...base, display: 'percent', normalized: 80, displayName: 'TMDB' })).toBe('TMDB score 80 percent')
    expect(scoreLabel({ ...base, displayName: 'Rotten Tomatoes', variant: 'audience', value: 88, scale: 100 })).toBe('Rotten Tomatoes audience score 88 out of 100')
  })
})
