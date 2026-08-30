import { Link } from 'react-router-dom'
import { PosterImage } from '@/components/PosterImage'
import type { PersonListItem } from '@/types'
import styles from './PersonCard.module.css'

function formatYear(iso: string | null): string | null {
  if (!iso) return null
  const year = new Date(iso).getFullYear()
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
 * docs/plans/2026-08-28-people-section-design.md Section 5. */
export function PersonCard({ person }: { person: PersonListItem }) {
  const dates = formatDateRange(person.birthDate, person.deathDate)

  return (
    <Link to={`/people/${person.id}`} className={styles.personCard}>
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
