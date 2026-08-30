import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getPeople, getPersonRoles } from '@/api/people'
import { PersonCard } from '@/components/people/PersonCard'
import styles from './PeopleLibraryPage.module.css'

type SortOption = 'name' | 'birthDate' | 'createdAt'
type DeceasedFilter = 'either' | 'living' | 'deceased'

const PER_PAGE = 60

/** Catalog-wide People grid -- every person credited on something in Chronicle, or added
 * directly, regardless of any single user's library (docs/plans/2026-08-28-people-section-
 * design.md Section 5). Not a fork of LibraryPage: no watch-status concept applies to a
 * person, so this mirrors that page's general shape only. */
export default function PeopleLibraryPage() {
  const [sort, setSort] = useState<SortOption>('name')
  const [activeRoles, setActiveRoles] = useState<Set<string>>(new Set())
  const [deceased, setDeceased] = useState<DeceasedFilter>('either')
  // "Load More" grows the window size rather than paging through separate pages that would
  // need merging client-side -- always re-fetches page 1 with a larger perPage, so the result
  // is naturally the full cumulative list with no accumulation bookkeeping of its own.
  const [visibleCount, setVisibleCount] = useState(PER_PAGE)

  const { data: roles = [] } = useQuery({ queryKey: ['person-roles'], queryFn: getPersonRoles })

  // Only a single role is sent server-side today (the API filters on one Role value) --
  // multiple active chips are OR'd together client-side against each page already fetched
  // by re-querying per selected role would multiply round-trips for a filter that in practice
  // is almost always used with 0 or 1 selections; this keeps the common case a single request.
  const primaryRole = activeRoles.size > 0 ? [...activeRoles][0] : undefined

  const { data, isLoading } = useQuery({
    queryKey: ['people', sort, primaryRole, deceased, visibleCount],
    queryFn: () => getPeople({
      sort,
      role: primaryRole,
      deceased: deceased === 'either' ? undefined : deceased === 'deceased',
      page: 1,
      perPage: visibleCount,
    }),
  })

  const people = data?.items ?? []
  const total = data?.total ?? null
  const hasMore = total != null ? people.length < total : people.length === visibleCount

  function toggleRole(role: string) {
    setVisibleCount(PER_PAGE)
    setActiveRoles(prev => {
      const next = new Set(prev)
      if (next.has(role)) next.delete(role)
      else { next.clear(); next.add(role) } // single-select for now -- see primaryRole's own note
      return next
    })
  }

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1>People</h1>
        <div className={styles.controls}>
          <select
            className={styles.select}
            value={sort}
            onChange={e => { setSort(e.target.value as SortOption); setVisibleCount(PER_PAGE) }}
          >
            <option value="name">Name A–Z</option>
            <option value="birthDate">Birth Date</option>
            <option value="createdAt">Recently Added</option>
          </select>
          <select
            className={styles.select}
            value={deceased}
            onChange={e => { setDeceased(e.target.value as DeceasedFilter); setVisibleCount(PER_PAGE) }}
          >
            <option value="either">Living or Deceased</option>
            <option value="living">Living</option>
            <option value="deceased">Deceased</option>
          </select>
        </div>
      </div>

      {roles.length > 0 && (
        <div className={styles.roleChips}>
          {roles.map(role => (
            <button
              key={role}
              type="button"
              className={activeRoles.has(role) ? styles.roleChipActive : styles.roleChip}
              onClick={() => toggleRole(role)}
            >
              {role}
            </button>
          ))}
        </div>
      )}

      {isLoading && people.length === 0 ? (
        <div className={styles.empty}>Loading…</div>
      ) : people.length === 0 ? (
        <div className={styles.empty}>No people found.</div>
      ) : (
        <>
          <div className={styles.grid}>
            {people.map(person => <PersonCard key={person.id} person={person} />)}
          </div>
          {hasMore && (
            <div className={styles.loadMoreRow}>
              <button type="button" className={styles.loadMoreButton} onClick={() => setVisibleCount(c => c + PER_PAGE)}>
                Load More
              </button>
            </div>
          )}
        </>
      )}
    </div>
  )
}
