import { useEffect } from 'react'
import { Link, useParams, useNavigate, useLocation } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getMedia, setMediaOverride, clearMediaOverride } from '@/api/media'
import { getPersonCredits, getPersonHeadshots } from '@/api/people'
import { PosterImage } from '@/components/PosterImage'
import styles from './PersonDetailPage.module.css'

function formatDate(iso: string | null | undefined): string | null {
  if (!iso) return null
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return null
  // A birth/death date is a calendar date with no meaningful time-of-day -- it's stored as
  // midnight UTC, so formatting in the viewer's LOCAL timezone can roll it back a day for
  // anyone west of UTC (confirmed live: "1973-04-03T00:00:00Z" displayed as "April 2" for a
  // UTC-6 viewer). Pin the format to UTC so the calendar date shown always matches what's stored.
  return d.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric', timeZone: 'UTC' })
}

/** Person detail page -- reuses the generic GET /media/:id endpoint for the person's own
 * data (a person is a MediaItem, no separate detail endpoint needed) plus a dedicated
 * role-grouped credits section. Deliberately a separate component from MediaDetailPage, not
 * a variant of it: no change-type/merge/library-status/children-grid sections apply to a
 * person (docs/plans/2026-08-28-people-section-design.md Section 6). */
export default function PersonDetailPage() {
  const { id } = useParams<{ id: string }>()
  const personId = Number(id)
  const qc = useQueryClient()
  const navigate = useNavigate()
  const location = useLocation()

  // "↑ People" + Prev/Next nav -- per-user request (2026-08-30): "movie details have an up
  // library button... I need that same kind of thing for people. also next and previous
  // buttons." Same listIds/sessionStorage pattern as MediaDetailPage's own list nav: state
  // arrives via router state when navigating from a card (PersonCard's navState prop), and
  // falls back to sessionStorage so the Prev/Next links (which themselves only carry state,
  // no fresh navigation from a list) and a hard refresh don't lose it.
  const navState = (location.state as { listIds?: number[]; listLabel?: string } | null) ?? null

  useEffect(() => {
    if (navState?.listIds?.length) {
      try {
        sessionStorage.setItem(`chronicle.listNav.person.${personId}`, JSON.stringify(navState))
      } catch {
        // Quota exceeded — silently skip, same tradeoff as MediaDetailPage's own guard.
      }
    }
  }, [personId, navState])

  const effectiveNavState = (() => {
    if (navState?.listIds?.length) return navState
    try {
      const stored = sessionStorage.getItem(`chronicle.listNav.person.${personId}`)
      return stored ? JSON.parse(stored) as { listIds: number[]; listLabel?: string } : null
    } catch { return null }
  })()

  const listIds = effectiveNavState?.listIds ?? []
  const currentIndex = listIds.indexOf(personId)
  const prevId = currentIndex > 0 ? listIds[currentIndex - 1] : null
  const nextId = currentIndex < listIds.length - 1 ? listIds[currentIndex + 1] : null

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

  // Every photo Chronicle has ever accumulated for this person (person_headshots), not just
  // whichever one is currently resolved onto PosterUrl -- lets the user actually see and pick
  // among alternates instead of being stuck with whatever was discovered first.
  const { data: headshots = [] } = useQuery({
    queryKey: ['person-headshots', personId],
    queryFn: () => getPersonHeadshots(personId),
    enabled: !Number.isNaN(personId),
  })

  const pinMut = useMutation({
    mutationFn: (url: string) => setMediaOverride(personId, 'poster_url', url),
    onSuccess: updated => {
      qc.setQueryData(['media', personId], updated)
      qc.invalidateQueries({ queryKey: ['person-headshots', personId] })
    },
  })

  const resetMut = useMutation({
    mutationFn: () => clearMediaOverride(personId, 'poster_url'),
    onSuccess: updated => {
      qc.setQueryData(['media', personId], updated)
      qc.invalidateQueries({ queryKey: ['person-headshots', personId] })
    },
  })

  if (isLoading || !person) {
    return <div className={styles.loading}>Loading…</div>
  }

  const bio = person.resolvedMetadata?.overview ?? person.overview
  const birth = formatDate(person.birthDate)
  const death = formatDate(person.deathDate)

  return (
    <div className={styles.page}>
      <div className={styles.topNav}>
        <button className={styles.backBtn} onClick={() => navigate(-1)}>← Back</button>
        {/* Jumps PeopleLibraryPage straight back to this person's spot in the (Name A-Z)
            list via its existing jumpTarget mechanism (the same one the A-Z rail and jump
            search box use) -- a bare `to="/people"` was landing at the top of the list every
            time, losing whatever section/scroll position the user came from. Only takes
            effect there when sort=name (jumpTo is otherwise ignored server-side, same as
            every other jump entry point in that page); for other sorts this no-ops rather
            than erroring. */}
        <Link to="/people" state={{ jumpTo: person.name }} className={styles.upBtn}>↑ People</Link>
        {listIds.length > 0 && (
          <div className={styles.listNav}>
            {prevId != null ? (
              <Link to={`/people/${prevId}`} state={effectiveNavState} className={styles.navBtn}>‹ Prev</Link>
            ) : (
              <span className={`${styles.navBtn} ${styles.navBtnDisabled}`}>‹ Prev</span>
            )}
            <span className={styles.navPos}>
              {effectiveNavState?.listLabel && <span className={styles.navLabel}>{effectiveNavState.listLabel} · </span>}
              {currentIndex + 1} / {listIds.length}
            </span>
            {nextId != null ? (
              <Link to={`/people/${nextId}`} state={effectiveNavState} className={styles.navBtn}>Next ›</Link>
            ) : (
              <span className={`${styles.navBtn} ${styles.navBtnDisabled}`}>Next ›</span>
            )}
          </div>
        )}
      </div>

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

      {headshots.length > 1 && (
        <section className={styles.photos}>
          <div className={styles.photosHeader}>
            <h2 className={styles.creditGroupTitle}>Photos</h2>
            {headshots.some(h => !h.isCurrent) && (
              <button
                type="button"
                className={styles.resetPhotoBtn}
                onClick={() => resetMut.mutate()}
                disabled={resetMut.isPending}
              >
                Reset to newest
              </button>
            )}
          </div>
          <div className={styles.photoGrid}>
            {headshots.map(h => (
              <button
                key={h.id}
                type="button"
                className={`${styles.photoThumb} ${h.isCurrent ? styles.photoThumbCurrent : ''}`}
                title={h.source}
                disabled={h.isCurrent || pinMut.isPending}
                onClick={() => pinMut.mutate(h.url)}
              >
                <PosterImage posterUrl={h.thumbnailUrl ?? h.url} name={person.name} lazy />
                {h.isCurrent && <span className={styles.photoCurrentBadge}>Current</span>}
              </button>
            ))}
          </div>
        </section>
      )}

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
