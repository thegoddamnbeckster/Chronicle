import { useState, useEffect, useCallback } from 'react'
import { listDirectory } from '@/api/filesystem'
import type { FilesystemListing } from '@/api/filesystem'
import styles from './FolderPickerModal.module.css'

interface FolderPickerModalProps {
  initialPath?: string
  onSelect: (path: string) => void
  onClose: () => void
}

export default function FolderPickerModal({
  initialPath,
  onSelect,
  onClose,
}: FolderPickerModalProps) {
  const [currentPath, setCurrentPath] = useState(initialPath ?? '')
  const [inputPath, setInputPath] = useState(initialPath ?? '')
  const [listing, setListing] = useState<FilesystemListing | null>(null)
  const [isLoading, setIsLoading] = useState(false)
  const [listError, setListError] = useState<string | null>(null)
  const [inputError, setInputError] = useState<string | null>(null)

  // Navigate to a directory (or '' for drive roots)
  const navigate = useCallback(async (path: string) => {
    setIsLoading(true)
    setListError(null)
    setInputError(null)
    try {
      const result = await listDirectory(path)
      setListing(result)
      setCurrentPath(result.path ?? '')
      setInputPath(result.path ?? '')
    } finally {
      setIsLoading(false)
    }
  }, [])

  // Load on mount
  useEffect(() => {
    // intentionally run once on mount; initialPath is only an opening hint, not a controlled prop
    navigate(initialPath ?? '').catch((err) =>
      setListError(err instanceof Error ? err.message : 'Failed to load directory')
    )
  }, [])

  // Escape key closes the modal
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const handlePathKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== 'Enter') return
    const typed = inputPath.trim()
    navigate(typed).catch((err) => setInputError(err instanceof Error ? err.message : 'Path not found'))
  }

  const handleSelect = async () => {
    const typed = inputPath.trim()
    if (typed && typed !== currentPath) {
      // User typed (or pasted) a path without pressing Enter — navigate to validate it first
      try {
        const result = await listDirectory(typed)
        const resolved = result.path ?? typed
        setListing(result)
        setCurrentPath(resolved)
        setInputPath(resolved)
        onSelect(resolved)
        onClose()
      } catch (err) {
        setInputError(err instanceof Error ? err.message : 'Path not found')
      }
    } else if (currentPath) {
      onSelect(currentPath)
      onClose()
    }
  }

  const selectedDisplay = currentPath || (inputPath.trim() ? inputPath.trim() : '(no folder selected)')

  return (
    <div
      className={styles.overlay}
      onClick={(e) => { if (e.target === e.currentTarget) onClose() }}
    >
      <div
        className={styles.modal}
        role="dialog"
        aria-modal="true"
        aria-label="Browse for Folder"
      >
        {/* Header */}
        <div className={styles.header}>
          <h2 className={styles.headerTitle}>Browse for Folder</h2>
          <button className={styles.closeBtn} onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>

        {/* Path bar */}
        <div className={styles.pathBar}>
          <input
            className={styles.pathInput}
            type="text"
            value={inputPath}
            onChange={(e) => setInputPath(e.target.value)}
            onKeyDown={handlePathKeyDown}
            placeholder="Type or paste a path, then press Enter"
            aria-label="Current path"
          />
          {inputError && <p className={styles.pathError}>{inputError}</p>}
        </div>

        {/* Directory list */}
        <div className={styles.dirList}>
          {isLoading && <p className={styles.loadingMsg}>Loading…</p>}

          {!isLoading && listError && (
            <p className={styles.errorMsg}>{listError}</p>
          )}

          {!isLoading && !listError && listing && (
            <>
              {/* Up row — shown whenever we are viewing a real directory (including drive roots);
                  navigates to parent, or back to the drive list if at a root (parent === null) */}
              {listing.path !== null && (
                <div
                  className={`${styles.dirRow} ${styles.upRow}`}
                  onClick={() => navigate(listing.parent ?? '').catch((err) => setListError(err instanceof Error ? err.message : 'Failed to load directory'))}
                  role="button"
                  tabIndex={0}
                  aria-label="Navigate up to parent directory"
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                      e.preventDefault()
                      navigate(listing.parent ?? '').catch((err) => setListError(err instanceof Error ? err.message : 'Failed to load directory'))
                    }
                  }}
                >
                  <span className={styles.dirIcon}>📁</span>
                  <span>.. (Up)</span>
                </div>
              )}

              {/* Empty-state messages */}
              {listing.directories.length === 0 && listing.parent === null && (
                <p className={styles.emptyMsg}>No drives found</p>
              )}
              {listing.directories.length === 0 && listing.parent !== null && (
                <p className={styles.emptyMsg}>No subfolders</p>
              )}

              {/* Folder rows */}
              {listing.directories.map((dir) => (
                <div
                  key={dir.path}
                  className={styles.dirRow}
                  onClick={() => navigate(dir.path).catch((err) => setListError(err instanceof Error ? err.message : 'Failed to load directory'))}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                      e.preventDefault()
                      navigate(dir.path).catch((err) => setListError(err instanceof Error ? err.message : 'Failed to load directory'))
                    }
                  }}
                >
                  <span className={styles.dirIcon}>📁</span>
                  <span>{dir.name}</span>
                </div>
              ))}
            </>
          )}
        </div>

        {/* Footer */}
        <div className={styles.footer}>
          <span className={styles.selectedPath} title={selectedDisplay}>
            {selectedDisplay}
          </span>
          <button className={styles.cancelBtn} onClick={onClose}>
            Cancel
          </button>
          <button
            className={styles.selectBtn}
            onClick={handleSelect}
            disabled={!currentPath && !inputPath.trim()}
          >
            Select
          </button>
        </div>
      </div>
    </div>
  )
}
