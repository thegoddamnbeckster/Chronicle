import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getDuplicateCandidates,
  dismissDuplicate,
  triggerDuplicateScan,
  type DuplicateCandidate,
  type DuplicateCandidateItem,
} from '@/api/duplicates'
import { deleteMedia } from '@/api/media'
import MergeModal from '@/components/MergeModal'
import styles from './DuplicatesPage.module.css'
import { PosterImage } from '@/components/PosterImage'
import { displayExternalIds } from '@/utils/externalId'

export default function DuplicatesPage() {
  const qc = useQueryClient()
  const [page, setPage] = useState(1)
  const [mergeTarget, setMergeTarget] = useState<DuplicateCandidate | null>(null)

  const { data, isLoading } = useQuery({
    queryKey: ['duplicates', page],
    queryFn: () => getDuplicateCandidates(page),
  })

  const dismiss = useMutation({
    mutationFn: ({ a, b }: { a: number; b: number }) => dismissDuplicate(a, b),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['duplicates'] }),
  })

  // Deleting one side resolves the pair without merging -- per-user preference (2026-09-19):
  // "I would also prefer to have the ability to delete one, not just merge." The candidate row
  // itself is cleaned up automatically (media_item_duplicate_candidates cascades off either
  // item id), so no separate dismiss/cleanup call is needed here.
  //
  // deletingIds (not deleteItem.isPending/.variables) tracks which item(s) are in flight --
  // caught in review: a single mutation's own isPending/variables only ever reflects its MOST
  // RECENT call, so deleting item X then, before that resolves, deleting item Y in another row
  // would overwrite variables to Y and make X's button look idle again while X's DELETE is
  // still in flight, letting a second click race a request against an item mid-deletion.
  // onMutate/onSettled fire once per individual mutate() call, so tracking ids through them
  // (rather than through the hook's own single-call state) supports overlapping deletes safely.
  const [deletingIds, setDeletingIds] = useState<Set<number>>(new Set())
  const deleteItem = useMutation({
    mutationFn: (id: number) => deleteMedia(id),
    onMutate: (id: number) => setDeletingIds(prev => new Set(prev).add(id)),
    onSettled: (_data, _error, id: number) => setDeletingIds(prev => {
      const next = new Set(prev)
      next.delete(id)
      return next
    }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['duplicates'] }),
  })

  const scan = useMutation({
    mutationFn: triggerDuplicateScan,
    onSuccess: () => setTimeout(() => qc.invalidateQueries({ queryKey: ['duplicates'] }), 2000),
  })

  const total = data?.pagination?.total ?? 0
  const perPage = data?.pagination?.perPage ?? 20
  const totalPages = Math.max(1, Math.ceil(total / perPage))

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1>Duplicate Candidates</h1>
        <button onClick={() => scan.mutate()} disabled={scan.isPending} className={styles.scanBtn}>
          {scan.isPending ? 'Scanning…' : 'Rescan'}
        </button>
      </div>

      {isLoading && <p>Loading…</p>}
      {!isLoading && (!data?.data || data.data.length === 0) && (
        <p className={styles.empty}>No duplicate candidates found. Run a scan to populate this list.</p>
      )}

      <div className={styles.list}>
        {data?.data?.map(candidate => (
          <div key={candidate.candidateId} className={styles.row}>
            <ItemCard item={candidate.itemA} onDelete={() => deleteItem.mutate(candidate.itemA.id)}
              deleting={deletingIds.has(candidate.itemA.id)} />
            <div className={styles.vs}>vs</div>
            <ItemCard item={candidate.itemB} onDelete={() => deleteItem.mutate(candidate.itemB.id)}
              deleting={deletingIds.has(candidate.itemB.id)} />
            <div className={styles.actions}>
              <button className={styles.mergeBtn} onClick={() => setMergeTarget(candidate)}>
                Merge
              </button>
              <button
                className={styles.dismissBtn}
                onClick={() => dismiss.mutate({ a: candidate.itemA.id, b: candidate.itemB.id })}
                disabled={dismiss.isPending}
              >
                Dismiss
              </button>
            </div>
          </div>
        ))}
      </div>

      {totalPages > 1 && (
        <div className={styles.pagination}>
          <button onClick={() => setPage(p => Math.max(1, p - 1))} disabled={page <= 1}>
            Previous
          </button>
          <span>Page {page} of {totalPages}</span>
          <button onClick={() => setPage(p => Math.min(totalPages, p + 1))} disabled={page >= totalPages}>
            Next
          </button>
        </div>
      )}

      {mergeTarget && (
        <MergeModal
          itemA={mergeTarget.itemA}
          itemB={mergeTarget.itemB}
          onClose={() => setMergeTarget(null)}
          onMerged={() => {
            setMergeTarget(null)
            qc.invalidateQueries({ queryKey: ['duplicates'] })
          }}
        />
      )}
    </div>
  )
}

function ItemCard({
  item, onDelete, deleting,
}: {
  item: DuplicateCandidateItem
  onDelete: () => void
  deleting: boolean
}) {
  // Same display form as the merge dialog (see utils/externalId.ts) -- the two used to filter and
  // format ids differently, so one film's cards could look like they disagreed.
  const displayIds = displayExternalIds(item.externalIds)

  const handleDelete = () => {
    if (window.confirm(`Permanently delete "${item.name}"? This cannot be undone.`)) onDelete()
  }

  return (
    <div className={styles.card}>
      <Link to={`/media/${item.id}`} target="_blank" rel="noopener noreferrer" className={styles.posterLink}>
        <PosterImage posterUrl={item.posterUrl} name={item.name} imgClassName={styles.poster}
          placeholderContent="No poster" />
      </Link>
      <div className={styles.info}>
        {item.ancestors && item.ancestors.length > 0 && (
          <p className={styles.breadcrumb}>{item.ancestors.join(' › ')}</p>
        )}
        <Link to={`/media/${item.id}`} target="_blank" rel="noopener noreferrer" className={styles.nameLink}>
          <p className={styles.name}>{item.name}</p>
        </Link>
        {item.year && <span className={styles.year}>{item.year}</span>}
        <p className={styles.meta}>{item.mediaType} · Level {item.hierarchyLevel}</p>
        {item.overview && (
          <p className={styles.overview}>{item.overview}</p>
        )}
        {displayIds.length > 0 && (
          <div className={styles.externalIds}>
            {displayIds.map(e => (
              <span key={e.key} className={styles.idBadge}>
                {e.label}: {e.value}
              </span>
            ))}
          </div>
        )}
        {item.filePath && (
          <p className={styles.filePath} title={item.filePath}>
            📁 {item.filePath}
          </p>
        )}
        <button className={styles.deleteBtn} onClick={handleDelete} disabled={deleting}>
          {deleting ? 'Deleting…' : 'Delete this one from Chronicle'}
        </button>
      </div>
    </div>
  )
}
