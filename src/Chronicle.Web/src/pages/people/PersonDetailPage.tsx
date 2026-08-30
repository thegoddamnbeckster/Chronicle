import { Link, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { getMedia } from '@/api/media'
import { getPersonCredits } from '@/api/people'
import { PosterImage } from '@/components/PosterImage'
import styles from './PersonDetailPage.module.css'

function formatDate(iso: string | null | undefined): string | null {
  if (!iso) return null
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? null : d.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' })
}

/** Person detail page -- reuses the generic GET /media/:id endpoint for the person's own
 * data (a person is a MediaItem, no separate detail endpoint needed) plus a dedicated
 * role-grouped credits section. Deliberately a separate component from MediaDetailPage, not
 * a variant of it: no change-type/merge/library-status/children-grid sections apply to a
 * person (docs/plans/2026-08-28-people-section-design.md Section 6). */
export default function PersonDetailPage() {
  const { id } = useParams<{ id: string }>()
  const personId = Number(id)

  const { data: person, isLoading } = useQuery({
    queryKey: ['media', personId],
    queryFn: () => getMedia(personId),
    enabled: !Number.isNaN(personId),
  })

  const { data: creditGroups = [] } = useQuery({
    queryKey: ['person-credits', personId],
    queryFn: () => getPersonCredits(personId),
    enabled: !Number.isNaN(personId),
  })

  if (isLoading || !person) {
    return <div className={styles.loading}>Loading…</div>
  }

  const bio = person.resolvedMetadata?.overview ?? person.overview
  const birth = formatDate(person.birthDate)
  const death = formatDate(person.deathDate)

  return (
    <div className={styles.page}>
      <div className={styles.hero}>
        <div className={styles.posterWrap}>
          <PosterImage posterUrl={person.posterUrl} name={person.name} />
        </div>
        <div className={styles.heroInfo}>
          <h1 className={styles.name}>
            {person.name}
            {person.deathDate && <span className={styles.deceasedBadge}>Deceased</span>}
          </h1>
          {(birth || death) && (
            <div className={styles.dates}>
              {birth && <span>Born {birth}</span>}
              {birth && death && <span> · </span>}
              {death && <span>Died {death}</span>}
            </div>
          )}
          {bio && <p className={styles.bio}>{bio}</p>}
        </div>
      </div>

      <div className={styles.credits}>
        {creditGroups.length === 0 ? (
          <div className={styles.empty}>No credits recorded yet.</div>
        ) : (
          creditGroups.map(group => (
            <section key={group.role} className={styles.creditGroup}>
              <h2 className={styles.creditGroupTitle}>{group.role}</h2>
              <div className={styles.creditGrid}>
                {group.items.map(credit => (
                  <Link key={credit.mediaItemId} to={`/media/${credit.mediaItemId}`} className={styles.creditCard}>
                    <PosterImage posterUrl={credit.posterUrl} name={credit.name} lazy />
                    <div className={styles.creditInfo}>
                      <div className={styles.creditName}>{credit.name}</div>
                      {credit.characterName && <div className={styles.creditCharacter}>{credit.characterName}</div>}
                      {credit.year && <div className={styles.creditYear}>{credit.year}</div>}
                    </div>
                  </Link>
                ))}
              </div>
            </section>
          ))
        )}
      </div>
    </div>
  )
}
