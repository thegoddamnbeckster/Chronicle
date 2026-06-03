import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { mergeItems } from '@/api/duplicates'
import styles from './MergeModal.module.css'

interface Item {
  id: number
  name: string
  posterUrl: string | null
}

interface Props {
  itemA: Item
  itemB: Item
  onClose: () => void
  onMerged: () => void
}

export default function MergeModal({ itemA, itemB, onClose, onMerged }: Props) {
  const [winnerId, setWinnerId] = useState<number | null>(null)

  const merge = useMutation({
    mutationFn: () => mergeItems(itemA.id, itemB.id, winnerId!),
    onSuccess: onMerged,
  })

  const loser = winnerId === itemA.id ? itemB : itemA

  return (
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
              {item.posterUrl
                ? <img src={item.posterUrl} alt={item.name} className={styles.poster} />
                : <div className={styles.posterPlaceholder} />}
              <p className={styles.name}>{item.name}</p>
              {winnerId === item.id && <span className={styles.winnerBadge}>Winner</span>}
            </button>
          ))}
        </div>

        {winnerId && (
          <p className={styles.preview}>
            <strong>"{loser.name}"</strong> will be absorbed into the winner and saved as an AKA.
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
    </div>
  )
}
