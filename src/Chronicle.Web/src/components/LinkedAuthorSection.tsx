import MediaDetailPage from '@/pages/media/MediaDetailPage'
import type { LinkedItem } from '@/types'
import styles from './LinkedAuthorSection.module.css'

/**
 * The library record of a person who is also an author (or musician, ...): two records, one real person, so the person's
 * page carries the whole of the other record too. It is the media detail page itself, embedded without its own top navigation
 * (this page supplies that), so everything that page does is here: status and rating, change type, merge, delete, reset
 * progress, series and books, every plugin panel, cast and crew, images, related files. Nothing is reimplemented, so the two
 * cannot drift apart.
 */
/** `headshotUrl`: the person's photo (null when there is none). The record shows that, not the cover art it resolved for itself. */
export function LinkedAuthorSection({ author, headshotUrl = null }: { author: LinkedItem; headshotUrl?: string | null }) {
  return (
    <section className={styles.section} aria-label={`${author.mediaTypeName} record`}>
      <div className={styles.header}>
        <h2 className={styles.title}>{author.mediaTypeName}</h2>
      </div>
      <MediaDetailPage embeddedId={author.itemId} embeddedPosterUrl={headshotUrl} />
    </section>
  )
}
