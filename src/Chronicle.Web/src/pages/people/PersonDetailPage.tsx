import { useState, useEffect, useMemo } from 'react'
import { Link, useParams, useNavigate, useLocation } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getMedia, getMediaTypes, refreshMedia, setMediaOverride, clearMediaOverride, deleteMedia, searchMedia } from '@/api/media'
import {
  getPersonCredits, getPersonAllCredits, getPersonHeadshots, hidePersonHeadshot, restorePersonHeadshots,
  getPersonNeighbors,
} from '@/api/people'
import { getLibrarySectionIds } from '@/api/library'
import { loadPrefs } from '@/utils/libraryPrefs'
import { loadSortSettings } from '@/utils/sortSettings'
import { PluginFold } from '@/components/PluginFold'
import { ItemPluginPanels } from '@/components/ItemPluginPanels'
import { LinkedAuthorSection } from '@/components/LinkedAuthorSection'
import { PosterImage } from '@/components/PosterImage'
import { useAuth } from '@/hooks/useAuth'
import MergeModal, { type MergeItem } from '@/components/MergeModal'
import styles from './PersonDetailPage.module.css'
// The backdrop look (image, scrim, light-on-dark colouring, boxed content) is the media page's own, reused so both sections match.
import mediaStyles from '@/pages/media/MediaDetailPage.module.css'
import { ArtTypeTag } from '@/components/ArtTypeTag'

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
 * a variant of it: no change-type/library-status/children-grid sections apply to a person
 * (docs/plans/2026-08-28-people-section-design.md Section 6).
 *
 * Merge/Remove DO apply here (per-user request, 2026-09-04, after a corrupted person record
 * had to be cleaned up by hand via raw SQL): MergeService and the generic DELETE /media/:id
 * endpoint are already type-agnostic, so both actions below reuse them exactly as
 * MediaDetailPage does, just scoped to the "people" media type for the merge search so an
 * admin can't accidentally merge a person into an unrelated movie/show. */
export default function PersonDetailPage() {
  const { id } = useParams<{ id: string }>()
  const personId = Number(id)
  const qc = useQueryClient()
  const navigate = useNavigate()
  const location = useLocation()
  const { user } = useAuth()
  const isAdmin = user?.isAdmin ?? false

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

  // A list this person is not in (e.g. the audiobook library, carried over when an author link opened this page)
  // is the author's library list, handled below -- it must not masquerade as the People list ("0 / 111").
  const effectiveNavState = (() => {
    if (navState?.listIds?.includes(personId)) return navState
    try {
      const stored = sessionStorage.getItem(`chronicle.listNav.person.${personId}`)
      const parsed = stored ? JSON.parse(stored) as { listIds: number[]; listLabel?: string } : null
      return parsed?.listIds?.includes(personId) ? parsed : null
    } catch { return null }
  })()

  const listIds = effectiveNavState?.listIds ?? []
  const currentIndex = listIds.indexOf(personId)
  const rememberedPrev = currentIndex > 0 ? listIds[currentIndex - 1] : null
  const rememberedNext = currentIndex >= 0 && currentIndex < listIds.length - 1 ? listIds[currentIndex + 1] : null

  // Not opened from the People list (a search, an author link, a bookmark): ask where this person sits in it instead,
  // so Prev/Next is always there, like the media page's own.
  const { data: neighbors } = useQuery({
    queryKey: ['person-neighbors', personId],
    queryFn: () => getPersonNeighbors(personId),
    enabled: !Number.isNaN(personId) && listIds.length === 0,
    staleTime: 60 * 1000,
  })
  const ownList = listIds.length === 0 && neighbors != null && neighbors.total > 1
  const prevId = ownList ? neighbors!.prevId : rememberedPrev
  const nextId = ownList ? neighbors!.nextId : rememberedNext
  const peopleNavShown = listIds.length > 0 || ownList
  const peoplePos = ownList ? `${neighbors!.index + 1} / ${neighbors!.total}` : `${currentIndex + 1} / ${listIds.length}`

  const { data: person, isLoading } = useQuery({
    queryKey: ['media', personId],
    queryFn: () => getMedia(personId),
    enabled: !Number.isNaN(personId),
  })


  // The linked author record(s): the same person in a book/audiobook/music library. This page is also theirs, so it
  // carries their library's Up button and Prev/Next, worked out like the media page does for a top-level item.
  const authors = (person?.linkedItems ?? []).filter(l => !l.isPerson)
  const primaryAuthor = authors[0]
  const { data: authorItem } = useQuery({
    queryKey: ['media', primaryAuthor?.itemId],
    queryFn: () => getMedia(primaryAuthor!.itemId),
    enabled: primaryAuthor != null,
  })
  const { data: mediaTypes = [] } = useQuery({ queryKey: ['media-types'], queryFn: getMediaTypes, staleTime: 5 * 60 * 1000 })
  const authorNavState = (() => {
    const all = authors.map(a => a.itemId)
    if (navState?.listIds?.some(i => all.includes(i))) return navState
    for (const id of all) {
      try {
        const stored = sessionStorage.getItem(`chronicle.listNav.${id}`)
        const parsed = stored ? JSON.parse(stored) as { listIds: number[]; listLabel?: string } : null
        if (parsed?.listIds?.includes(id)) return parsed
      } catch { /* unreadable: derive instead */ }
    }
    return null
  })()
  const libraryPrefs = useMemo(() => loadPrefs(), [])
  const librarySort = useMemo(() => loadSortSettings(), [])
  const articles = librarySort.ignoreArticles ? librarySort.ignoredArticles : undefined
  const needsOwnAuthorList = !authorNavState && authorItem != null && authorItem.parentId == null
  const { data: sectionIds = [] } = useQuery({
    queryKey: ['library', 'section-ids', authorItem?.mediaTypeId, libraryPrefs.statusFilter ?? null,
      libraryPrefs.groupMoviesIntoCollections, libraryPrefs.sortBy, libraryPrefs.sortDir, articles ?? null],
    queryFn: () => getLibrarySectionIds({
      mediaTypeId: authorItem!.mediaTypeId,
      status: libraryPrefs.statusFilter,
      includeMoviesInCollections: !libraryPrefs.groupMoviesIntoCollections,
      sortBy: libraryPrefs.sortBy,
      sortDir: libraryPrefs.sortDir,
      ignoreArticles: articles,
    }),
    enabled: needsOwnAuthorList,
    staleTime: 5 * 60 * 1000,
  })
  const libraryNav = authorNavState ?? (needsOwnAuthorList && sectionIds.length > 1
    ? { listIds: sectionIds, listLabel: mediaTypes.find(t => t.id === authorItem!.mediaTypeId)?.displayName ?? authorItem!.mediaTypeName }
    : null)
  const libraryListIds = libraryNav?.listIds ?? []
  const libraryAnchorId = authors.map(a => a.itemId).find(i => libraryListIds.includes(i)) ?? primaryAuthor?.itemId
  const libraryIndex = libraryAnchorId != null ? libraryListIds.indexOf(libraryAnchorId) : -1
  const libraryPrevId = libraryIndex > 0 ? libraryListIds[libraryIndex - 1] : null
  const libraryNextId = libraryIndex >= 0 && libraryIndex < libraryListIds.length - 1 ? libraryListIds[libraryIndex + 1] : null

  // "Show every credit" (default off): the whole filmography, including titles that are not in the
  // library. Only fetched once switched on -- it asks the metadata provider, which is slower than the
  // library-only list above.
  // The Preferences default decides every person page; the checkbox below is only a view-level override,
  // dropped on navigating to another person and whenever the default itself changes.
  const defaultShowAll = user?.showAllCredits ?? false
  const [showAllOverride, setShowAllOverride] = useState<boolean | null>(null)
  useEffect(() => { setShowAllOverride(null) }, [personId, defaultShowAll])
  const showAll = showAllOverride ?? defaultShowAll
  const { data: allCredits, isLoading: allCreditsLoading, isError: allCreditsError } = useQuery({
    queryKey: ['person-all-credits', personId],
    queryFn: () => getPersonAllCredits(personId),
    enabled: !Number.isNaN(personId) && showAll,
  })

  // The library-only list (the default view); not fetched while the full list is what is shown.
  const { data: creditGroups = [] } = useQuery({
    queryKey: ['person-credits', personId],
    queryFn: () => getPersonCredits(personId),
    enabled: !Number.isNaN(personId) && !showAll,
  })

  // Every photo Chronicle has ever accumulated for this person (person_headshots), not just
  // whichever one is currently resolved onto PosterUrl -- lets the user actually see and pick
  // among alternates instead of being stuck with whatever was discovered first.
  const { data: headshots = [] } = useQuery({
    queryKey: ['person-headshots', personId],
    queryFn: () => getPersonHeadshots(personId, true),
    enabled: !Number.isNaN(personId),
  })
  // Hidden photos stay out of the gallery; they are only fetched so the page can offer to bring them back.
  const photos = headshots.filter(h => !h.isHidden)
  const hiddenCount = headshots.length - photos.length

  const hideMut = useMutation({
    mutationFn: (headshotId: number) => hidePersonHeadshot(personId, headshotId),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['person-headshots', personId] }),
  })

  const restoreMut = useMutation({
    mutationFn: () => restorePersonHeadshots(personId),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['person-headshots', personId] }),
  })

  const refreshAllMut = useMutation({
    mutationFn: async () => {
      const own = await refreshMedia(personId)
      for (const l of own.linkedItems ?? []) if (!l.isPerson) await refreshMedia(l.itemId)
      return own
    },
    onSuccess: updated => {
      qc.setQueryData(['media', personId], updated)
      qc.invalidateQueries({ queryKey: ['media'] })
      qc.invalidateQueries({ queryKey: ['person-headshots', personId] })
    },
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

  // ── Remove ────────────────────────────────────────────────────────────────
  const [deleteConfirm, setDeleteConfirm] = useState(false)

  const deleteMut = useMutation({
    mutationFn: () => deleteMedia(personId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['people'] })
      navigate('/people')
    },
  })

  // ── Merge with… ──────────────────────────────────────────────────────────
  // Scoped to this person's own mediaTypeId (rather than an unfiltered search like
  // MediaDetailPage's) so the dropdown only ever offers other people to merge with.
  const [mergeSearchOpen, setMergeSearchOpen] = useState(false)
  const [mergeSearchQuery, setMergeSearchQuery] = useState('')
  const [mergeTarget, setMergeTarget] = useState<MergeItem | null>(null)

  const { data: mergeSearchResults = [] } = useQuery({
    queryKey: ['personMergeSearch', mergeSearchQuery, person?.mediaTypeId],
    queryFn: () => searchMedia(mergeSearchQuery, person!.mediaTypeId),
    enabled: mergeSearchQuery.trim().length >= 2 && person != null,
  })

  if (isLoading || !person) {
    return <div className={styles.loading}>Loading…</div>
  }

  // Same lookup as the media page: the resolved backdrop, else any plugin's. A person with none of their own shows their linked
  // record's (the same entity), so the two sections carry one background.
  const backdropOf = (m?: typeof person) => m?.resolvedMetadata?.backdropUrl
    ?? (m?.pluginMetadata
      ? (Object.values(m.pluginMetadata).map(x => (x as Record<string, unknown>)?.backdropUrl).find(u => typeof u === 'string' && u) as string | undefined)
      : undefined)
  const backdropUrl = backdropOf(person) ?? backdropOf(authorItem)
  const hasBackdrop = Boolean(backdropUrl)

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
        <div className={styles.upGroup}>
          <Link to="/people" state={{ jumpTo: person.name }} className={styles.upBtn}>↑ People</Link>
          {authors.map(a => (
            <Link key={a.itemId} to={`/library#media-${a.itemId}`} className={styles.upBtn}>↑ {a.mediaTypeName}</Link>
          ))}
        </div>
      </div>

      {(peopleNavShown || (libraryListIds.length > 1 && libraryIndex >= 0)) && (
        <div className={styles.navRows}>
          {peopleNavShown && (
          <div className={styles.listNav}>
            {prevId != null ? (
              <Link to={`/people/${prevId}`} state={effectiveNavState} className={styles.navBtn}>‹ Prev</Link>
            ) : (
              <span className={`${styles.navBtn} ${styles.navBtnDisabled}`}>‹ Prev</span>
            )}
            <span className={styles.navPos}>
              <span className={styles.navLabel}>{(listIds.length > 0 && effectiveNavState?.listLabel) || 'People'} · </span>
              {peoplePos}
            </span>
            {nextId != null ? (
              <Link to={`/people/${nextId}`} state={effectiveNavState} className={styles.navBtn}>Next ›</Link>
            ) : (
              <span className={`${styles.navBtn} ${styles.navBtnDisabled}`}>Next ›</span>
            )}
          </div>
        )}
          {libraryListIds.length > 1 && libraryIndex >= 0 && (
        <div className={styles.listNav}>
          {libraryPrevId != null ? (
            <Link to={`/media/${libraryPrevId}`} state={libraryNav} className={styles.navBtn}>‹ Prev</Link>
          ) : (
            <span className={`${styles.navBtn} ${styles.navBtnDisabled}`}>‹ Prev</span>
          )}
          <span className={styles.navPos}>
            {libraryNav?.listLabel && <span className={styles.navLabel}>{libraryNav.listLabel} · </span>}
            {libraryIndex + 1} / {libraryListIds.length}
          </span>
          {libraryNextId != null ? (
            <Link to={`/media/${libraryNextId}`} state={libraryNav} className={styles.navBtn}>Next ›</Link>
          ) : (
            <span className={`${styles.navBtn} ${styles.navBtnDisabled}`}>Next ›</span>
          )}
        </div>
          )}
        </div>
      )}

      <section className={styles.recordSection} aria-label="People record">
        <div className={styles.recordHeader}>
          <h2 className={styles.recordTitle}>People</h2>
        </div>
      <div className={`${mediaStyles.backdropSection}${hasBackdrop ? ` ${mediaStyles.backdropActive}` : ''} ${mediaStyles.backdropEmbedded}`}>
        {hasBackdrop && <div className={mediaStyles.backdropImg} style={{ backgroundImage: `url("${backdropUrl}")` }} aria-hidden />}
        {hasBackdrop && <ArtTypeTag label="Backdrop" />}
        <div className={`${mediaStyles.backdropContent}${hasBackdrop ? ` ${mediaStyles.backdropContentActive}` : ''}`}>
      <div className={styles.hero}>
        <div className={styles.posterWrap}>
          <PosterImage posterUrl={person.posterUrl} name={person.name} />
        </div>
        <div className={`${styles.heroInfo}${hasBackdrop ? ` ${mediaStyles.metaBoxed}` : ''}`}>
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

          <div className={styles.actionsRow}>
            {/* Same toolbar and button style as Merge/Remove; the pressed state shows which view is on. */}
            <button
              type="button"
              className={`${styles.actionBtn} ${showAll ? styles.actionBtnOn : ''}`}
              onClick={() => setShowAllOverride(!showAll)}
              aria-pressed={showAll}
              title="Show every credit this person has ever had, including titles that are not in the library"
            >
              {showAll ? 'Showing all credits' : 'Show all credits'}
            </button>
            {isAdmin && (
              <>
              <button
                className={styles.actionBtn}
                onClick={() => setMergeSearchOpen(o => !o)}
              >
                {mergeSearchOpen ? 'Cancel Merge' : 'Merge with…'}
              </button>
              {!deleteConfirm ? (
                <button className={styles.deleteBtn} onClick={() => setDeleteConfirm(true)}>
                  Remove
                </button>
              ) : (
                <div className={styles.deleteConfirmStrip}>
                  <span className={styles.deleteConfirmText}>
                    Remove <strong>{person.name}</strong>? This cannot be undone.
                  </span>
                  <button className={styles.deleteConfirmCancel} onClick={() => setDeleteConfirm(false)}>
                    Cancel
                  </button>
                  <button
                    className={styles.deleteConfirmOk}
                    onClick={() => deleteMut.mutate()}
                    disabled={deleteMut.isPending}
                  >
                    {deleteMut.isPending ? 'Removing…' : 'Remove'}
                  </button>
                </div>
              )}
              </>
            )}
          </div>

          {mergeSearchOpen && (
            <div className={styles.mergeSearch}>
              <input
                className={styles.mergeSearchInput}
                type="text"
                placeholder="Search for a person to merge with…"
                value={mergeSearchQuery}
                onChange={e => setMergeSearchQuery(e.target.value)}
                autoFocus
              />
              {mergeSearchResults.length > 0 && (
                <div className={styles.mergeSearchResults}>
                  {mergeSearchResults.filter(r => r.id !== personId).slice(0, 8).map(result => (
                    <button
                      key={result.id}
                      className={styles.mergeSearchResult}
                      onClick={() => {
                        setMergeTarget({
                          id: result.id,
                          name: result.name,
                          posterUrl: result.posterUrl,
                          mediaTypeName: result.mediaTypeName,
                          year: result.year,
                          overview: result.overview,
                          externalIds: result.externalIds,
                        })
                        setMergeSearchOpen(false)
                        setMergeSearchQuery('')
                      }}
                    >
                      {result.posterUrl && (
                        <img src={result.posterUrl} alt="" className={styles.mergeResultPoster} />
                      )}
                      <span className={styles.mergeResultText}>
                        <span className={styles.mergeResultName}>{result.name}</span>
                      </span>
                    </button>
                  ))}
                </div>
              )}
            </div>
          )}

          {(photos.length > 1 || hiddenCount > 0) && (
            <section className={styles.photos}>
              <div className={styles.photosHeader}>
                <h2 className={styles.creditGroupTitle}>Photos</h2>
                <div className={styles.photosActions}>
                  {isAdmin && hiddenCount > 0 && (
                    <button
                      type="button"
                      className={styles.resetPhotoBtn}
                      onClick={() => restoreMut.mutate()}
                      disabled={restoreMut.isPending}
                    >
                      Restore hidden ({hiddenCount})
                    </button>
                  )}
                  {photos.some(h => !h.isCurrent) && (
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
              </div>
              <div className={styles.photoGrid}>
                {photos.map(h => (
                  <div key={h.id} className={styles.photoCell}>
                    <button
                      type="button"
                      className={`${styles.photoThumb} ${h.isCurrent ? styles.photoThumbCurrent : ''}`}
                      title={h.source}
                      disabled={h.isCurrent || pinMut.isPending}
                      onClick={() => pinMut.mutate(h.url)}
                    >
                      <PosterImage posterUrl={h.thumbnailUrl ?? h.url} name={person.name} lazy />
                      {h.isCurrent && <span className={styles.photoCurrentBadge}>Current</span>}
                    </button>
                    {isAdmin && !h.isCurrent && (
                      <button
                        type="button"
                        className={styles.photoHideBtn}
                        aria-label={`Hide this photo of ${person.name}`}
                        title="Hide this photo (it is not this person)"
                        disabled={hideMut.isPending}
                        onClick={() => hideMut.mutate(h.id)}
                      >
                        ✕
                      </button>
                    )}
                  </div>
                ))}
              </div>
            </section>
          )}

          <div className={styles.credits}>
            {showAll && allCreditsLoading ? (
              <div className={styles.empty}>Loading every credit…</div>
            ) : showAll && allCreditsError ? (
              <div className={styles.empty}>Could not load the full credit list.</div>
            ) : showAll && allCredits ? (
              allCredits.groups.length === 0 ? (
                <div className={styles.empty}>No credits found.</div>
              ) : (
                <>
                {allCredits.incomplete && (
                  <div className={styles.incompleteWarning} role="alert">
                    Couldn&apos;t reach the metadata provider, so this is only the credits already in your library.
                    Try again later.
                  </div>
                )}
                {allCredits.groups.map(group => (
                  <PluginFold key={group.role} foldKey={`person.${personId}.credits.${group.role}`} label={`${group.role} (${group.items.length})`} defaultOpen>
                    <div className={styles.creditGrid}>
                      {group.items.map((credit, i) => {
                        const body = (
                          <>
                            <PosterImage posterUrl={credit.posterUrl} name={credit.name} lazy />
                            <div className={styles.creditInfo}>
                              <div className={styles.creditName}>{credit.name}</div>
                              {credit.characterName && <div className={styles.creditCharacter}>{credit.characterName}</div>}
                              {credit.year && <div className={styles.creditYear}>{credit.year}</div>}
                              {credit.mediaItemId == null && <div className={styles.notInLibrary}>Not in library</div>}
                            </div>
                          </>
                        )
                        return credit.mediaItemId != null ? (
                          <Link key={`${credit.mediaItemId}-${i}`} to={`/media/${credit.mediaItemId}`} className={styles.creditCard}>
                            {body}
                          </Link>
                        ) : (
                          <div key={`ext-${credit.name}-${credit.year}-${i}`} className={`${styles.creditCard} ${styles.creditCardStatic}`}>
                            {body}
                          </div>
                        )
                      })}
                    </div>
                  </PluginFold>
                ))}
                </>
              )
            ) : creditGroups.length === 0 ? (
              <div className={styles.empty}>No credits recorded yet.</div>
            ) : (
              creditGroups.map(group => (
                <PluginFold key={group.role} foldKey={`person.${personId}.credits.${group.role}`} label={`${group.role} (${group.items.length})`} defaultOpen>
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
                </PluginFold>
              ))
            )}
          </div>

          {/* Refresh All covers the person AND every linked author record: they are one entity here. */}
          <div className={styles.refreshStrip}>
            <button className={styles.refreshBtn} onClick={() => refreshAllMut.mutate()} disabled={refreshAllMut.isPending}>
              {refreshAllMut.isPending ? 'Refreshing all…' : '↻ Refresh All'}
            </button>
            {refreshAllMut.isError && <span role="alert" className={styles.empty}>Refresh failed.</span>}
          </div>
          <ItemPluginPanels item={person} />
        </div>
      </div>
        </div>
      </div>
      </section>


      {(person.linkedItems ?? []).filter(l => !l.isPerson).map(author => (
        <LinkedAuthorSection key={author.itemId} author={author} />
      ))}

      {mergeTarget && (
        <MergeModal
          itemA={{
            id: person.id,
            name: person.name,
            posterUrl: person.posterUrl,
            mediaTypeName: person.mediaTypeName,
            year: person.year,
            overview: person.overview,
            externalIds: person.externalIds,
          }}
          itemB={mergeTarget}
          onClose={() => setMergeTarget(null)}
          onMerged={(winnerId) => {
            setMergeTarget(null)
            qc.invalidateQueries({ queryKey: ['media', String(winnerId)] })
            navigate(`/people/${winnerId}`, { replace: true })
          }}
        />
      )}
    </div>
  )
}
