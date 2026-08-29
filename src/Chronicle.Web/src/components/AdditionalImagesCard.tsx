import { useMemo } from 'react'
import {
  extractSlottedImages, SLOT_INFO, SLOT_ORDER,
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
  const bySlot = useMemo(() => {
    const map = new Map<CanonicalSlot, SlottedImageEntry[]>()
    for (const [pluginId, meta] of Object.entries(item.pluginMetadata ?? {})) {
      for (const img of extractSlottedImages(pluginId, meta as Record<string, unknown>)) {
        const list = map.get(img.slot) ?? []
        list.push(img)
        map.set(img.slot, list)
      }
    }
    // Dedupe identical URLs across plugins (two providers can return the same CDN URL).
    // The image currently filling a slot is deliberately kept in the list — it's how you
    // navigate back to it in the viewer to unpin it.
    for (const slot of SLOT_ORDER) {
      const list = map.get(slot)
      if (!list) continue
      const seen = new Set<string>()
      const deduped = list.filter(img => {
        if (seen.has(img.url)) return false
        seen.add(img.url)
        return true
      })
      if (deduped.length > 0) map.set(slot, deduped)
      else map.delete(slot)
    }
    return map
  }, [item.pluginMetadata])

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
                {images.map((img, i) => {
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
