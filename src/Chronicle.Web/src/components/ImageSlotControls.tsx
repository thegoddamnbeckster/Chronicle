import { SLOT_INFO, SLOT_ORDER, type CanonicalSlot } from '@/utils/imageExtractor'
import type { MediaItem } from '@/types'
import styles from './ImageSlotControls.module.css'

export interface ImageSlotControlsProps {
  /** URL of the image currently shown full size — the one these controls act on. */
  imageUrl: string
  overrides: MediaItem['overrides']
  onSet: (slot: CanonicalSlot) => void
  onClear: (slot: CanonicalSlot) => void
  /** Slot with a request in flight, so only that chip shows a busy state. */
  pendingSlot?: CanonicalSlot | null
}

/**
 * The promote/demote surface for one image, deliberately rendered ONLY inside a full-size
 * image viewer (the type-scoped gallery and the page-level lightbox) — never on the detail
 * page itself.
 *
 * Every slot is offered for every image: an image's "natural" type (what the plugin called
 * it) decides how it's grouped for browsing, not what it's allowed to become — a backdrop
 * can be pinned as a thumb, the same artwork can hold several slots at once, and each slot
 * is independently revertible to the automatically-resolved default.
 */
export function ImageSlotControls({
  imageUrl, overrides, onSet, onClear, pendingSlot,
}: ImageSlotControlsProps) {
  return (
    <div className={styles.wrap} onClick={e => e.stopPropagation()}>
      <span className={styles.prompt}>Use this image as:</span>
      <div className={styles.chips}>
        {SLOT_ORDER.map(slot => {
          const override = overrides?.[slot]
          const pinnedHere = override?.url === imageUrl
          const pinnedElsewhere = Boolean(override) && !pinnedHere
          const busy = pendingSlot === slot
          const { singular } = SLOT_INFO[slot]

          return (
            <button
              key={slot}
              type="button"
              disabled={busy}
              className={[
                styles.chip,
                pinnedHere ? styles.chipActive : '',
                pinnedElsewhere ? styles.chipTaken : '',
              ].filter(Boolean).join(' ')}
              title={
                pinnedHere
                  ? `This image is pinned as the ${singular}. Click to revert to the automatic default.`
                  : pinnedElsewhere
                    ? `${singular} is currently pinned to a different image. Click to use this one instead.`
                    : `Pin this image as the ${singular}`
              }
              onClick={() => (pinnedHere ? onClear(slot) : onSet(slot))}
            >
              {busy ? '…' : pinnedHere ? `✓ ${singular}` : singular}
            </button>
          )
        })}
      </div>
      <span className={styles.hint}>
        Pinned choices survive future metadata refreshes. Click a pinned slot again to return it to the default.
      </span>
    </div>
  )
}
