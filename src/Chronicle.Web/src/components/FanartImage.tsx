import { useState } from 'react'
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
}

/**
 * Replaces a bare <img> for fanart.tv-sourced images.
 * Shows a shimmer skeleton labelled "fanart.tv" while the image loads so the
 * user knows Chronicle is waiting on an external CDN, not that it's broken.
 */
export function FanartImage({ src, alt = '', wrapperClassName, imgClassName, minHeight = 80 }: FanartImageProps) {
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
    </div>
  )
}
