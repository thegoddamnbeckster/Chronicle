import type { MediaOwnership } from '@/types'

/** "4 min", "1 h 10 min", "56.4 hours": a game's playtime in the words people say it in. */
export function formatPlaytime(minutes: number): string {
  if (minutes < 1) return 'not played'
  if (minutes < 60) return `${Math.round(minutes)} min`
  if (minutes < 600) {
    const h = Math.floor(minutes / 60)
    const m = Math.round(minutes - h * 60)
    return m === 0 ? `${h} h` : `${h} h ${m} min`
  }
  return `${(minutes / 60).toFixed(1)} hours`
}

/** The platform key ("steam", "psn") as a name to show. A key nobody has named is shown capitalised, never blank. */
export function platformName(source: string): string {
  const known: Record<string, string> = { steam: 'Steam', psn: 'PlayStation', xbox: 'Xbox', play: 'Google Play' }
  return known[source.toLowerCase()] ?? (source ? source[0].toUpperCase() + source.slice(1) : 'Unknown')
}

/** What the page says about one platform's hold on a title: "Steam: 56.4 hours, last played 4 Nov 2026". */
export function ownershipSummary(o: MediaOwnership): string {
  const name = platformName(o.source)
  if (o.isWishlist) return `On your ${name} wishlist`
  const parts: string[] = []
  if (o.playtimeMinutes == null) parts.push('owned')
  else if (o.playtimeMinutes < 1) parts.push('owned, never played')
  else parts.push(formatPlaytime(o.playtimeMinutes))
  if (o.lastPlayedAt) parts.push(`last played ${new Date(o.lastPlayedAt).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })}`)
  if (o.ownedViaSubscription) parts.push('through a subscription')
  return `${name}: ${parts.join(', ')}`
}
