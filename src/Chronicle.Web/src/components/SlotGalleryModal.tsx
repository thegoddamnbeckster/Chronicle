import { useEffect, useState } from 'react'
import type { CanonicalSlot, SlottedImageEntry } from '@/utils/imageExtractor'
import type { MediaItem } from '@/types'
import { ImageSlotControls } from './ImageSlotControls'
import styles from './PluginMetadataBox.module.css'

export interface SlotGalleryModalProps {
  slotLabel: string
  images: SlottedImageEntry[]
  startIndex: number
  onClose: () => void
  overrides: MediaItem['overrides']
  onSet: (slot: CanonicalSlot, img: SlottedImageEntry) => void
  onClear: (slot: CanonicalSlot) => void
  pendingSlot?: CanonicalSlot | null
}

/**
 * Self-contained, type-scoped image browser — deliberately NOT an extension of the
 * page-level combined lightbox in MediaDetailPage.tsx (that one spans every image type
 * across every plugin, by design, for its existing "browse everything" use). This modal's
 * own index/keyboard-nav state is scoped to exactly the one slot's image list, so browsing
 * posters never crosses into backdrops/logos/etc.
 *
 * The slot the gallery was opened from only scopes what you BROWSE here. Assignment is not
 * restricted to it — ImageSlotControls offers every slot for the image on screen.
 */
export function SlotGalleryModal({
  slotLabel, images, startIndex, onClose, overrides, onSet, onClear, pendingSlot,
}: SlotGalleryModalProps) {
  const [idx, setIdx] = useState(startIndex)

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      if (e.key === 'ArrowRight') setIdx(i => (i < images.length - 1 ? i + 1 : i))
      if (e.key === 'ArrowLeft') setIdx(i => (i > 0 ? i - 1 : i))
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [images.length, onClose])

  const current = images[idx]
  if (!current) return null

  return (
    <div
      className={styles.lightboxOverlay}
      onClick={onClose}
      role="dialog"
      aria-modal="true"
      aria-label={`${slotLabel} gallery`}
    >
      <button className={styles.lightboxClose} onClick={onClose} type="button" aria-label="Close">
        ✕
      </button>
      {idx > 0 && (
        <button
          className={`${styles.lightboxNav} ${styles.lightboxNavPrev}`}
          onClick={e => { e.stopPropagation(); setIdx(idx - 1) }}
          type="button"
          aria-label="Previous image"
        >
          ‹
        </button>
      )}
      <img
        className={styles.lightboxImg}
        src={current.url}
        alt={`${slotLabel} — ${current.pluginId.replace('chronicle.plugin.', '')}`}
        onClick={e => e.stopPropagation()}
      />
      <div className={styles.lightboxCaption} onClick={e => e.stopPropagation()}>
        <span>
          {slotLabel} from {current.pluginId.replace('chronicle.plugin.', '')}
          {images.length > 1 && (
            <span className={styles.lightboxCounter}> {idx + 1} / {images.length}</span>
          )}
        </span>
      </div>
      <ImageSlotControls
        imageUrl={current.url}
        overrides={overrides}
        onSet={slot => onSet(slot, current)}
        onClear={onClear}
        pendingSlot={pendingSlot}
      />
      {idx < images.length - 1 && (
        <button
          className={`${styles.lightboxNav} ${styles.lightboxNavNext}`}
          onClick={e => { e.stopPropagation(); setIdx(idx + 1) }}
          type="button"
          aria-label="Next image"
        >
          ›
        </button>
      )}
    </div>
  )
}
