import type { ItemScore } from '@/types'

/** At most one decimal, a trailing ".0" dropped: 8 -> "8", 8.24 -> "8.2", 4.25 -> "4.3". */
export function formatNumber(n: number): string {
  const rounded = Math.round(n * 10) / 10
  return Number.isInteger(rounded) ? String(rounded) : rounded.toFixed(1)
}

/** The score the way its source's own site writes it: "8.2/10", "4.2/5", "83/100" or "80%". Never a bare number. */
export function formatScore(s: Pick<ItemScore, 'value' | 'scale' | 'display' | 'normalized'>): { value: string; suffix: string } {
  if (s.display === 'percent') return { value: String(Math.round(s.normalized)), suffix: '%' }
  return { value: formatNumber(s.value), suffix: `/${formatNumber(s.scale)}` }
}

/** 788K, 1.2M; small counts are written out. */
export function formatVotes(n: number): string {
  if (n >= 1_000_000) return `${trim(n / 1_000_000)}M`
  if (n >= 10_000) return `${Math.round(n / 1000)}K`
  if (n >= 1_000) return `${trim(n / 1000)}K`
  return String(n)
}

function trim(n: number): string {
  const r = Math.round(n * 10) / 10
  return Number.isInteger(r) ? String(r) : r.toFixed(1)
}

/** One star per point of the scale (five for a /5 source, ten for a /10 one, as TVMaze draws); each filled in proportion: 4.2 of 5 -> [1,1,1,1,.2]. */
export function starFills(value: number, scale: number): number[] {
  const count = Math.max(1, Math.min(10, Math.round(scale)))
  return Array.from({ length: count }, (_, i) => Math.max(0, Math.min(1, value - i)))
}

/** The sentence a screen reader gets: "IMDb score 8.2 out of 10, 787,753 votes". */
export function scoreLabel(s: ItemScore): string {
  const name = s.variant ? `${s.displayName} ${s.variant}` : s.displayName
  const f = formatScore(s)
  const body = s.display === 'percent' ? `${f.value} percent` : `${f.value} out of ${formatNumber(s.scale)}`
  return `${name} score ${body}${s.votes ? `, ${s.votes.toLocaleString('en-US')} votes` : ''}`
}
