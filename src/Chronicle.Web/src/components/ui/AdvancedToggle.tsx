import { useState, type ReactNode } from 'react'
import styles from './AdvancedToggle.module.css'

interface AdvancedToggleProps {
  /** Text label shown next to the arrow indicator. */
  label: string
  children: ReactNode
  /** Whether the panel starts open. Defaults to false. */
  defaultOpen?: boolean
}

/**
 * A disclosure widget that hides advanced/optional content behind a
 * clickable toggle button. Manages its own open/closed state.
 *
 * Usage:
 *   <AdvancedToggle label="Advanced: Custom account">
 *     <p>Hidden content here</p>
 *   </AdvancedToggle>
 */
export default function AdvancedToggle({
  label,
  children,
  defaultOpen = false,
}: AdvancedToggleProps) {
  const [open, setOpen] = useState(defaultOpen)

  return (
    <div className={styles.wrapper}>
      <button
        type="button"
        className={styles.toggle}
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
      >
        {open ? '▼' : '▶'} {label}
      </button>

      {open && (
        <div className={styles.panel} role="region">
          {children}
        </div>
      )}
    </div>
  )
}
