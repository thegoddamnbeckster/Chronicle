import React, { useState } from 'react'
import styles from './PosterImage.module.css'

interface PosterImageProps {
  /** URL of the poster image. When null/undefined shows only the placeholder. */
  posterUrl?: string | null
  /** Used for alt text and the fallback initial letter. */
  name: string
  className?: string
  /** Optional click handler forwarded to the img element. */
  onClick?: () => void
  /** Extra CSS class applied to the img element. */
  imgClassName?: string
  /** Custom content for the placeholder. Defaults to the first letter of name. */
  placeholderContent?: React.ReactNode
  /**
   * Set to true only for off-screen grid items where lazy loading is safe.
   * Default false — eager loading ensures onLoad fires and the placeholder hides.
   */
  lazy?: boolean
  /**
   * In-progress watch/read/listen position, 0-100. Renders a thin green fill bar
   * across the bottom edge of the poster. Omit, or pass null/0, to show no bar —
   * a completed item's resume position is cleared server-side (not sent as 0), so
   * callers don't need to distinguish "not started" from "finished" themselves.
   */
  progressPercent?: number | null
}

/**
 * Renders a poster image with a letter-initial placeholder that stays visible
 * until the image finishes loading. Prevents blank gaps when images load slowly
 * (e.g. from fanart.tv) and handles load errors gracefully.
 */
export function PosterImage({ posterUrl, name, className, onClick, imgClassName, placeholderContent, lazy = false, progressPercent }: PosterImageProps) {
  const [loaded, setLoaded] = useState(false)
  const clampedProgress = progressPercent != null ? Math.max(0, Math.min(100, progressPercent)) : null

  return (
    <div className={`${styles.root} ${className ?? ''}`}>
      <div className={styles.placeholder} style={{ display: loaded ? 'none' : 'flex' }}>
        {placeholderContent ?? name.charAt(0)}
      </div>
      {posterUrl && (
        <img
          src={posterUrl}
          alt={name}
          loading={lazy ? 'lazy' : 'eager'}
          className={`${styles.img} ${imgClassName ?? ''}`}
          style={loaded
            ? undefined
            : { visibility: 'hidden', position: 'absolute', inset: 0, width: '100%', height: '100%' }}
          onClick={onClick}
          onLoad={() => setLoaded(true)}
          onError={() => setLoaded(false)}
        />
      )}
      {clampedProgress != null && clampedProgress > 0 && (
        <div className={styles.progressTrack} title={`${Math.round(clampedProgress)}% watched`}>
          <div className={styles.progressFill} style={{ width: `${clampedProgress}%` }} />
        </div>
      )}
    </div>
  )
}
