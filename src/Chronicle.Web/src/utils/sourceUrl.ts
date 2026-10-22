/** One id-pattern to web-address rule from a plugin manifest (sourceUrls). */
export interface SourceUrlRule {
  match: string
  url: string
}

/**
 * The web page a plugin's stored external id resolved to: the first rule whose regex matches the id wins, and its URL template has
 * $0 (the whole id) and $1..$n (capture groups) filled in. Only http(s) addresses are returned, and null when nothing matches or a
 * rule's regex is invalid, so a bad manifest never breaks the card.
 */
export function sourceUrlFor(externalId: string | null | undefined, rules: SourceUrlRule[] | null | undefined): string | null {
  if (!externalId || !rules?.length) return null
  for (const rule of rules) {
    let m: RegExpMatchArray | null
    try { m = externalId.match(new RegExp(rule.match)) } catch { continue }
    if (!m) continue
    const url = rule.url.replace(/\$(\d+)/g, (_, n: string) => m![Number(n)] ?? '')
    return /^https?:\/\//i.test(url) ? url : null
  }
  return null
}
