/**
 * Readable text on a coloured surface. A theme supplies its accent, success, danger and warning colours, and the text a
 * button puts on one of them used to be a fixed white (or a fixed near-black) that suited some themes and was unreadable
 * on others (white on a bright green button). The theme loader now derives the text colour from the surface colour, so a
 * button is readable on every theme, including ones installed later.
 */

/** [r, g, b] (0-255) of #rgb, #rrggbb, #rrggbbaa or rgb()/rgba(); null for anything else (a var(), a gradient, a name). */
export function parseColor(value: string | null | undefined): [number, number, number] | null {
  if (!value) return null
  const v = value.trim().toLowerCase()
  const hex = /^#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/.exec(v)
  if (hex) {
    let h = hex[1]
    if (h.length === 3) h = h.split('').map(c => c + c).join('')
    return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)]
  }
  const rgb = /^rgba?\(\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*[, ]\s*(\d{1,3})/.exec(v)
  return rgb ? [Number(rgb[1]), Number(rgb[2]), Number(rgb[3])] : null
}

function channel(c: number): number {
  const s = c / 255
  return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4)
}

/** WCAG relative luminance. */
export function luminance([r, g, b]: [number, number, number]): number {
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)
}

/** WCAG contrast ratio, 1 (none) to 21 (black on white). */
export function contrastRatio(a: [number, number, number], b: [number, number, number]): number {
  const la = luminance(a), lb = luminance(b)
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05)
}

/** The WCAG minimum for normal-size text. */
export const MIN_TEXT_CONTRAST = 4.5

const WHITE: [number, number, number] = [255, 255, 255]
const NEAR_BLACK: [number, number, number] = [13, 17, 23]

/**
 * A text colour readable on `surfaces[0]` (a button's resting colour; later entries are its hover shade and the like):
 * `preferred` when the theme's own choice already reads at 4.5:1 on ALL of them, otherwise white or near-black, whichever
 * reads better on the resting colour (the one people look at). Null when the resting colour cannot be read (a var() or
 * gradient), so the caller keeps whatever it had.
 */
export function readableOn(surfaces: Array<string | undefined>, preferred?: string): string | null {
  const resting = parseColor(surfaces[0])
  if (!resting) return null
  const others = surfaces.slice(1).map(parseColor).filter((c): c is [number, number, number] => c !== null)
  const all = [resting, ...others]

  const own = parseColor(preferred)
  if (own && preferred && Math.min(...all.map(s => contrastRatio(own, s))) >= MIN_TEXT_CONTRAST) return preferred
  return contrastRatio(NEAR_BLACK, resting) >= contrastRatio(WHITE, resting) ? '#0d1117' : '#ffffff'
}

/**
 * `colour` nudged toward white (on dark surfaces) or black (on light ones) until it reads at 4.5:1 on every surface it can be
 * read against; the colour itself when it already does, null when it cannot be read. Used for the muted and secondary text
 * colours, which several themes set too dim for the buttons, chips and hints that use them.
 */
export function ensureContrast(colour: string | undefined, surfaces: Array<string | undefined>): string | null {
  const text = parseColor(colour)
  const bgs = surfaces.map(parseColor).filter((c): c is [number, number, number] => c !== null)
  if (!text || bgs.length === 0) return null
  const worst = (c: [number, number, number]) => Math.min(...bgs.map(s => contrastRatio(c, s)))
  if (worst(text) >= MIN_TEXT_CONTRAST) return colour ?? null

  const towards: [number, number, number] =
    bgs.reduce((sum, s) => sum + luminance(s), 0) / bgs.length < 0.4 ? [255, 255, 255] : [0, 0, 0]
  for (let step = 1; step <= 20; step++) {
    const mix = step / 20
    const c: [number, number, number] = [0, 1, 2].map(i => Math.round(text[i] + (towards[i] - text[i]) * mix)) as [number, number, number]
    if (worst(c) >= MIN_TEXT_CONTRAST) return '#' + c.map(v => v.toString(16).padStart(2, '0')).join('')
  }
  return '#' + towards.map(v => v.toString(16).padStart(2, '0')).join('')
}

/**
 * The derived "text on this colour" variables for a theme's variable map: --accent-fg (readable on the accent and its hover
 * shade), --success-fg, --danger-fg, --warning-fg (on those colours), and --on-status-success/-danger/-warning (on the
 * --status-*-fg colours some buttons use as a background), plus --text-muted/--text-secondary lifted until they read on the
 * theme's backgrounds. Only variables whose surface colour could be read are returned.
 */
export function derivedTextColors(vars: Record<string, string>): Record<string, string> {
  const out: Record<string, string> = {}
  const set = (name: string, surfaces: Array<string | undefined>, preferred?: string) => {
    const colour = readableOn(surfaces, preferred)
    if (colour) out[name] = colour
  }
  set('--accent-fg', [vars['--accent'], vars['--accent-hover']], vars['--accent-fg'])
  set('--success-fg', [vars['--success']])
  set('--danger-fg', [vars['--danger']])
  set('--warning-fg', [vars['--warning']])
  set('--on-status-success', [vars['--status-success-fg']])
  set('--on-status-danger', [vars['--status-danger-fg']])
  set('--on-status-warning', [vars['--status-warning-fg']])

  // Muted and secondary text must read on the surfaces the app puts text on.
  const surfaces = [vars['--bg-primary'], vars['--bg-secondary'], vars['--bg-card'], vars['--surface-2']]
  for (const name of ['--text-muted', '--text-secondary']) {
    const colour = ensureContrast(vars[name], surfaces)
    if (colour && colour !== vars[name]) out[name] = colour
  }
  return out
}
