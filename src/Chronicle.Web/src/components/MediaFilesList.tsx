import { useQuery } from '@tanstack/react-query'
import { getMediaFiles } from '@/api/media'
import styles from './MediaFilesList.module.css'

function formatSize(bytes: number | null): string {
  if (bytes == null) return ''
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`
}

/**
 * Every file the scanner recorded for an item (parts, cuts, an album folder), each marked when it is no longer on disk.
 * While loading, or when nothing was recorded, the single path from the item's own metadata is shown instead.
 */
export default function MediaFilesList({ mediaId, fallbackPath }: { mediaId: number; fallbackPath: string | null }) {
  const { data = [] } = useQuery({
    queryKey: ['media', mediaId, 'files'],
    queryFn: () => getMediaFiles(mediaId),
    enabled: !Number.isNaN(mediaId),
  })

  if (data.length === 0) {
    return fallbackPath ? <span className={styles.path}>{fallbackPath}</span> : null
  }
  return (
    <ul className={styles.list} aria-label="Files">
      {data.map(f => (
        <li key={f.path} className={f.exists ? undefined : styles.missing}>
          <span className={styles.path}>{f.path}</span>
          {f.type === 'folder' && <span className={styles.meta}>folder</span>}
          {f.exists && f.sizeBytes != null && <span className={styles.meta}>{formatSize(f.sizeBytes)}</span>}
          {!f.exists && <span className={styles.flag}>not found on disk</span>}
        </li>
      ))}
    </ul>
  )
}
