/**
 * Progress value for PosterImage's bar. A Completed item's resumePositionPercent is cleared
 * server-side (there's no resume point for a finished item), which used to mean its poster
 * showed no bar at all -- indistinguishable from never having been started.
 *
 * Per-user correction (2026-09-09): showing a flat 100% for every Completed item was wrong too
 * -- stopping at 96% and crossing the watched threshold should still display as 96%, not be
 * inflated. lastKnownProgressPercent carries the actual last-scrobbled percent and is never
 * cleared on completion (unlike resumePositionPercent), so a Completed item shows that real
 * value. Only falls back to 100% when there's truly no better information (e.g. an item marked
 * Completed by hand, or imported from a watch-history sync that reports no percentage at all).
 *
 * Takes `status` as a plain string (not the LibraryStatus union) so callers backed by a DTO
 * that only guarantees a raw string (e.g. CollectionMember.libraryStatus, serialized from the
 * backend's LibraryStatus.ToString()) don't need an unsafe cast just to call this.
 */
export function posterProgressPercent(
  status: string | null | undefined,
  resumePositionPercent: number | null | undefined,
  lastKnownProgressPercent?: number | null,
): number | null {
  if (status === 'Completed') return lastKnownProgressPercent ?? 100
  return resumePositionPercent ?? null
}
