import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getLibrary, updateLibraryEntry, removeFromLibrary } from '@/api/library'
import type { LibraryStatus } from '@/types'
import styles from './LibraryPage.module.css'

const STATUS_OPTIONS: LibraryStatus[] = ['Watching', 'PlanToWatch', 'Completed', 'Dropped', 'OnHold', 'Rewatching']

export default function LibraryPage() {
  const [filter, setFilter] = useState<LibraryStatus | undefined>(undefined)
  const qc = useQueryClient()

  const { data: entries = [], isLoading } = useQuery({
    queryKey: ['library', filter],
    queryFn: () => getLibrary(filter),
  })

  const updateMut = useMutation({
    mutationFn: ({ id, status }: { id: number; status: LibraryStatus }) =>
      updateLibraryEntry(id, { status }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['library'] }),
  })

  const removeMut = useMutation({
    mutationFn: (id: number) => removeFromLibrary(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['library'] }),
  })

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h2 className={styles.heading}>Library</h2>
        <div className={styles.filters}>
          <button
            className={filter === undefined ? styles.filterActive : styles.filter}
            onClick={() => setFilter(undefined)}
          >All</button>
          {STATUS_OPTIONS.map(s => (
            <button
              key={s}
              className={filter === s ? styles.filterActive : styles.filter}
              onClick={() => setFilter(s)}
            >{s}</button>
          ))}
        </div>
      </div>

      {isLoading && <p className={styles.empty}>Loading…</p>}

      {!isLoading && entries.length === 0 && (
        <p className={styles.empty}>No items in your library yet.</p>
      )}

      <div className={styles.grid}>
        {entries.map(entry => (
          <div key={entry.id} className={styles.card}>
            <Link to={`/media/${entry.mediaItem.id}`} className={styles.posterLink}>
              <div className={styles.poster}>
                {entry.mediaItem.posterUrl
                  ? (
                    <img
                      src={entry.mediaItem.posterUrl}
                      alt={entry.mediaItem.name}
                      onError={e => {
                        const img = e.currentTarget
                        img.style.display = 'none'
                        const placeholder = img.nextElementSibling as HTMLElement | null
                        if (placeholder) placeholder.style.display = 'flex'
                      }}
                    />
                  )
                  : null
                }
                <div
                  className={styles.posterPlaceholder}
                  style={{ display: entry.mediaItem.posterUrl ? 'none' : 'flex' }}
                >
                  {entry.mediaItem.name.charAt(0)}
                </div>
              </div>
              <span className={styles.mediaTypeBadge}>{entry.mediaItem.mediaTypeName}</span>
            </Link>
            <div className={styles.info}>
              <Link to={`/media/${entry.mediaItem.id}`} className={styles.nameLink}>
                <div className={styles.name}>{entry.mediaItem.name}</div>
              </Link>
              {entry.mediaItem.year && <div className={styles.year}>{entry.mediaItem.year}</div>}
              <select
                className={styles.statusSelect}
                value={entry.status}
                onChange={e => updateMut.mutate({ id: entry.id, status: e.target.value as LibraryStatus })}
              >
                {STATUS_OPTIONS.map(s => <option key={s} value={s}>{s}</option>)}
              </select>
              <button
                className={styles.removeBtn}
                onClick={() => { if (confirm('Remove from library?')) removeMut.mutate(entry.id) }}
              >Remove</button>
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}
