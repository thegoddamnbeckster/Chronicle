import { useQuery } from '@tanstack/react-query'
import { getRelatedFiles } from '@/api/media'
import styles from './RelatedFilesBox.module.css'

const KIND_LABELS: Record<string, string> = {
  subtitle: 'Subtitles', artwork: 'Artwork', theme: 'Theme music', extra: 'Extras', booklet: 'Booklets', other: 'Other files',
}

function fileName(path: string): string {
  return path.split(/[\\/]/).filter(Boolean).pop() ?? path
}

function formatSize(bytes: number | null): string {
  if (bytes == null) return ''
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

/** Subtitles, artwork and extras a scan found with this item. Renders nothing when the item has none. */
export default function RelatedFilesBox({ mediaId }: { mediaId: number }) {
  const { data = [] } = useQuery({
    queryKey: ['media', mediaId, 'related-files'],
    queryFn: () => getRelatedFiles(mediaId),
    enabled: !Number.isNaN(mediaId),
  })
  if (data.length === 0) return null

  const kinds = [...new Set(data.map(f => f.kind))]
  return (
    <details className={styles.box}>
      <summary className={styles.summary}>Related files ({data.length})</summary>
      {kinds.map(kind => (
        <div key={kind} className={styles.group}>
          <div className={styles.kind}>{KIND_LABELS[kind] ?? kind}</div>
          <ul className={styles.list}>
            {data.filter(f => f.kind === kind).map(f => (
              <li key={f.id} className={f.missingSince ? styles.missing : undefined} title={f.path}>
                {f.kind === 'artwork' && !f.missingSince && (
                  <img className={styles.thumb} loading="lazy" alt={fileName(f.path)} src={`/api/v1/media/${mediaId}/related-files/${f.id}/content`} />
                )}
                <span>{fileName(f.path)}</span>
                {f.sizeBytes != null && <span className={styles.size}>{formatSize(f.sizeBytes)}</span>}
                {f.missingSince && <span className={styles.flag}>not found at last scan</span>}
              </li>
            ))}
          </ul>
        </div>
      ))}
    </details>
  )
}
