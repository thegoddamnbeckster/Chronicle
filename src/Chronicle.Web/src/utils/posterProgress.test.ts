import { describe, it, expect } from 'vitest'
import { posterProgressPercent } from './posterProgress'

describe('posterProgressPercent', () => {
  it('returns 100 for a Completed item regardless of resumePositionPercent', () => {
    // Completed items have their resume position cleared server-side (there's no resume
    // point for a finished item) -- per-user report (2026-09-09), the poster bar should
    // still read full, not disappear as if the item were never started.
    expect(posterProgressPercent('Completed', null)).toBe(100)
    expect(posterProgressPercent('Completed', undefined)).toBe(100)
  })

  it('returns the raw resume position for a non-Completed status', () => {
    expect(posterProgressPercent('Watching', 43.79)).toBe(43.79)
  })

  it('returns null when there is no resume position and the item is not Completed', () => {
    expect(posterProgressPercent('PlanToWatch', null)).toBeNull()
    expect(posterProgressPercent(undefined, undefined)).toBeNull()
  })
})
