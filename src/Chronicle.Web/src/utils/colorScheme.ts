/**
 * Whether a CSS colour reads as dark. Handles #rgb, #rrggbb, and rgb()/rgba(); anything else (a gradient, a var() reference,
 * a named colour) returns null so the caller keeps the default.
 */
export function isDarkColor(value: string | undefined): boolean | null {
  if (!value) return null
  const v = value.trim().toLowerCase()
  let r: number, g: number, b: number

  const hex = /^#([0-9a-f]{3}|[0-9a-f]{6})$/.exec(v)
  if (hex) {
    const h = hex[1].length === 3 ? hex[1].split('').map(c => c + c).join('') : hex[1]
    r = parseInt(h.slice(0, 2), 16)
    g = parseInt(h.slice(2, 4), 16)
    b = parseInt(h.slice(4, 6), 16)
  } else {
    const rgb = /^rgba?\(\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*[, ]\s*(\d{1,3})/.exec(v)
    if (!rgb) return null
    r = Number(rgb[1]); g = Number(rgb[2]); b = Number(rgb[3])
  }
  // Relative luminance (sRGB weights); below the midpoint reads as dark.
  return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255 < 0.5
}

/**
 * The CSS color-scheme a theme's variables call for. A native control's popup (the open list of a <select>, scrollbars, date
 * pickers) is drawn by the browser from this, not from the page's own colours: on a dark theme without it the list is white
 * and the theme's light option text cannot be read.
 */
export function colorSchemeFor(vars: Record<string, string>): 'dark' | 'light' {
  return isDarkColor(vars['--bg-primary'] ?? vars['--bg-secondary']) ? 'dark' : 'light'
}
