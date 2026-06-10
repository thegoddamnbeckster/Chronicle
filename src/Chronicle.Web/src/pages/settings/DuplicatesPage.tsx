import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getDuplicateCandidates,
  dismissDuplicate,
  triggerDuplicateScan,
  type DuplicateCandidate,
  type DuplicateCandidateItem,
} from '@/api/duplicates'
import MergeModal from '@/components/MergeModal'
import styles from './DuplicatesPage.module.css'
import { PosterImage } from '@/components/PosterImage'

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
            <ItemCard item={candidate.itemA} />
            <div className={styles.vs}>vs</div>
            <ItemCard item={candidate.itemB} />
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

function ItemCard({ item }: { item: DuplicateCandidateItem }) {
  // Show only the most useful external IDs (skip internal/noisy sources)
  const displayIds = item.externalIds.filter(e =>
    ['tmdb', 'imdb', 'tvdb', 'musicbrainz', 'simkl', 'trakt', 'hardcover', 'igdb'].includes(e.source.toLowerCase())
  )

  return (
    <div className={styles.card}>
      <PosterImage posterUrl={item.posterUrl} name={item.name} imgClassName={styles.poster}
        placeholderContent="No poster" />
      <div className={styles.info}>
        <p className={styles.name}>{item.name}</p>
        {item.year && <span className={styles.year}>{item.year}</span>}
        <p className={styles.meta}>{item.mediaType} · Level {item.hierarchyLevel}</p>
        {item.overview && (
          <p className={styles.overview}>{item.overview}</p>
        )}
        {displayIds.length > 0 && (
          <div className={styles.externalIds}>
            {displayIds.map(e => (
              <span key={`${e.source}:${e.externalId}`} className={styles.idBadge}>
                {e.source}: {e.externalId}
              </span>
            ))}
          </div>
        )}
        {item.filePath && (
          <p className={styles.filePath} title={item.filePath}>
            📁 {item.filePath}
          </p>
        )}
      </div>
    </div>
  )
}
