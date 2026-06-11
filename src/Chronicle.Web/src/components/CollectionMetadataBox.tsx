import { useState } from 'react'
import { Link } from 'react-router-dom'
import { PosterImage } from './PosterImage'
import { FanartImage } from './FanartImage'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getCollection, rebuildCollection } from '@/api/collections'
import styles from './CollectionMetadataBox.module.css'

function isFanartUrl(url: string | null | undefined): boolean {
  if (!url) return false
  try { return new URL(url).hostname.includes('fanart.tv') } catch { return false }
}

interface Props {
  mediaItemId: number
  /** When true, only show the collection header — no movie grid (use on movie detail pages) */
  compact?: boolean
}

export default function CollectionMetadataBox({ mediaItemId, compact = false }: Props) {
  const queryClient = useQueryClient()
  const [rebuildSummary, setRebuildSummary] = useState<string | null>(null)

  const { data, isLoading, isError } = useQuery({
    queryKey: ['collection', mediaItemId],
    queryFn: () => getCollection(mediaItemId),
    retry: false,
  })

  const rebuildMut = useMutation({
    mutationFn: () => rebuildCollection(data!.id),
    onSuccess: (result) => {
      setRebuildSummary(result.summary)
      if (result.collection) {
        queryClient.setQueryData(['collection', mediaItemId], result.collection)
      } else {
        // Collection was removed — refetch to show no-collection state
        queryClient.invalidateQueries({ queryKey: ['collection', mediaItemId] })
      }
    },
  })

  if (isLoading) return null

  // After a successful rebuild that removed the collection entirely, show only the summary.
  if ((isError || !data) && rebuildSummary) {
    return (
      <section className={styles.box}>
        <p className={styles.rebuildSummary}>{rebuildSummary}</p>
      </section>
    )
  }

  if (isError || !data) return null

  return (
    <section className={styles.box}>
      <h3 className={styles.heading}>
        {data.posterUrl && (
          isFanartUrl(data.posterUrl)
            ? <FanartImage src={data.posterUrl} wrapperClassName={styles.collectionPosterWrap} imgClassName={styles.collectionPosterImg} minHeight={60} />
            : <img src={data.posterUrl} alt="" className={styles.collectionPoster} />
        )}
        <span>Part of <Link to={`/media/${data.id}`} className={styles.collectionLink}><em>{data.name}</em></Link></span>
        {!compact && (
          <button
            className={styles.rebuildBtn}
            onClick={() => { setRebuildSummary(null); rebuildMut.mutate() }}
            disabled={rebuildMut.isPending}
            title="Re-check this collection against TMDB — re-parents incorrectly grouped movies and adds stubs for missing members"
          >
            {rebuildMut.isPending ? 'Rebuilding…' : 'Rebuild Collection'}
          </button>
        )}
      </h3>
      {!compact && rebuildSummary && (
        <p className={styles.rebuildSummary}>{rebuildSummary}</p>
      )}
      {!compact && rebuildMut.isError && (
        <p className={styles.rebuildError}>Rebuild failed — check the API logs.</p>
      )}
      {!compact && data.overview && <p className={styles.overview}>{data.overview}</p>}
      {!compact && (
        <div className={styles.grid}>
          {data.movies.map(movie => (
            <div
              key={movie.id}
              className={[
                styles.card,
                !movie.inLibrary ? styles.notInLibrary : '',
                movie.isStub ? styles.stubCard : '',
              ].filter(Boolean).join(' ')}
            >
              <Link to={`/media/${movie.id}`} className={styles.posterLink}>
                <div className={styles.posterWrap}>
                  {isFanartUrl(movie.posterUrl)
                    ? <FanartImage src={movie.posterUrl!} wrapperClassName={styles.moviePosterWrap} imgClassName={styles.moviePosterImg} minHeight={120} />
                    : <PosterImage posterUrl={movie.posterUrl} name={movie.name} />
                  }
                  {movie.isStub && (
                    <div className={styles.stubBanner}>Not in Library</div>
                  )}
                </div>
              </Link>
              <div className={styles.info}>
                <Link to={`/media/${movie.id}`} style={{ textDecoration: 'none', color: 'inherit' }}>
                  <div className={styles.name} title={movie.name}>{movie.name}</div>
                </Link>
                <div className={styles.metaRow}>
                  {movie.year && <span className={styles.year}>{movie.year}</span>}
                  {movie.isStub && <span className={styles.stubLabel}>Not in your library</span>}
                  {!movie.isStub && movie.rating != null && (
                    <span className={styles.rating} title="Public rating">★ {movie.rating.toFixed(1)}</span>
                  )}
                  {movie.userRating != null && (
                    <span className={styles.userRating} title={`My Rating${movie.userRatingSource ? ` (via ${movie.userRatingSource})` : ''}`}>♥ {movie.userRating}</span>
                  )}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}
