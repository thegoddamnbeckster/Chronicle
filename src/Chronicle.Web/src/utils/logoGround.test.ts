import { describe, it, expect } from 'vitest'
import { pickGround } from './logoGround'

/** n identical RGBA pixels. */
const px = (n: number, r: number, g: number, b: number, a = 255) => Array.from({ length: n }, () => [r, g, b, a]).flat()

describe('pickGround', () => {
  it('puts a black logo on a light ground', () => expect(pickGround(px(50, 0, 0, 0))).toBe('light'))
  it('puts a white logo on a dark ground', () => expect(pickGround(px(50, 255, 255, 255))).toBe('dark'))
  it('puts a dark colour on a light ground and a pale colour on a dark one', () => {
    expect(pickGround(px(50, 20, 40, 120))).toBe('light')    // navy
    expect(pickGround(px(50, 250, 230, 120))).toBe('dark')   // pale yellow
  })
  it('ignores the transparent ground, which is not the logo', () => {
    // a few white pixels among a lot of transparent black ones: it is a white logo
    expect(pickGround([...px(10, 255, 255, 255), ...px(500, 0, 0, 0, 0)])).toBe('dark')
  })
  it('gives a mid grey whichever ground serves it better, and always one of the two', () => {
    expect(['light', 'dark']).toContain(pickGround(px(50, 128, 128, 128)))
  })
  it('says nothing for an image with nothing in it', () => {
    expect(pickGround(px(50, 0, 0, 0, 0))).toBeNull()
    expect(pickGround([])).toBeNull()
  })
  it('chooses by contrast, so a logo is never left on a ground it cannot be read on', () => {
    for (let v = 0; v <= 255; v += 15) {
      const g = pickGround(px(20, v, v, v))
      // the dark ground is chosen exactly for the bright logos, the light one for the dark ones
      expect(g).toBe(v > 120 ? 'dark' : 'light')
    }
  })
})
