import { Link } from 'react-router-dom'
import { PosterImage } from '@/components/PosterImage'
import type { PersonListItem } from '@/types'
import styles from './PersonCard.module.css'

function formatYear(iso: string | null): string | null {
  if (!iso) return null
  // getUTCFullYear, not getFullYear -- a birth/death date is stored as midnight UTC with no
  // meaningful time-of-day, so a viewer west of UTC (e.g. "1970-01-01T00:00:00Z") would
  // otherwise see the year rolled back to 1969 (same class of bug as PersonDetailPage's
  // formatDate, fixed alongside this).
  const year = new Date(iso).getUTCFullYear()
  return Number.isNaN(year) ? null : String(year)
}

function formatDateRange(birth: string | null, death: string | null): string | null {
  const b = formatYear(birth)
  const d = formatYear(death)
  if (b && d) return `${b} – ${d}`
  if (b) return b
  if (d) return `d. ${d}`
  return null
}

/** One entry on the catalog-wide People grid (PeopleLibraryPage) -- see
 * docs/plans/2026-08-28-people-section-design.md Section 5.
 *
 * navState, when given, is carried as router location state so PersonDetailPage can build
 * the same "↑ People" / Prev / Next navigation MediaDetailPage already has for library items
 * -- per-user request (2026-08-30): "movie details have an up library button... I need that
 * same kind of thing for people. also next and previous buttons." Mirrors LibraryPage's own
 * per-section listIds/listLabel state passed to its media cards. */
export function PersonCard({
  person, navState,
}: {
  person: PersonListItem
  navState?: { listIds: number[]; listLabel?: string }
}) {
  const dates = formatDateRange(person.birthDate, person.deathDate)

  return (
    <Link to={`/people/${person.id}`} state={navState} className={styles.personCard}>
      <div className={styles.poster}>
        <PosterImage posterUrl={person.posterUrl} name={person.name} lazy />
        {person.deathDate && (
          <div className={styles.deceasedBar} title={`Died ${formatYear(person.deathDate)}`} />
        )}
      </div>
      <div className={styles.info}>
        <div className={styles.name}>{person.name}</div>
        {person.roles.length > 0 && (
          <div className={styles.positions}>{person.roles.join(', ')}</div>
        )}
        {dates && <div className={styles.dates}>{dates}</div>}
      </div>
    </Link>
  )
}
