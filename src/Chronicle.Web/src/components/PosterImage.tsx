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
}

/**
 * Renders a poster image with a letter-initial placeholder that stays visible
 * until the image finishes loading. Prevents blank gaps when images load slowly
 * (e.g. from fanart.tv) and handles load errors gracefully.
 */
export function PosterImage({ posterUrl, name, className, onClick, imgClassName, placeholderContent, lazy = false }: PosterImageProps) {
  const [loaded, setLoaded] = useState(false)

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
    </div>
  )
}
