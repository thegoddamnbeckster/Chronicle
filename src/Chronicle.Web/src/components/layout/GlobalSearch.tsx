import { useRef, useState, useEffect, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import { searchMedia } from '@/api/media'
import type { MediaItem } from '@/types'
import styles from './GlobalSearch.module.css'
import { PosterImage } from '@/components/PosterImage'

export default function GlobalSearch() {
  const [query, setQuery]       = useState('')
  const [results, setResults]   = useState<MediaItem[]>([])
  const [open, setOpen]         = useState(false)
  const [loading, setLoading]   = useState(false)
  const [focused, setFocused]   = useState(false)
  const timerRef  = useRef<ReturnType<typeof setTimeout> | null>(null)
  const wrapperRef = useRef<HTMLDivElement>(null)
  const inputRef  = useRef<HTMLInputElement>(null)
  const navigate  = useNavigate()

  const runSearch = useCallback(async (q: string) => {
    if (!q.trim()) { setResults([]); setOpen(false); return }
    setLoading(true)
    try {
      const hits = await searchMedia(q, undefined, 1, true)
      setResults(hits)
      setOpen(hits.length > 0)
    } catch {
      setResults([])
    } finally {
      setLoading(false)
    }
  }, [])

  function handleChange(e: React.ChangeEvent<HTMLInputElement>) {
    const q = e.target.value
    setQuery(q)
    if (timerRef.current) clearTimeout(timerRef.current)
    if (!q.trim()) { setResults([]); setOpen(false); return }
    timerRef.current = setTimeout(() => runSearch(q), 300)
  }

  function handleClear() {
    setQuery('')
    setResults([])
    setOpen(false)
    inputRef.current?.focus()
  }

  function handleSelect(item: MediaItem) {
    setOpen(false)
    // People are a reference type (see MediaType.IsTrackable) with their own detail page --
    // routing them through /media/:id would land on the generic MediaDetailPage, complete with
    // library-status controls and a "Missing" badge that make no sense for a person.
    navigate(item.mediaTypeName === 'People' ? `/people/${item.id}` : `/media/${item.id}`)
  }

  function handleFocus() {
    setFocused(true)
    if (results.length > 0) setOpen(true)
  }

  function handleBlur() {
    setFocused(false)
  }

  // Close dropdown when clicking outside the wrapper
  useEffect(() => {
    function onPointerDown(e: PointerEvent) {
      if (wrapperRef.current && !wrapperRef.current.contains(e.target as Node)) {
        setOpen(false)
      }
    }
    document.addEventListener('pointerdown', onPointerDown)
    return () => document.removeEventListener('pointerdown', onPointerDown)
  }, [])

  return (
    <div ref={wrapperRef} className={styles.wrapper}>
      <div className={`${styles.inputWrap} ${focused ? styles.inputWrapFocused : ''}`}>
        <span className={styles.searchIcon}>⌕</span>
        <input
          ref={inputRef}
          className={styles.input}
          type="text"
          placeholder="Search media…"
          value={query}
          onChange={handleChange}
          onFocus={handleFocus}
          onBlur={handleBlur}
          onKeyDown={e => {
            if (e.key === 'Escape') { setOpen(false); inputRef.current?.blur() }
            if (e.key === 'Enter' && results.length > 0) handleSelect(results[0])
          }}
          aria-label="Search all media"
          autoComplete="off"
        />
        {loading && <span className={styles.spinner} />}
        {query && !loading && (
          <button className={styles.clearBtn} onClick={handleClear} tabIndex={-1} aria-label="Clear search">✕</button>
        )}
      </div>

      {open && results.length > 0 && (
        <ul className={styles.dropdown} role="listbox">
          {results.slice(0, 10).map(item => (
            <li
              key={item.id}
              className={styles.result}
              role="option"
              // onClick, not onPointerDown+preventDefault -- that combination is a classic
              // mobile-scroll killer: preventDefault() on pointerdown tells the browser not
              // to start a touch-scroll gesture from this element at all, and every result
              // row had it, so no row in the (max-height: 400px, overflow-y: auto) dropdown
              // could be touch-scrolled past. Confirmed live report (2026-08-29): "still have
              // that bug in mobile with being unable to scroll search results." Blur on the
              // input (which tapping a result triggers) only affects wrapper focus styling
              // here, not `open`/`results`, so there's nothing the pointerdown-first ordering
              // was actually protecting -- onClick fires after the browser's own tap/scroll
              // gesture is resolved, which is exactly what a normal list-item click needs.
              onClick={() => handleSelect(item)}
            >
              <div className={styles.thumb}>
                <PosterImage posterUrl={item.posterUrl} name={item.name} imgClassName={styles.poster} />
              </div>
              <div className={styles.info}>
                <span className={styles.name}>{item.name}</span>
                <span className={styles.meta}>
                  {item.year && <span>{item.year}</span>}
                  <span className={styles.type}>{item.mediaTypeName}</span>
                </span>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
