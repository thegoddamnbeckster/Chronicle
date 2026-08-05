import { useMemo } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { setMediaOverride, clearMediaOverride } from '@/api/media'
import { extractSlottedImages, type CanonicalSlot, type SlottedImageEntry } from '@/utils/imageExtractor'
import type { MediaItem } from '@/types'
import styles from './AdditionalImagesCard.module.css'
import boxStyles from './PluginMetadataBox.module.css'

const SLOT_ORDER: CanonicalSlot[] = [
  'poster_url', 'backdrop_url', 'logo_url', 'banner_url',
  'thumb_url', 'clearart_url', 'disc_url', 'character_art_url',
]

const SLOT_INFO: Record<CanonicalSlot, { label: string; resolvedKey: string }> = {
  poster_url:        { label: 'Posters',      resolvedKey: 'posterUrl' },
  backdrop_url:       { label: 'Backdrops',     resolvedKey: 'backdropUrl' },
  logo_url:           { label: 'Logos',         resolvedKey: 'logoUrl' },
  banner_url:         { label: 'Banners',       resolvedKey: 'bannerUrl' },
  thumb_url:          { label: 'Thumbs',        resolvedKey: 'thumbUrl' },
  clearart_url:       { label: 'Clear Art',     resolvedKey: 'clearartUrl' },
  disc_url:           { label: 'Disc Art',      resolvedKey: 'discUrl' },
  character_art_url:  { label: 'Character Art', resolvedKey: 'characterArtUrl' },
}

export interface AdditionalImagesCardProps {
  mediaId: number
  item: MediaItem
  onOpenGallery: (slot: CanonicalSlot, slotLabel: string, images: SlottedImageEntry[], startIndex: number) => void
}

export function AdditionalImagesCard({ mediaId, item, onOpenGallery }: AdditionalImagesCardProps) {
  const qc = useQueryClient()

  const promoteMut = useMutation({
    mutationFn: (p: { field: CanonicalSlot; img: SlottedImageEntry }) =>
      setMediaOverride(mediaId, p.field, p.img.url, p.img.pluginId, p.img.slot),
    onSuccess: (updated) => { qc.setQueryData(['media', mediaId], updated) },
  })

  const clearMut = useMutation({
    mutationFn: (field: CanonicalSlot) => clearMediaOverride(mediaId, field),
    onSuccess: (updated) => { qc.setQueryData(['media', mediaId], updated) },
  })

  const bySlot = useMemo(() => {
    const map = new Map<CanonicalSlot, SlottedImageEntry[]>()
    for (const [pluginId, meta] of Object.entries(item.pluginMetadata ?? {})) {
      for (const img of extractSlottedImages(pluginId, meta as Record<string, unknown>)) {
        const list = map.get(img.slot) ?? []
        list.push(img)
        map.set(img.slot, list)
      }
    }
    // Exclude whichever image currently holds each slot, and dedupe identical URLs
    // across plugins (two providers can return the same CDN URL).
    for (const slot of SLOT_ORDER) {
      const currentUrl = (item.resolvedMetadata as Record<string, unknown> | undefined)?.[SLOT_INFO[slot].resolvedKey]
      const list = map.get(slot)
      if (!list) continue
      const seen = new Set<string>()
      const filtered = list.filter(img => {
        if (typeof currentUrl === 'string' && img.url === currentUrl) return false
        if (seen.has(img.url)) return false
        seen.add(img.url)
        return true
      })
      if (filtered.length > 0) map.set(slot, filtered)
      else map.delete(slot)
    }
    return map
  }, [item.pluginMetadata, item.resolvedMetadata])

  if (bySlot.size === 0) return null

  return (
    <div className={boxStyles.box}>
      <div className={boxStyles.header}>
        <div className={boxStyles.brand}>
          <span className={boxStyles.name}>Additional Images</span>
          <p className={boxStyles.timestamp}>
            Every image available across all sources for this item — click one to make it the poster/backdrop/etc.
          </p>
        </div>
      </div>

      <div className={boxStyles.grid}>
        {SLOT_ORDER.filter(slot => bySlot.has(slot)).map(slot => {
          const images = bySlot.get(slot)!
          const info = SLOT_INFO[slot]
          const isPinned = Boolean(item.overrides?.[slot])
          return (
            <div key={slot} className={`${boxStyles.row} ${boxStyles.rowImages}`}>
              <span className={boxStyles.label}>
                {info.label}
                {isPinned && (
                  <button
                    className={styles.resetIcon}
                    type="button"
                    title={`Reset ${info.label} to the default (unpin your manual choice)`}
                    disabled={clearMut.isPending}
                    onClick={() => clearMut.mutate(slot)}
                  >
                    ↺
                  </button>
                )}
              </span>
              <div className={boxStyles.imageLinks}>
                {images.slice(0, 12).map((img, i) => (
                  <div key={`${img.pluginId}-${img.url}`} className={styles.thumbWrap}>
                    <button
                      className={boxStyles.imageLink}
                      type="button"
                      title={`Set as ${info.label.replace(/s$/, '')} (from ${img.pluginId.replace('chronicle.plugin.', '')})`}
                      disabled={promoteMut.isPending}
                      onClick={() => promoteMut.mutate({ field: slot, img })}
                    >
                      <img
                        src={img.url}
                        alt={img.label}
                        className={boxStyles.thumbnail}
                        onError={e => { e.currentTarget.style.display = 'none' }}
                      />
                      <span className={boxStyles.thumbnailLabel}>
                        {img.pluginId.replace('chronicle.plugin.', '')}
                      </span>
                    </button>
                    <button
                      className={styles.expandIcon}
                      type="button"
                      title={`Browse all ${info.label.toLowerCase()}`}
                      onClick={() => onOpenGallery(slot, info.label, images, i)}
                    >
                      ⤢
                    </button>
                  </div>
                ))}
              </div>
            </div>
          )
        })}
      </div>

      {promoteMut.isError && (
        <p className={boxStyles.error}>{`Failed to set image: ${(promoteMut.error as Error).message}`}</p>
      )}
      {clearMut.isError && (
        <p className={boxStyles.error}>{`Failed to reset: ${(clearMut.error as Error).message}`}</p>
      )}
    </div>
  )
}
