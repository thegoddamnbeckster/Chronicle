import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { getCollection } from '@/api/collections'
import styles from './CollectionMetadataBox.module.css'

interface Props {
  mediaItemId: number
}

export default function CollectionMetadataBox({ mediaItemId }: Props) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['collection', mediaItemId],
    queryFn: () => getCollection(mediaItemId),
    retry: false,  // 404 = no collection; don't spam retries
  })

  if (isLoading) return null
  if (isError || !data) return null

  return (
    <section className={styles.box}>
      <h3 className={styles.heading}>
        {data.posterUrl && (
          <img src={data.posterUrl} alt="" className={styles.collectionPoster} />
        )}
        <span>Part of <em>{data.name}</em></span>
      </h3>
      {data.overview && <p className={styles.overview}>{data.overview}</p>}
      <div className={styles.grid}>
        {data.movies.map(movie => (
          <Link
            key={movie.id}
            to={`/media/${movie.id}`}
            className={`${styles.card} ${movie.inLibrary ? styles.inLibrary : styles.notInLibrary}`}
            title={`${movie.name}${movie.year ? ` (${movie.year})` : ''}`}
          >
            {movie.posterUrl
              ? <img src={movie.posterUrl} alt={movie.name} className={styles.poster} />
              : <div className={styles.posterPlaceholder}>{movie.name[0]}</div>
            }
            <div className={styles.cardName}>{movie.name}</div>
            {movie.year && <div className={styles.cardYear}>{movie.year}</div>}
            {!movie.inLibrary && <div className={styles.missingBadge}>Not in library</div>}
          </Link>
        ))}
      </div>
    </section>
  )
}
