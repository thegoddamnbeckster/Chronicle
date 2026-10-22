import React, { useState, useEffect } from 'react'
import { getMyPreferences, updateMyPreferences } from '@/api/users'
// Shares the detail page's stylesheet on purpose: the fold's classes are also targeted by that page's own nested rules.
import styles from '@/pages/media/MediaDetailPage.module.css'

// ── Plugin fold ──────────────────────────────────────────────────────────────

// Module-level cache so preferences are fetched once per page session (not per component mount)
let _foldsCache: Record<string, boolean> | null = null
let _foldsFetch: Promise<Record<string, boolean>> | null = null

function getFoldsCache(): Promise<Record<string, boolean>> {
  if (_foldsCache !== null) return Promise.resolve(_foldsCache)
  if (_foldsFetch) return _foldsFetch
  _foldsFetch = getMyPreferences().then(p => {
    _foldsCache = p.folds ?? {}
    return _foldsCache
  }).catch(() => {
    _foldsCache = {}
    return _foldsCache
  })
  return _foldsFetch
}

function useFold(key: string, defaultOpen: boolean) {
  const [isOpen, setIsOpen] = useState(defaultOpen)
  const [loaded, setLoaded] = useState(false)

  useEffect(() => {
    let cancelled = false
    getFoldsCache().then(folds => {
      if (cancelled) return
      if (key in folds) setIsOpen(folds[key])
      setLoaded(true)
    })
    return () => { cancelled = true }
  }, [key])

  function toggle() {
    const next = !isOpen
    setIsOpen(next)
    if (_foldsCache) _foldsCache[key] = next
    updateMyPreferences({ folds: { [key]: next } }).catch(() => {})
  }

  return { isOpen, toggle, loaded }
}

interface PluginFoldProps {
  foldKey: string
  label: string
  iconUrl?: string | null
  defaultOpen?: boolean
  children: React.ReactNode
}

export function PluginFold({ foldKey, label, iconUrl, defaultOpen = false, children }: PluginFoldProps) {
  const { isOpen, toggle } = useFold(foldKey, defaultOpen)

  return (
    <div className={styles.pluginFold}>
      <button className={styles.pluginFoldHeader} onClick={toggle} aria-expanded={isOpen}>
        {iconUrl && <img src={iconUrl} alt="" className={styles.pluginFoldIcon} onError={e => { e.currentTarget.style.display = 'none' }} />}
        <span className={styles.pluginFoldLabel}>{label}</span>
        <span className={`${styles.pluginFoldChevron} ${isOpen ? styles.pluginFoldChevronOpen : ''}`}>
          ›
        </span>
      </button>
      {isOpen && <div className={styles.pluginFoldBody}>{children}</div>}
    </div>
  )
}
