import { useEffect, useRef, useState } from 'react'
import styles from './AlphabetScrollIndicator.module.css'

interface AlphabetScrollIndicatorProps {
  /**
   * Selector (scoped to `document`) matching every element carrying a `data-letter`
   * attribute — one per alphabetically-grouped item. Within a group the elements must appear in the DOM
   * in the same order the list is sorted in, so document order doubles as scroll order.
   */
  selector: string
  /**
   * Selector for the groups that are each sorted on their own (the library's sections). The letter shown is the one
   * of the item at the reference line within the group that holds that line, so each section has its own alphabet.
   * Without it the whole page is taken as one list.
   */
  groupSelector?: string
  /** Only meaningful while the list is actually sorted alphabetically by name. */
  enabled: boolean
}

// How long the badge stays visible after scrolling stops, in ms — long enough to read,
// short enough to get out of the way once you've found your spot (matches Kodi/iOS feel).
const HIDE_DELAY_MS = 900
// Vertical reference line, in px from the viewport top, used to decide which item
// counts as "current". Below the page header so it tracks what's actually visible.
const REFERENCE_LINE_PX = 140

/** The last node whose top is at or above the reference line (the first node when none is), by binary search over tops that never decrease. */
function currentNode(nodes: ArrayLike<HTMLElement>): HTMLElement {
  let lo = 0
  let hi = nodes.length - 1
  let current = 0
  while (lo <= hi) {
    const mid = (lo + hi) >> 1
    if (nodes[mid].getBoundingClientRect().top > REFERENCE_LINE_PX) hi = mid - 1
    else { current = mid; lo = mid + 1 }
  }
  return nodes[current]
}

export function AlphabetScrollIndicator({ selector, groupSelector, enabled }: AlphabetScrollIndicatorProps) {
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
      let scope: ParentNode = document
      if (groupSelector) {
        // the group the reference line is in: the last one that starts at or above it (groups appear top to bottom)
        const groups = Array.from(document.querySelectorAll<HTMLElement>(groupSelector))
        let holder: HTMLElement | null = null
        for (const g of groups) {
          if (g.getBoundingClientRect().top <= REFERENCE_LINE_PX) holder = g
          else break
        }
        if (!holder) holder = groups[0] ?? null
        if (!holder) return null
        scope = holder
      }
      const nodes = scope.querySelectorAll<HTMLElement>(selector)
      if (nodes.length === 0) return null                     // a collapsed or empty group has no letter to show
      return currentNode(nodes).dataset.letter ?? null
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

    // Capture phase: a scroll event does not bubble, and Chronicle's pages scroll inside their own content
    // container rather than the window, so a plain window listener only ever heard the rare window scroll.
    window.addEventListener('scroll', onScroll, { passive: true, capture: true })
    return () => {
      window.removeEventListener('scroll', onScroll, { capture: true })
      if (hideTimer.current) clearTimeout(hideTimer.current)
    }
  }, [selector, groupSelector, enabled])

  if (!enabled || letter === null) return null

  return (
    <div className={`${styles.badge} ${visible ? styles.visible : ''}`} aria-hidden="true">
      {letter}
    </div>
  )
}
