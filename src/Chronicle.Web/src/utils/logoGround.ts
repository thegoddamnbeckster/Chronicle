/**
 * A logo is a drawing on a transparent ground, so what shows behind it decides whether it can be read: a black logo vanishes on a dark tile
 * and a white one on a light tile. This measures how bright the logo itself is and picks whichever ground gives it more contrast.
 */

export type LogoGround = 'light' | 'dark'

/** The two grounds the tile can use (the same colours as PersonCard.module.css), as relative luminance 0..1. */
const LIGHT_GROUND = 0.88
const DARK_GROUND = 0.015

/** sRGB channel 0..255 to linear light. */
function linear(c: number): number {
  const s = c / 255
  return s <= 0.04045 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4)
}

/** WCAG contrast ratio between two relative luminances. */
function contrast(a: number, b: number): number {
  const [hi, lo] = a >= b ? [a, b] : [b, a]
  return (hi + 0.05) / (lo + 0.05)
}

/**
 * The better ground for RGBA pixel data (as from a canvas). Only pixels that are mostly opaque count: the transparent ground is not the logo.
 * Returns null for an image with nothing in it to measure. The average is taken in linear light, which is how the eye mixes a logo's strokes
 * and its gaps; a logo that is half black and half white scores in the middle and gets whichever ground serves its stronger half.
 */
export function pickGround(rgba: ArrayLike<number>): LogoGround | null {
  let sum = 0
  let n = 0
  for (let i = 0; i + 3 < rgba.length; i += 4) {
    if (rgba[i + 3] < 128) continue
    sum += 0.2126 * linear(rgba[i]) + 0.7152 * linear(rgba[i + 1]) + 0.0722 * linear(rgba[i + 2])
    n++
  }
  if (n === 0) return null
  const lum = sum / n
  return contrast(lum, LIGHT_GROUND) >= contrast(lum, DARK_GROUND) ? 'light' : 'dark'
}

/**
 * Loads the picture once more (as a cross-origin request, which is what allows its pixels to be read), draws it small and picks a ground.
 * Null when it cannot be measured (the server does not allow cross-origin reads, the load fails): the caller keeps its default.
 */
export function measureGround(url: string): Promise<LogoGround | null> {
  return new Promise(resolve => {
    try {
      const img = new Image()
      img.crossOrigin = 'anonymous'
      img.onload = () => {
        try {
          const size = 48
          const canvas = document.createElement('canvas')
          canvas.width = size
          canvas.height = size
          const ctx = canvas.getContext('2d', { willReadFrequently: true })
          if (!ctx) return resolve(null)
          // fit the whole logo inside the square, keeping its shape
          const scale = Math.min(size / img.naturalWidth, size / img.naturalHeight)
          const w = Math.max(1, Math.round(img.naturalWidth * scale))
          const h = Math.max(1, Math.round(img.naturalHeight * scale))
          ctx.drawImage(img, Math.floor((size - w) / 2), Math.floor((size - h) / 2), w, h)
          resolve(pickGround(ctx.getImageData(0, 0, size, size).data))
        } catch {
          resolve(null)   // a tainted canvas: the picture cannot be read
        }
      }
      img.onerror = () => resolve(null)
      img.src = url
    } catch {
      resolve(null)
    }
  })
}
