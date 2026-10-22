import { useEffect, useRef, useState } from 'react'
import styles from './AlphabetScrollIndicator.module.css'

interface AlphabetScrollIndicatorProps {
  /**
   * Selector (scoped to `document`) matching every element carrying a `data-letter`
   * attribute — one per alphabetically-grouped item. Elements must appear in the DOM
   * in the same order the list is sorted in, so document order doubles as scroll order.
   */
  selector: string
  /** Only meaningful while the list is actually sorted alphabetically by name. */
  enabled: boolean
}

// How long the badge stays visible after scrolling stops, in ms — long enough to read,
// short enough to get out of the way once you've found your spot (matches Kodi/iOS feel).
const HIDE_DELAY_MS = 900
// Vertical reference line, in px from the viewport top, used to decide which item
// counts as "current". Below the page header so it tracks what's actually visible.
const REFERENCE_LINE_PX = 140

export function AlphabetScrollIndicator({ selector, enabled }: AlphabetScrollIndicatorProps) {
  const [letter, setLetter] = useState<string | null>(null)
  const [visible, setVisible] = useState(false)
  const hideTimer = useRef<ReturnType<typeof setTimeout>>()
  const ticking = useRef(false)

  useEffect(() => {
    if (!enabled) {
      setVisible(false)
      return
    }

    function currentLetter(): string | null {
      const nodes = document.querySelectorAll<HTMLElement>(selector)
      if (nodes.length === 0) return null
      // Nodes are in document order, which is also top-to-bottom order, so their tops never decrease down the list: the "current"
      // one is the last whose top is at or above the reference line, found by binary search. A scan from the first node read the
      // layout of thousands of cards on every scroll frame once a long section was open, which is what made scrolling choppy.
      let lo = 0
      let hi = nodes.length - 1
      let current = 0
      while (lo <= hi) {
        const mid = (lo + hi) >> 1
        if (nodes[mid].getBoundingClientRect().top > REFERENCE_LINE_PX) hi = mid - 1
        else { current = mid; lo = mid + 1 }
      }
      return nodes[current].dataset.letter ?? null
    }

    function onScroll() {
      if (ticking.current) return
      ticking.current = true
      requestAnimationFrame(() => {
        const next = currentLetter()
        if (next) {
          setLetter(next)
          setVisible(true)
          if (hideTimer.current) clearTimeout(hideTimer.current)
          hideTimer.current = setTimeout(() => setVisible(false), HIDE_DELAY_MS)
        }
        ticking.current = false
      })
    }

    window.addEventListener('scroll', onScroll, { passive: true })
    return () => {
      window.removeEventListener('scroll', onScroll)
      if (hideTimer.current) clearTimeout(hideTimer.current)
    }
  }, [selector, enabled])

  if (!enabled || letter === null) return null

  return (
    <div className={`${styles.badge} ${visible ? styles.visible : ''}`} aria-hidden="true">
      {letter}
    </div>
  )
}
