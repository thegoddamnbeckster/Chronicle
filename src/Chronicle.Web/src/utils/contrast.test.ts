import { describe, it, expect } from 'vitest'
import { parseColor, contrastRatio, readableOn, ensureContrast, derivedTextColors, MIN_TEXT_CONTRAST } from './contrast'

describe('parseColor', () => {
  it('reads hex and rgb', () => {
    expect(parseColor('#fff')).toEqual([255, 255, 255])
    expect(parseColor('#00ff88')).toEqual([0, 255, 136])
    expect(parseColor('#00ff88cc')).toEqual([0, 255, 136])
    expect(parseColor('rgb(1, 2, 3)')).toEqual([1, 2, 3])
  })
  it('says nothing about what it cannot read', () => {
    expect(parseColor('var(--x)')).toBeNull()
    expect(parseColor('red')).toBeNull()
    expect(parseColor(undefined)).toBeNull()
  })
})

describe('contrastRatio', () => {
  it('is 21 for black on white and 1 for a colour on itself', () => {
    expect(contrastRatio([0, 0, 0], [255, 255, 255])).toBeCloseTo(21, 0)
    expect(contrastRatio([10, 20, 30], [10, 20, 30])).toBeCloseTo(1, 5)
  })
})

describe('readableOn', () => {
  it('keeps the theme\'s own text colour when it already reads', () => {
    expect(readableOn(['#6200ea'], '#fff')).toBe('#fff')
    expect(readableOn(['#00ff88'], '#0a1f1f')).toBe('#0a1f1f')
  })

  it('replaces white on a bright green with dark text (the unreadable Install button)', () => {
    const colour = readableOn(['#00ff88'], '#fff')
    expect(colour).toBe('#0d1117')
    expect(contrastRatio(parseColor(colour)!, parseColor('#00ff88')!)).toBeGreaterThan(MIN_TEXT_CONTRAST)
  })

  it('replaces dark text on a dark accent with white', () => {
    expect(readableOn(['#4a00b3'], '#0d1117')).toBe('#ffffff')
  })

  it('keeps the theme\'s colour when it reads on the resting colour and the hover shade', () => {
    expect(readableOn(['#bb86fc', '#a070dc'], '#0d1117')).toBe('#0d1117')
  })

  it('judges the resting colour first when no choice suits both', () => {
    // A mid-tone pink: dark text reads well on it at rest, and that is the choice made.
    const colour = readableOn(['#ff4081', '#c2185b'], '#ffffff')
    expect(contrastRatio(parseColor(colour)!, parseColor('#ff4081')!)).toBeGreaterThan(MIN_TEXT_CONTRAST)
  })

  it('has no opinion when no surface can be read', () => {
    expect(readableOn(['var(--accent)'], '#fff')).toBeNull()
    expect(readableOn([undefined])).toBeNull()
  })

  it('works without a preferred colour', () => {
    expect(readableOn(['#000000'])).toBe('#ffffff')
    expect(readableOn(['#ffffff'])).toBe('#0d1117')
  })
})

describe('ensureContrast', () => {
  it('leaves a colour that already reads alone', () => {
    expect(ensureContrast('#cccccc', ['#0a1f1f', '#102a2a'])).toBe('#cccccc')
  })

  it('lifts dim text on dark surfaces until it reads', () => {
    const lifted = ensureContrast('#5a7a76', ['#0a1f1f', '#14302f'])!
    expect(lifted).not.toBe('#5a7a76')
    for (const bg of ['#0a1f1f', '#14302f'])
      expect(contrastRatio(parseColor(lifted)!, parseColor(bg)!)).toBeGreaterThanOrEqual(MIN_TEXT_CONTRAST)
  })

  it('darkens pale text on light surfaces', () => {
    const lowered = ensureContrast('#9e9e9e', ['#f5f5f5', '#ffffff', '#e8e8e8'])!
    expect(contrastRatio(parseColor(lowered)!, parseColor('#e8e8e8')!)).toBeGreaterThanOrEqual(MIN_TEXT_CONTRAST)
  })

  it('has no opinion when it cannot read the colours', () => {
    expect(ensureContrast('var(--x)', ['#000'])).toBeNull()
    expect(ensureContrast('#fff', ['var(--bg)'])).toBeNull()
  })
})

describe('derivedTextColors', () => {
  it('derives readable text for the accent, the state colours and the status colours', () => {
    const out = derivedTextColors({
      '--accent': '#00ff88', '--accent-hover': '#00cc6d', '--accent-fg': '#ffffff',
      '--success': '#2e7d32', '--danger': '#ff5252', '--warning': '#ffb74d',
      '--status-danger-fg': '#ef9a9a',
    })
    expect(out['--accent-fg']).toBe('#0d1117')
    expect(out['--success-fg']).toBe('#ffffff')
    expect(out['--danger-fg']).toBe('#0d1117')
    expect(out['--warning-fg']).toBe('#0d1117')
    expect(out['--on-status-danger']).toBe('#0d1117')
    expect(out['--on-status-success']).toBeUndefined()     // that surface was not supplied
  })

  it('lifts a muted text colour that does not read on the theme\'s backgrounds', () => {
    const out = derivedTextColors({
      '--bg-primary': '#0a1f1f', '--bg-secondary': '#102a2a', '--bg-card': '#14302f',
      '--text-muted': '#4a6a66', '--text-secondary': '#c8d8d6',
    })
    expect(out['--text-muted']).toBeDefined()
    expect(out['--text-secondary']).toBeUndefined()        // already reads
  })
})
