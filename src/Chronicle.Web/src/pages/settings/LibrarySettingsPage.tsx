import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation } from '@tanstack/react-query'
import { loadPresets, savePresets, type LibraryPreset } from '@/pages/library/LibraryPage'
import {
  loadSortSettings,
  saveSortSettings,
  DEFAULT_SORT_SETTINGS,
  type SortSettings,
} from '@/utils/sortSettings'
import { clearScannerData, nuclearReset } from '@/api/library'
import styles from './LibrarySettingsPage.module.css'

const STATUS_LABELS: Record<string, string> = {
  Watching: 'Watching',
  PlanToWatch: 'Plan to Watch',
  Completed: 'Completed',
  Dropped: 'Dropped',
  OnHold: 'On Hold',
  Rewatching: 'Rewatching',
}

const SORT_LABELS: Record<string, string> = {
  'name-asc':       'Name A–Z',
  'name-desc':      'Name Z–A',
  'year-desc':      'Year (newest first)',
  'year-asc':       'Year (oldest first)',
  'dateAdded-desc': 'Date Added (newest)',
  'dateAdded-asc':  'Date Added (oldest)',
  'rating-desc':    'My Rating (highest)',
  'rating-asc':     'My Rating (lowest)',
  'status-asc':     'Status A–Z',
}

const PAGE_SIZE_LABELS: Record<string, string> = {
  minimal: 'Few (6)',
  medium:  'Medium (24)',
  maximal: 'Many (100)',
  all:     'All',
}

function describePreset(p: LibraryPreset): string {
  const prefs = p.prefs
  const status = prefs.statusFilter ? STATUS_LABELS[prefs.statusFilter] ?? prefs.statusFilter : 'All statuses'
  const sort = SORT_LABELS[`${prefs.sortBy}-${prefs.sortDir}`] ?? prefs.sortBy
  const size = PAGE_SIZE_LABELS[prefs.pageSizePreset] ?? prefs.pageSizePreset
  return `${status} · ${sort} · ${size}`
}

export default function LibrarySettingsPage() {
  const [presets, setPresets] = useState<LibraryPreset[]>(loadPresets)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [editName, setEditName] = useState('')

  // ── Sort settings ────────────────────────────────────────────────────────
  const [sortSettings, setSortSettings] = useState<SortSettings>(loadSortSettings)

  // ── Danger Zone ──────────────────────────────────────────────────────────
  const [clearConfirm, setClearConfirm] = useState(false)
  const clearMut = useMutation({
    mutationFn: clearScannerData,
    onSuccess: (data) => {
      setClearConfirm(false)
      alert(`Done. ${data.deleted} scanner-imported items removed.`)
    },
  })

  const [resetConfirm, setResetConfirm] = useState(false)
  const [resetToken, setResetToken] = useState('')
  const resetMut = useMutation({
    mutationFn: () => nuclearReset(resetToken),
    onSuccess: () => {
      setResetConfirm(false)
      setResetToken('')
      alert('Library has been reset.')
    },
    onError: (err: Error) => alert(err.message),
  })
  const [newArticle, setNewArticle] = useState('')

  function updateSortSettings(patch: Partial<SortSettings>) {
    const next = { ...sortSettings, ...patch }
    setSortSettings(next)
    saveSortSettings(next)
  }

  function addArticle() {
    const word = newArticle.trim().toLowerCase()
    if (!word) return
    if (sortSettings.ignoredArticles.includes(word)) { setNewArticle(''); return }
    updateSortSettings({ ignoredArticles: [...sortSettings.ignoredArticles, word] })
    setNewArticle('')
  }

  function removeArticle(word: string) {
    updateSortSettings({ ignoredArticles: sortSettings.ignoredArticles.filter(a => a !== word) })
  }

  function resetArticles() {
    updateSortSettings({ ignoredArticles: [...DEFAULT_SORT_SETTINGS.ignoredArticles] })
  }

  function startEdit(preset: LibraryPreset) {
    setEditingId(preset.id)
    setEditName(preset.name)
  }

  function saveEdit() {
    if (!editName.trim() || !editingId) { setEditingId(null); return }
    const next = presets.map(p =>
      p.id === editingId ? { ...p, name: editName.trim() } : p,
    )
    setPresets(next)
    savePresets(next)
    setEditingId(null)
  }

  function deletePreset(id: string) {
    if (!confirm('Delete this preset?')) return
    const next = presets.filter(p => p.id !== id)
    setPresets(next)
    savePresets(next)
  }

  function moveUp(idx: number) {
    if (idx === 0) return
    const next = [...presets]
    ;[next[idx - 1], next[idx]] = [next[idx], next[idx - 1]]
    setPresets(next)
    savePresets(next)
  }

  function moveDown(idx: number) {
    if (idx === presets.length - 1) return
    const next = [...presets]
    ;[next[idx], next[idx + 1]] = [next[idx + 1], next[idx]]
    setPresets(next)
    savePresets(next)
  }

  return (
    <div className={styles.page}>
      <div className={styles.breadcrumb}>
        <Link to="/settings/api-keys" className={styles.breadcrumbLink}>Settings</Link>
        <span className={styles.breadcrumbSep}>/</span>
        <span>Library</span>
      </div>

      <h2 className={styles.heading}>Library Settings</h2>

      {/* ── Sorting section ─────────────────────────────────────────────── */}
      <section className={styles.section}>
        <div className={styles.sectionHeader}>
          <h3 className={styles.sectionTitle}>Sorting</h3>
          <p className={styles.sectionDesc}>
            Configure how library items are sorted by name.
          </p>
        </div>

        <div className={styles.sortCard}>
          <label className={styles.toggleRow}>
            <span className={styles.toggleLabel}>
              <span className={styles.toggleTitle}>Ignore leading articles</span>
              <span className={styles.toggleDesc}>
                Strip words like "The", "A", "Le" from the start of titles when sorting by name.
                e.g. "The Dark Knight" sorts under D.
              </span>
            </span>
            <button
              role="switch"
              aria-checked={sortSettings.ignoreArticles}
              className={`${styles.toggle} ${sortSettings.ignoreArticles ? styles.toggleOn : ''}`}
              onClick={() => updateSortSettings({ ignoreArticles: !sortSettings.ignoreArticles })}
            >
              <span className={styles.toggleThumb} />
            </button>
          </label>

          {sortSettings.ignoreArticles && (
            <div className={styles.articleSection}>
              <div className={styles.articleHeader}>
                <span className={styles.articleTitle}>Ignored words</span>
                <button className={styles.resetBtn} onClick={resetArticles}>
                  Reset to defaults
                </button>
              </div>

              <div className={styles.articleTags}>
                {sortSettings.ignoredArticles.map(word => (
                  <span key={word} className={styles.articleTag}>
                    {word}
                    <button
                      className={styles.articleRemove}
                      onClick={() => removeArticle(word)}
                      title={`Remove "${word}"`}
                      aria-label={`Remove "${word}"`}
                    >
                      ×
                    </button>
                  </span>
                ))}
              </div>

              <div className={styles.articleAddRow}>
                <input
                  className={styles.articleInput}
                  type="text"
                  placeholder="Add a word…"
                  value={newArticle}
                  onChange={e => setNewArticle(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter') addArticle() }}
                  maxLength={20}
                />
                <button className={styles.articleAddBtn} onClick={addArticle}>
                  Add
                </button>
              </div>
            </div>
          )}
        </div>
      </section>

      {/* ── Presets section ──────────────────────────────────────────────── */}
      <section className={styles.section}>
        <div className={styles.sectionHeader}>
          <h3 className={styles.sectionTitle}>Saved Presets</h3>
          <p className={styles.sectionDesc}>
            Presets let you save a combination of status filter, sort, and page size to apply with one click.
            Create presets from the <Link to="/library" className={styles.inlineLink}>Library page</Link>.
          </p>
        </div>

        {presets.length === 0 ? (
          <div className={styles.empty}>
            <p>No saved presets yet.</p>
            <p>
              Go to the <Link to="/library" className={styles.inlineLink}>Library</Link>, configure
              your filters and sort, then click <strong>Save as Preset</strong>.
            </p>
          </div>
        ) : (
          <ul className={styles.presetList}>
            {presets.map((preset, idx) => (
              <li key={preset.id} className={styles.presetRow}>
                <div className={styles.presetReorder}>
                  <button
                    className={styles.reorderBtn}
                    onClick={() => moveUp(idx)}
                    disabled={idx === 0}
                    title="Move up"
                  >▲</button>
                  <button
                    className={styles.reorderBtn}
                    onClick={() => moveDown(idx)}
                    disabled={idx === presets.length - 1}
                    title="Move down"
                  >▼</button>
                </div>

                <div className={styles.presetInfo}>
                  {editingId === preset.id ? (
                    <input
                      className={styles.editInput}
                      value={editName}
                      onChange={e => setEditName(e.target.value)}
                      onBlur={saveEdit}
                      onKeyDown={e => {
                        if (e.key === 'Enter') saveEdit()
                        if (e.key === 'Escape') setEditingId(null)
                      }}
                      autoFocus
                    />
                  ) : (
                    <span
                      className={styles.presetName}
                      onClick={() => startEdit(preset)}
                      title="Click to rename"
                    >
                      {preset.name}
                    </span>
                  )}
                  <span className={styles.presetDesc}>{describePreset(preset)}</span>
                </div>

                <div className={styles.presetActions}>
                  <Link
                    to="/library"
                    className={styles.applyBtn}
                    onClick={() => {
                      // Write the preset's prefs to localStorage so the library page picks them up
                      localStorage.setItem('chronicle_library_prefs', JSON.stringify(preset.prefs))
                    }}
                  >Apply</Link>
                  <button
                    className={styles.editBtn}
                    onClick={() => startEdit(preset)}
                  >Rename</button>
                  <button
                    className={styles.deleteBtn}
                    onClick={() => deletePreset(preset.id)}
                  >Delete</button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>

      {/* ── Danger Zone ──────────────────────────────────────────────────── */}
      <section className={styles.section}>
        <div className={styles.sectionHeader}>
          <h3 className={`${styles.sectionTitle} ${styles.dangerTitle}`}>Danger Zone</h3>
          <p className={styles.sectionDesc}>
            These actions are irreversible. Think carefully before proceeding.
          </p>
        </div>

        <div className={styles.dangerCard}>

          {/* Clear scanner data */}
          <div className={styles.dangerRow}>
            <div className={styles.dangerInfo}>
              <span className={styles.dangerLabel}>Clear File Scanner Data</span>
              <span className={styles.dangerDesc}>
                Removes all media items that were imported via the File Scanner.
                Use this before re-scanning with the improved hierarchical scanner.
                Manually-added and metadata-matched items are unaffected.
              </span>
            </div>
            {!clearConfirm ? (
              <button className={styles.dangerBtnAmber} onClick={() => setClearConfirm(true)}>
                Clear Scanner Data
              </button>
            ) : (
              <div className={styles.dangerConfirmRow}>
                <span className={styles.dangerConfirmText}>
                  This will delete all file-scanner items. Are you sure?
                </span>
                <button
                  className={styles.dangerBtnAmber}
                  onClick={() => clearMut.mutate()}
                  disabled={clearMut.isPending}
                >
                  {clearMut.isPending ? 'Clearing…' : 'Yes, clear it'}
                </button>
                <button className={styles.cancelBtn} onClick={() => setClearConfirm(false)}>
                  Cancel
                </button>
              </div>
            )}
          </div>

          <hr className={styles.dangerDivider} />

          {/* Nuclear reset */}
          <div className={styles.dangerRow}>
            <div className={styles.dangerInfo}>
              <span className={styles.dangerLabel}>Reset Entire Library</span>
              <span className={styles.dangerDesc}>
                Permanently deletes <strong>everything</strong>: all media items, library entries,
                scrobble history, ratings, and notes. This cannot be undone.
                Chronicle will be as if it was freshly installed.
              </span>
            </div>
            {!resetConfirm ? (
              <button className={styles.dangerBtnRed} onClick={() => setResetConfirm(true)}>
                Reset Entire Library
              </button>
            ) : (
              <div className={styles.dangerConfirmBox}>
                <p className={styles.dangerWarning}>
                  This will permanently delete ALL media items, library entries,
                  scrobble history, ratings, and notes. There is no undo.
                </p>
                <p className={styles.dangerWarning}>
                  To confirm, type <strong>RESET</strong> in the box below:
                </p>
                <input
                  className={styles.dangerInput}
                  value={resetToken}
                  onChange={e => setResetToken(e.target.value)}
                  placeholder="Type RESET to confirm"
                  autoFocus
                />
                <div className={styles.dangerConfirmActions}>
                  <button
                    className={styles.dangerBtnRed}
                    onClick={() => resetMut.mutate()}
                    disabled={resetToken !== 'RESET' || resetMut.isPending}
                  >
                    {resetMut.isPending ? 'Resetting…' : 'Yes, delete everything'}
                  </button>
                  <button
                    className={styles.cancelBtn}
                    onClick={() => { setResetConfirm(false); setResetToken('') }}
                  >
                    Cancel
                  </button>
                </div>
              </div>
            )}
          </div>
        </div>
      </section>
    </div>
  )
}
