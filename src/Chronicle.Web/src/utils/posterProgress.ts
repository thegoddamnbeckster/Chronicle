import type { LibraryStatus } from '@/types'

/**
 * Progress value for PosterImage's bar. A Completed item's resumePositionPercent is cleared
 * server-side (there's no resume point for a finished item), which used to mean its poster
 * showed no bar at all -- indistinguishable from never having been started. Per-user report
 * (2026-09-09): a completed item's bar should read 100%, not disappear.
 */
export function posterProgressPercent(
  status: LibraryStatus | null | undefined,
  resumePositionPercent: number | null | undefined,
): number | null {
  if (status === 'Completed') return 100
  return resumePositionPercent ?? null
}
