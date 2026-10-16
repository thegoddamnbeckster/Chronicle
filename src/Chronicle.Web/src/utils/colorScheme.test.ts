import { describe, it, expect } from 'vitest'
import { isDarkColor, colorSchemeFor } from './colorScheme'

describe('isDarkColor', () => {
  it('reads hex and rgb colours', () => {
    expect(isDarkColor('#0a1f1f')).toBe(true)
    expect(isDarkColor('#fff')).toBe(false)
    expect(isDarkColor('#f5f5f5')).toBe(false)
    expect(isDarkColor('rgb(10, 20, 30)')).toBe(true)
    expect(isDarkColor('rgba(240, 240, 240, 0.9)')).toBe(false)
  })

  it('says nothing about a colour it cannot read', () => {
    expect(isDarkColor('var(--x)')).toBeNull()
    expect(isDarkColor('linear-gradient(#000, #fff)')).toBeNull()
    expect(isDarkColor(undefined)).toBeNull()
  })
})

describe('colorSchemeFor', () => {
  it('is dark for a dark page background and light otherwise', () => {
    expect(colorSchemeFor({ '--bg-primary': '#0b1f1f', '--bg-secondary': '#12302f' })).toBe('dark')
    expect(colorSchemeFor({ '--bg-primary': '#f5f5f5' })).toBe('light')
    expect(colorSchemeFor({})).toBe('light')
  })
})
