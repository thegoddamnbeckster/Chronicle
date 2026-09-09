import { useState } from 'react'
import { PosterProgressBar } from './PosterProgressBar'
import styles from './FanartImage.module.css'

interface FanartImageProps {
  src: string
  alt?: string
  /** CSS class applied to the outer wrapper div — use for sizing / flex / margins. */
  wrapperClassName?: string
  /** CSS class applied to the <img> element — use for object-fit / border-radius. */
  imgClassName?: string
  /** Minimum height (px) of the skeleton placeholder. */
  minHeight?: number
  /**
   * Watch/read/listen position, 0-100. Renders a thin highlight-colored fill bar across the
   * bottom edge, same as PosterImage's own progressPercent -- needed here too since this
   * component stands in for PosterImage whenever a poster happens to be fanart.tv-hosted (see
   * CollectionMetadataBox's isFanartUrl branch). Omit, or pass null/0, to show no bar.
   */
  progressPercent?: number | null
}

/**
 * Replaces a bare <img> for fanart.tv-sourced images.
 * Shows a shimmer skeleton labelled "fanart.tv" while the image loads so the
 * user knows Chronicle is waiting on an external CDN, not that it's broken.
 */
export function FanartImage({ src, alt = '', wrapperClassName, imgClassName, minHeight = 80, progressPercent }: FanartImageProps) {
  const [state, setState] = useState<'loading' | 'loaded' | 'error'>('loading')

  if (state === 'error') return null

  return (
    <div className={`${styles.wrap} ${wrapperClassName ?? ''}`}>
      {state === 'loading' && (
        <div className={styles.skeleton} style={{ minHeight }}>
          <span className={styles.label}>fetching from fanart.tv</span>
          <span className={styles.dot} />
          <span className={styles.dot} />
          <span className={styles.dot} />
        </div>
      )}
      <img
        src={src}
        alt={alt}
        className={`${state === 'loaded' ? '' : styles.hidden} ${imgClassName ?? ''}`}
        onLoad={() => setState('loaded')}
        onError={() => setState('error')}
      />
      <PosterProgressBar percent={progressPercent} />
    </div>
  )
}
