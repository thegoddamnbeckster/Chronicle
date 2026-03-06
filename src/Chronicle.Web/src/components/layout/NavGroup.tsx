import { useState } from 'react'
import styles from './NavGroup.module.css'

interface NavGroupProps {
  label: string
  storageKey: string
  defaultOpen: boolean
  children: React.ReactNode
}

export default function NavGroup({ label, storageKey, defaultOpen, children }: NavGroupProps) {
  const [open, setOpen] = useState<boolean>(() => {
    const stored = localStorage.getItem(storageKey)
    return stored !== null ? stored === 'true' : defaultOpen
  })

  function toggle() {
    const next = !open
    setOpen(next)
    localStorage.setItem(storageKey, String(next))
  }

  return (
    <div className={styles.group}>
      <button className={styles.header} onClick={toggle} aria-expanded={open}>
        <svg
          className={`${styles.chevron} ${open ? styles.chevronOpen : ''}`}
          width="12"
          height="12"
          viewBox="0 0 12 12"
          fill="none"
          xmlns="http://www.w3.org/2000/svg"
          aria-hidden="true"
        >
          <path d="M2 4L6 8L10 4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        <span>{label}</span>
      </button>
      <div className={`${styles.children} ${open ? styles.childrenOpen : ''}`}>
        {children}
      </div>
    </div>
  )
}
