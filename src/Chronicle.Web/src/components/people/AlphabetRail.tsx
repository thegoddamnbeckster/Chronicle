import { useCallback, useRef } from 'react'
import styles from './AlphabetRail.module.css'

export const RAIL_LETTERS = ['#', ...Array.from({ length: 26 }, (_, i) => String.fromCharCode(65 + i))]

interface AlphabetRailProps {
  activeLetter: string | null
  onJump: (letter: string) => void
}

/** Vertical A-Z scrubber, iOS Contacts style -- tap a letter to jump straight there, or
 * press and drag up/down the rail to scrub through letters continuously. Per-user request
 * (2026-08-30): "let me jump into a mid point of the list of people as I need to... let my
 * phone scroll through the people as I want to scroll." "#" covers names that don't start
 * with a letter (Chronicle's people catalog has plenty: "!DelaDap", "$NOT", "1 in Five") --
 * backed by the same jump-position resolution every other jump target uses
 * (PeopleController.GetJumpPosition), just sent as a single non-letter character; ASCII
 * punctuation/digits all sort before 'A' so ToUpper().CompareTo() on the server still orders
 * it correctly against real names. */
export function AlphabetRail({ activeLetter, onJump }: AlphabetRailProps) {
  const railRef = useRef<HTMLDivElement>(null)
  const isPressedRef = useRef(false)
  const lastLetterRef = useRef<string | null>(null)

  const letterAtPoint = useCallback((clientY: number): string | null => {
    const rail = railRef.current
    if (!rail) return null
    const rect = rail.getBoundingClientRect()
    if (rect.height === 0) return null
    const relY = clientY - rect.top
    const index = Math.floor((relY / rect.height) * RAIL_LETTERS.length)
    const clamped = Math.max(0, Math.min(RAIL_LETTERS.length - 1, index))
    return RAIL_LETTERS[clamped]
  }, [])

  function jumpTo(clientY: number) {
    const letter = letterAtPoint(clientY)
    if (letter && letter !== lastLetterRef.current) {
      lastLetterRef.current = letter
      onJump(letter)
    }
  }

  function handlePointerDown(e: React.PointerEvent<HTMLDivElement>) {
    isPressedRef.current = true
    e.currentTarget.setPointerCapture(e.pointerId)
    jumpTo(e.clientY)
  }

  function handlePointerMove(e: React.PointerEvent<HTMLDivElement>) {
    if (!isPressedRef.current) return
    jumpTo(e.clientY)
  }

  function handlePointerUp(e: React.PointerEvent<HTMLDivElement>) {
    isPressedRef.current = false
    e.currentTarget.releasePointerCapture(e.pointerId)
  }

  return (
    <div
      ref={railRef}
      className={styles.rail}
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={handlePointerUp}
      onPointerCancel={handlePointerUp}
    >
      {RAIL_LETTERS.map(letter => (
        <div
          key={letter}
          className={letter === activeLetter ? styles.letterActive : styles.letter}
        >
          {letter}
        </div>
      ))}
    </div>
  )
}
