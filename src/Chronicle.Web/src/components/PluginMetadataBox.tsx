import React, { useState, useRef, useEffect } from 'react'
import { JsonTree } from './JsonTree'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { refreshMediaForPlugin, clearMediaExternalId, suppressMediaMatch } from '@/api/media'
import type { ExternalId, RefreshLog } from '@/types'
import { IMAGE_KEYS, IMAGE_ARRAY_KEYS, toLabel, isImageUrl, extractImages, type ImageEntry } from '@/utils/imageExtractor'
import styles from './PluginMetadataBox.module.css'

// ── Helpers ─────────────────────────────────────────────────────────────────

/** Keys that are skip-rendered — already shown elsewhere on the page. */
const SKIP_KEYS = new Set([
  'title', 'externalid', 'source', 'totalresults', 'total_results',
])

// ── Component ────────────────────────────────────────────────────────────────

export interface PluginMetadataBoxProps {
  mediaId: number
  pluginId: string
  pluginName: string
  iconUrl?: string | null
  fixMatchHint?: string | null
  metadata: Record<string, unknown>
  externalIds: ExternalId[]
  refreshLogs?: RefreshLog[] | null
  hierarchyLevel: number
  /**
   * When provided, clicking an image thumbnail calls this instead of opening
   * the internal lightbox. The argument is the image's index within the
   * page-level allImages array (imageStartIndex + local index).
   */
  onImageClick?: (globalIndex: number) => void
  /**
   * The index of this plugin's first image within the page-level allImages array.
   * Only used when onImageClick is provided.
   */
  imageStartIndex?: number
}

export function PluginMetadataBox({
  mediaId,
  pluginId,
  pluginName,
  iconUrl,
  fixMatchHint,
  metadata,
  externalIds,
  refreshLogs,
  hierarchyLevel,
  onImageClick,
  imageStartIndex = 0,
}: PluginMetadataBoxProps) {
  const qc = useQueryClient()
  const [fixMatchOpen, setFixMatchOpen] = useState(false)
  const [fixMatchInput, setFixMatchInput] = useState('')
  const [lightboxIdx, setLightboxIdx] = useState<number | null>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (fixMatchOpen) inputRef.current?.focus()
  }, [fixMatchOpen])

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ['media', mediaId] })
    qc.invalidateQueries({ queryKey: ['library'] })
  }

  const refreshMut = useMutation({
    mutationFn: () => refreshMediaForPlugin(mediaId, pluginId),
    onSuccess: (updated) => {
      qc.setQueryData(['media', mediaId], updated)
      qc.invalidateQueries({ queryKey: ['library'] })
    },
  })

  const fixMatchMut = useMutation({
    mutationFn: () => refreshMediaForPlugin(mediaId, pluginId, fixMatchInput.trim()),
    onSuccess: (updated) => {
      qc.setQueryData(['media', mediaId], updated)
      qc.invalidateQueries({ queryKey: ['library'] })
      setFixMatchOpen(false)
      setFixMatchInput('')
    },
  })

  const clearMatchMut = useMutation({
    mutationFn: () => clearMediaExternalId(mediaId, pluginId),
    onSuccess: invalidate,
  })

  const suppressMut = useMutation({
    mutationFn: () => suppressMediaMatch(mediaId, pluginId),
    onSuccess: invalidate,
  })

  // Determine state of this plugin's external ID
  const pluginExtIds = externalIds.filter(
    e => e.source.toLowerCase() === pluginId.toLowerCase(),
  )
  const isSuppressed = pluginExtIds.some(e => e.externalId === '__suppress__')
  const hasRealId = pluginExtIds.some(e => e.externalId !== '__suppress__')

  // Last refresh log for this plugin
  const log = refreshLogs?.find(l =>
    l.providerName.toLowerCase() === pluginName.toLowerCase()
  )

  // ── Partition metadata fields ──────────────────────────────────────────────

  const imageEntries: ImageEntry[] = extractImages(metadata, SKIP_KEYS)
  const dataRows: { key: string; value: unknown }[] = []
  for (const [key, value] of Object.entries(metadata)) {
    const lower = key.toLowerCase()
    if (SKIP_KEYS.has(lower)) continue
    if (value === null || value === undefined) continue
    if (IMAGE_ARRAY_KEYS.has(lower) && Array.isArray(value)) continue
    if (IMAGE_KEYS.has(lower) && typeof value === 'string') continue
    if (typeof value === 'string' && isImageUrl(value)) continue
    dataRows.push({ key, value })
  }

  // Close lightbox on Escape key (only when using internal lightbox)
  useEffect(() => {
    if (onImageClick) return          // page-level lightbox handles keyboard
    if (lightboxIdx === null) return
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setLightboxIdx(null)
      if (e.key === 'ArrowRight') setLightboxIdx(i => i !== null && i < imageEntries.length - 1 ? i + 1 : i)
      if (e.key === 'ArrowLeft') setLightboxIdx(i => i !== null && i > 0 ? i - 1 : i)
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [lightboxIdx, imageEntries.length, onImageClick])

  // ── Render ─────────────────────────────────────────────────────────────────

  const renderValue = (key: string, value: unknown): React.ReactNode => {
    const lower = key.toLowerCase()

    if (Array.isArray(value)) {
      if (value.length === 0) return null
      const isTagLike = lower.includes('genre') || lower.includes('tag') || lower.includes('style')
      if (isTagLike) {
        return (
          <div className={styles.tagList}>
            {(value as string[]).slice(0, 20).map(t => (
              <span key={String(t)} className={styles.tag}>{String(t)}</span>
            ))}
          </div>
        )
      }
      return <span className={styles.value}>{(value as unknown[]).slice(0, 8).map(String).join(', ')}</span>
    }

    if (typeof value === 'number') {
      if (lower.includes('rating') || lower.includes('voteaverage')) {
        return <span className={styles.value}>{value.toFixed(1)}&thinsp;/&thinsp;10</span>
      }
      if (lower.includes('runtime') || lower.includes('duration')) {
        return <span className={styles.value}>{value} min</span>
      }
      return <span className={styles.value}>{value}</span>
    }

    if (typeof value === 'boolean') {
      return <span className={styles.value}>{value ? 'Yes' : 'No'}</span>
    }

    if (typeof value === 'object' && value !== null) {
      return (
        <div className={styles.value}>
          <JsonTree
            data={value}
            depth={0}
            onImageClick={onImageClick
              ? (url) => {
                  const localIdx = imageEntries.findIndex(img => img.url === url)
                  if (localIdx >= 0) onImageClick(imageStartIndex + localIdx)
                  else window.open(url, '_blank')
                }
              : undefined
            }
          />
        </div>
      )
    }

    return <span className={styles.value}>{String(value)}</span>
  }

  return (
    <div className={styles.box}>
      {/* Header */}
      <div className={styles.header}>
        <div className={styles.brand}>
          {iconUrl && (
            <img
              src={iconUrl}
              alt=""
              className={styles.icon}
              aria-hidden
              onError={e => { e.currentTarget.style.display = 'none' }}
            />
          )}
          <span className={styles.name}>{pluginName}</span>
          {log && (
            <p className={styles.timestamp}>
              {log.succeeded
                ? `Last refreshed ${new Date(log.refreshedAt).toLocaleDateString()} ${new Date(log.refreshedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`
                : `Last refresh failed: ${log.errorMessage ?? 'unknown error'}`}
            </p>
          )}
        </div>
        <div className={styles.actions}>
          <button
            className={styles.refreshBtn}
            onClick={() => refreshMut.mutate()}
            disabled={refreshMut.isPending}
            title={`Re-fetch metadata from ${pluginName}`}
          >
            {refreshMut.isPending ? 'Refreshing…' : '↻ Refresh'}
          </button>
          {hierarchyLevel === 0 && (
            <>
              <button
                className={styles.fixMatchBtn}
                onClick={() => { setFixMatchOpen(v => !v); fixMatchMut.reset() }}
                title={`Manually specify the correct ${pluginName} match`}
              >
                ✎ Fix Match
              </button>
              {hasRealId && (
                <button
                  className={styles.clearMatchBtn}
                  onClick={() => clearMatchMut.mutate()}
                  disabled={clearMatchMut.isPending}
                  title="Remove this match — refresh will attempt a new auto-search next cycle"
                >
                  {clearMatchMut.isPending ? 'Clearing…' : '✕ Clear Match'}
                </button>
              )}
              {isSuppressed ? (
                <button
                  className={styles.resumeMatchBtn}
                  onClick={() => clearMatchMut.mutate()}
                  disabled={clearMatchMut.isPending}
                  title="Re-enable auto-matching for this item"
                >
                  {clearMatchMut.isPending ? 'Resuming…' : '↺ Resume Auto-Match'}
                </button>
              ) : !hasRealId && (
                <button
                  className={styles.suppressMatchBtn}
                  onClick={() => suppressMut.mutate()}
                  disabled={suppressMut.isPending}
                  title="Mark as unmatched — refresh will never auto-search for this item again"
                >
                  {suppressMut.isPending ? 'Suppressing…' : '⊘ No Match'}
                </button>
              )}
            </>
          )}
          {hierarchyLevel > 0 && hasRealId && (
            <button
              className={styles.clearMatchBtn}
              onClick={() => clearMatchMut.mutate()}
              disabled={clearMatchMut.isPending}
              title="Remove the stale match from this item"
            >
              {clearMatchMut.isPending ? 'Clearing…' : '✕ Clear Match'}
            </button>
          )}
        </div>
      </div>

      {/* Fix Match panel */}
      {fixMatchOpen && (
        <div className={styles.fixMatchPanel}>
          {fixMatchHint ? (
            <p className={styles.fixMatchHint}>{fixMatchHint}</p>
          ) : (
            <p className={styles.fixMatchHint}>Enter an ID or URL for {pluginName}</p>
          )}
          <div className={styles.fixMatchRow}>
            <input
              ref={inputRef}
              className={styles.fixMatchInput}
              type="text"
              placeholder={`${pluginName} ID or URL…`}
              value={fixMatchInput}
              onChange={e => { setFixMatchInput(e.target.value); fixMatchMut.reset() }}
              onKeyDown={e => {
                if (e.key === 'Enter' && fixMatchInput.trim()) fixMatchMut.mutate()
                if (e.key === 'Escape') { setFixMatchOpen(false); setFixMatchInput('') }
              }}
            />
            <button
              className={styles.fixMatchApplyBtn}
              onClick={() => fixMatchMut.mutate()}
              disabled={fixMatchMut.isPending || !fixMatchInput.trim()}
            >
              {fixMatchMut.isPending ? 'Applying…' : 'Apply'}
            </button>
          </div>
          {fixMatchMut.isError && (
            <p className={styles.error}>{(fixMatchMut.error as Error).message}</p>
          )}
        </div>
      )}

      {/* Metadata grid */}
      <div className={styles.grid}>
        {dataRows.map(({ key, value }) => {
          const rendered = renderValue(key, value)
          if (rendered === null) return null
          return (
            <div key={key} className={styles.row}>
              <span className={styles.label}>{toLabel(key)}</span>
              {rendered}
            </div>
          )
        })}

        {/* External ID row */}
        {hasRealId && (
          <div className={styles.row}>
            <span className={styles.label}>ID</span>
            <div className={styles.idChips}>
              {pluginExtIds
                .filter(e => e.externalId !== '__suppress__')
                .map(eid => (
                  <span key={eid.externalId} className={styles.idChip}>{eid.externalId}</span>
                ))}
            </div>
          </div>
        )}

        {/* Images row */}
        {imageEntries.length > 0 && (
          <div className={`${styles.row} ${styles.rowImages}`}>
            <span className={styles.label}>Images</span>
            <div className={styles.imageLinks}>
              {imageEntries.slice(0, 8).map((img, i) => (
                <button
                  key={i}
                  className={styles.imageLink}
                  title={img.label}
                  onClick={() => onImageClick
                    ? onImageClick(imageStartIndex + i)
                    : setLightboxIdx(i)
                  }
                  type="button"
                >
                  <img
                    src={img.url}
                    alt={img.label}
                    className={styles.thumbnail}
                    onError={e => { e.currentTarget.style.display = 'none' }}
                  />
                  <span className={styles.thumbnailLabel}>{img.label}</span>
                </button>
              ))}
            </div>
          </div>
        )}
      </div>

      {refreshMut.isError && (
        <p className={styles.error}>{`Refresh failed: ${(refreshMut.error as Error).message}`}</p>
      )}
      {clearMatchMut.isError && (
        <p className={styles.error}>{`Clear failed: ${(clearMatchMut.error as Error).message}`}</p>
      )}

        {/* ── Lightbox (internal — only used when no page-level onImageClick) ── */}
        {!onImageClick && lightboxIdx !== null && (
          <div
            className={styles.lightboxOverlay}
            onClick={() => setLightboxIdx(null)}
            role="dialog"
            aria-modal="true"
            aria-label={imageEntries[lightboxIdx]?.label ?? 'Image'}
          >
            <button
              className={styles.lightboxClose}
              onClick={() => setLightboxIdx(null)}
              type="button"
              aria-label="Close"
            >
              ✕
            </button>
            {lightboxIdx > 0 && (
              <button
                className={`${styles.lightboxNav} ${styles.lightboxNavPrev}`}
                onClick={e => { e.stopPropagation(); setLightboxIdx(lightboxIdx - 1) }}
                type="button"
                aria-label="Previous image"
              >
                ‹
              </button>
            )}
            <img
              className={styles.lightboxImg}
              src={imageEntries[lightboxIdx]?.url}
              alt={imageEntries[lightboxIdx]?.label}
              onClick={e => e.stopPropagation()}
            />
            <div className={styles.lightboxCaption}>
              {imageEntries[lightboxIdx]?.label}
              {imageEntries.length > 1 && (
                <span className={styles.lightboxCounter}> {lightboxIdx + 1} / {Math.min(imageEntries.length, 8)}</span>
              )}
            </div>
            {lightboxIdx < Math.min(imageEntries.length, 8) - 1 && (
              <button
                className={`${styles.lightboxNav} ${styles.lightboxNavNext}`}
                onClick={e => { e.stopPropagation(); setLightboxIdx(lightboxIdx + 1) }}
                type="button"
                aria-label="Next image"
              >
                ›
              </button>
            )}
          </div>
        )}
    </div>
  )
}
