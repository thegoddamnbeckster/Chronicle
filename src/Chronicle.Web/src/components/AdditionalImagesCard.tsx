import { useMemo } from 'react'
import {
  buildSlotImages, SLOT_INFO, SLOT_ORDER,
  type CanonicalSlot, type SlottedImageEntry,
} from '@/utils/imageExtractor'
import type { MediaItem } from '@/types'
import styles from './AdditionalImagesCard.module.css'
import boxStyles from './PluginMetadataBox.module.css'

export interface AdditionalImagesCardProps {
  item: MediaItem
  onOpenGallery: (slot: CanonicalSlot, slotLabel: string, images: SlottedImageEntry[], startIndex: number) => void
}

/**
 * Browse-only surface: every promote-eligible image for this item, grouped by the slot its
 * source plugin called it. Clicking a thumbnail opens the full-size gallery — it does NOT
 * assign anything. All promote/demote controls live inside the full-size viewer, so an image
 * is only ever assigned while it's actually visible at full size.
 */
export function AdditionalImagesCard({ item, onOpenGallery }: AdditionalImagesCardProps) {
  // buildSlotImages already dedupes across plugins and adds a synthetic entry for a manually
  // pinned override with no plugin blob of its own — without that, a slot filled only via
  // ManualImageUrlModal would have no row here at all, and no way back into the viewer to
  // change or clear it.
  const bySlot = useMemo(() => {
    const map = new Map<CanonicalSlot, SlottedImageEntry[]>()
    for (const slot of SLOT_ORDER) {
      const images = buildSlotImages(item, slot)
      if (images.length > 0) map.set(slot, images)
    }
    return map
  }, [item])

  if (bySlot.size === 0) return null

  return (
    <div className={boxStyles.box}>
      <div className={boxStyles.header}>
        <div className={boxStyles.brand}>
          <span className={boxStyles.name}>Additional Images</span>
          <p className={boxStyles.timestamp}>
            Every image available across all sources for this item — click one to open it full
            size, where you can set it as the poster, backdrop, thumb, disc art, and so on.
          </p>
        </div>
      </div>

      <div className={boxStyles.grid}>
        {SLOT_ORDER.filter(slot => bySlot.has(slot)).map(slot => {
          const images = bySlot.get(slot)!
          const info = SLOT_INFO[slot]
          const pinnedUrl = item.overrides?.[slot]?.url
          return (
            <div key={slot} className={`${boxStyles.row} ${boxStyles.rowImages}`}>
              <span className={boxStyles.label}>{info.label}</span>
              <div className={boxStyles.imageLinks}>
                {images.slice(0, 12).map((img, i) => {
                  const isPinned = pinnedUrl === img.url
                  return (
                    <div key={`${img.pluginId}-${img.url}`} className={styles.thumbWrap}>
                      <button
                        className={boxStyles.imageLink}
                        type="button"
                        title={
                          isPinned
                            ? `Currently pinned as the ${info.singular} — open full size to change or reset it`
                            : `Open full size (from ${img.pluginId.replace('chronicle.plugin.', '')})`
                        }
                        onClick={() => onOpenGallery(slot, info.label, images, i)}
                      >
                        <img
                          src={img.url}
                          alt={img.label}
                          className={boxStyles.thumbnail}
                          onError={e => { e.currentTarget.style.display = 'none' }}
                        />
                        <span className={boxStyles.thumbnailLabel}>
                          {isPinned ? '✓ ' : ''}{img.pluginId.replace('chronicle.plugin.', '')}
                        </span>
                      </button>
                    </div>
                  )
                })}
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}
