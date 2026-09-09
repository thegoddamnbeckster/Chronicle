import styles from './PosterProgressBar.module.css'

interface PosterProgressBarProps {
  /**
   * Watch/read/listen position, 0-100. Renders a thin highlight-colored fill bar across the
   * bottom edge of whatever positioned container it's placed in. Omit, or pass null/0, to
   * render nothing. Callers don't need to distinguish "not started" from "finished" -- a
   * completed item's resume position is cleared server-side, so pass 100 explicitly for that
   * case (see utils/posterProgress.ts's posterProgressPercent).
   */
  percent?: number | null
}

/**
 * Shared by PosterImage and FanartImage -- both stand in for "the poster" depending on
 * whether the image happens to be fanart.tv-hosted, and both need the identical progress bar.
 * Extracted after the two nearly drifted out of sync with each other (confirmed live,
 * 2026-09-09): fixing the bar's color/behavior in one and not the other is an easy miss
 * otherwise, since nothing makes the two implementations look related at a glance.
 */
export function PosterProgressBar({ percent }: PosterProgressBarProps) {
  const clamped = percent != null ? Math.max(0, Math.min(100, percent)) : null
  if (clamped == null || clamped <= 0) return null

  return (
    <div className={styles.track} title={`${Math.round(clamped)}% watched`}>
      <div className={styles.fill} style={{ width: `${clamped}%` }} />
    </div>
  )
}
