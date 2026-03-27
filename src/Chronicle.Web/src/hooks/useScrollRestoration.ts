import { useEffect, useLayoutEffect, useRef } from 'react'
import { useLocation } from 'react-router-dom'

/**
 * Module-level Map: survives StrictMode double-mount cycles.
 * Keys = React Router location keys, values = scrollTop px.
 */
const scrollPositions = new Map<string, number>()

/**
 * Saves and restores <main> scroll position across route navigation.
 * Call ONCE inside Layout (which never unmounts).
 *
 * Design:
 *  - A scroll event listener on <main> tracks the current position into a ref.
 *    This is immune to the DOM clamping that happens when shorter content renders.
 *  - useLayoutEffect fires synchronously after React commits new content.
 *    At that moment the ref still holds the OLD page's last scroll position.
 *    We save it to the Map keyed by the OLD location key, then restore for the new key.
 */
export function useScrollRestoration(mainRef: React.RefObject<HTMLElement | null>) {
  const { key } = useLocation()
  const keyRef = useRef(key)
  const scrollRef = useRef(0)

  // Disable browser native scroll restoration — we handle it ourselves
  useEffect(() => {
    if ('scrollRestoration' in history) history.scrollRestoration = 'manual'
  }, [])

  // Track scrollTop continuously via event listener (immune to content-change clamping)
  useEffect(() => {
    const main = mainRef.current
    if (!main) return
    const onScroll = () => { scrollRef.current = main.scrollTop }
    main.addEventListener('scroll', onScroll, { passive: true })
    return () => main.removeEventListener('scroll', onScroll)
  }, [mainRef])

  // On navigation: save outgoing position, restore incoming position
  useLayoutEffect(() => {
    const main = mainRef.current
    if (!main) return

    // Save outgoing page's scroll position (ref holds real value, unaffected by DOM clamp)
    if (keyRef.current !== key) {
      scrollPositions.set(keyRef.current, scrollRef.current)
      keyRef.current = key
    }

    const saved = scrollPositions.get(key) ?? 0

    if (saved === 0) {
      main.scrollTo(0, 0)
      return
    }

    // Returning page: restore after content renders (double-rAF)
    let id2 = -1
    const id = requestAnimationFrame(() => {
      id2 = requestAnimationFrame(() => main.scrollTo(0, saved))
    })
    return () => {
      cancelAnimationFrame(id)
      cancelAnimationFrame(id2)
    }
  }, [key, mainRef])
}
