import { useState } from 'react'
import { createPortal } from 'react-dom'
import { useMutation } from '@tanstack/react-query'
import { mergeItems } from '@/api/duplicates'
import styles from './MergeModal.module.css'

export interface MergeItem {
  id: number
  name: string
  posterUrl: string | null
  mediaTypeName?: string | null
  year?: number | null
  runtimeMinutes?: number | null
  overview?: string | null
  filePath?: string | null   // first file path from fileScanner, if any
}

interface Props {
  itemA: MergeItem
  itemB: MergeItem
  onClose: () => void
  onMerged: (winnerId: number) => void
}

export default function MergeModal({ itemA, itemB, onClose, onMerged }: Props) {
  const [winnerId, setWinnerId] = useState<number | null>(null)

  const merge = useMutation({
    mutationFn: () => mergeItems(itemA.id, itemB.id, winnerId!),
    onSuccess: () => onMerged(winnerId!),
  })

  const winner = winnerId === itemA.id ? itemA : itemB
  const loser  = winnerId === itemA.id ? itemB : itemA

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={e => e.stopPropagation()}>
        <h2 className={styles.title}>Select the Canonical Record</h2>
        <p className={styles.subtitle}>
          The winner becomes the canonical entry. The other item's name will be saved as an AKA if
          the names differ.
        </p>

        <div className={styles.items}>
          {[itemA, itemB].map(item => (
            <button
              key={item.id}
              className={`${styles.itemCard} ${winnerId === item.id ? styles.selected : ''}`}
              onClick={() => setWinnerId(item.id)}
            >
              {winnerId === item.id && <span className={styles.winnerBadge}>Winner</span>}

              <div className={styles.cardInner}>
                {/* Poster */}
                <div className={styles.posterWrap}>
                  {item.posterUrl
                    ? <img src={item.posterUrl} alt={item.name} className={styles.poster} />
                    : <div className={styles.posterPlaceholder}>{item.name.charAt(0)}</div>}
                </div>

                {/* Metadata */}
                <div className={styles.meta}>
                  <p className={styles.name}>{item.name}</p>

                  <div className={styles.chips}>
                    {item.mediaTypeName && (
                      <span className={styles.chip}>{item.mediaTypeName}</span>
                    )}
                    {item.year && (
                      <span className={styles.chip}>{item.year}</span>
                    )}
                    {item.runtimeMinutes != null && item.runtimeMinutes > 0 && (
                      <span className={styles.chip}>{item.runtimeMinutes} min</span>
                    )}
                  </div>

                  {item.overview && (
                    <p className={styles.overview}>{item.overview}</p>
                  )}

                  {item.filePath && (
                    <p className={styles.filePath} title={item.filePath}>
                      <span className={styles.filePathLabel}>File</span>
                      <span className={styles.filePathValue}>
                        {item.filePath.split(/[/\\]/).pop()}
                      </span>
                      <span className={styles.filePathFull}>{item.filePath}</span>
                    </p>
                  )}

                  {!item.filePath && (
                    <p className={styles.noFile}>No physical file</p>
                  )}
                </div>
              </div>
            </button>
          ))}
        </div>

        {winnerId && (
          <p className={styles.preview}>
            <strong>"{loser.name}"</strong> will be absorbed into <strong>"{winner.name}"</strong> and saved as an AKA.
            {winner.mediaTypeName && loser.mediaTypeName && winner.mediaTypeName !== loser.mediaTypeName && (
              <span className={styles.typeMismatchNote}>
                {' '}The result will be type <strong>{winner.mediaTypeName}</strong> (the winner's type).
              </span>
            )}
          </p>
        )}

        {merge.isError && (
          <p className={styles.error}>Merge failed. Please try again.</p>
        )}

        <div className={styles.footer}>
          <button className={styles.cancelBtn} onClick={onClose}>Cancel</button>
          <button
            className={styles.confirmBtn}
            disabled={!winnerId || merge.isPending}
            onClick={() => merge.mutate()}
          >
            {merge.isPending ? 'Merging…' : 'Confirm Merge'}
          </button>
        </div>
      </div>
    </div>,
    document.body
  )
}
