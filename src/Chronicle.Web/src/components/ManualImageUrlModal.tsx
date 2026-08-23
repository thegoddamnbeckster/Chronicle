import { useState } from 'react'
import type { CanonicalSlot } from '@/utils/imageExtractor'
import type { MediaItem } from '@/types'
import { ImageSlotControls } from './ImageSlotControls'
import overlayStyles from './PluginMetadataBox.module.css'
import styles from './ManualImageUrlModal.module.css'

export interface ManualImageUrlModalProps {
  onClose: () => void
  overrides: MediaItem['overrides']
  onSet: (slot: CanonicalSlot, url: string) => void
  onClear: (slot: CanonicalSlot) => void
  pendingSlot?: CanonicalSlot | null
}

/**
 * The escape hatch for an item a provider found nothing for at all (a rare fan-made release,
 * a concert film, anything obscure) -- SlotGalleryModal and the page-level lightbox only ever
 * browse candidates Chronicle already has, so there's nothing to click when that list is
 * empty. Lets the user paste any direct image URL, confirms it actually loads before offering
 * to pin it (the server independently re-validates and rejects anything unsafe to fetch —
 * see ExternalUrlSafety — this preview is just fast user feedback, not the security boundary).
 */
// Mirrors the server's own ExternalUrlSafety.IsWellFormedHttpUrl scheme check. The server
// re-validates before ever fetching anything (see this component's own docstring), but that
// check only runs once the user clicks a slot to pin the image -- the preview below renders
// straight into an <img src> before that round-trip, so a javascript:/data: URL pasted here
// would otherwise reach the DOM completely unvalidated.
//
// Returns a freshly re-serialized URL (via the URL object's own .toString(), not the original
// substring) rather than just a pass/fail boolean -- what reaches the DOM is a new string this
// function produced from a validated http(s) URL, never the literal characters the user typed.
const SAFE_URL_SCHEMES = new Set(['http:', 'https:'])

function toSafePreviewUrl(candidate: string): string | null {
  let parsed: URL
  try {
    parsed = new URL(candidate)
  } catch {
    return null
  }
  return SAFE_URL_SCHEMES.has(parsed.protocol) ? parsed.toString() : null
}

export function ManualImageUrlModal({ onClose, overrides, onSet, onClear, pendingSlot }: ManualImageUrlModalProps) {
  const [url, setUrl] = useState('')
  const [previewUrl, setPreviewUrl] = useState<string | null>(null)
  const [loadFailed, setLoadFailed] = useState(false)

  function handlePreview() {
    const trimmed = url.trim()
    if (!trimmed) return
    const safeUrl = toSafePreviewUrl(trimmed)
    if (!safeUrl) {
      setPreviewUrl(null)
      setLoadFailed(true)
      return
    }
    setLoadFailed(false)
    setPreviewUrl(safeUrl)
  }

  return (
    <div
      className={overlayStyles.lightboxOverlay}
      onClick={onClose}
      role="dialog"
      aria-modal="true"
      aria-label="Add image from URL"
    >
      <div className={styles.panel} onClick={e => e.stopPropagation()}>
        <button className={overlayStyles.lightboxClose} onClick={onClose} type="button" aria-label="Close">
          ✕
        </button>
        <h3 className={styles.title}>Add Image from URL</h3>
        <p className={styles.hint}>
          Paste a direct link to an image. Use this when none of Chronicle's providers found
          anything to choose from.
        </p>
        <div className={styles.inputRow}>
          <input
            type="text"
            className={styles.input}
            placeholder="https://example.com/poster.jpg"
            value={url}
            onChange={e => { setUrl(e.target.value); setPreviewUrl(null); setLoadFailed(false) }}
            onKeyDown={e => { if (e.key === 'Enter') handlePreview() }}
            autoFocus
          />
          <button
            type="button"
            className={styles.previewBtn}
            onClick={handlePreview}
            disabled={!url.trim()}
          >
            Preview
          </button>
        </div>

        {previewUrl && (
          <div className={styles.previewArea}>
            <img
              key={previewUrl}
              src={previewUrl}
              alt=""
              className={styles.previewImg}
              onError={() => setLoadFailed(true)}
            />
            {loadFailed ? (
              <p className={styles.error}>Couldn't load an image from that URL. Check the link and try again.</p>
            ) : (
              <ImageSlotControls
                imageUrl={previewUrl}
                overrides={overrides}
                onSet={slot => onSet(slot, previewUrl)}
                onClear={onClear}
                pendingSlot={pendingSlot}
              />
            )}
          </div>
        )}
      </div>
    </div>
  )
}
