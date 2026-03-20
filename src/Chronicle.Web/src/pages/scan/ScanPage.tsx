import { useState, useEffect, useRef } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getScanStatus, getScanProgress, previewGrouped, importGroups, getImportProgress,
  getScanFolders, createScanFolder, updateScanFolder, deleteScanFolder, validatePath,
} from '@/api/scan'
import type { ScanProgress, ImportProgressState, CreateScanFolderPayload, UpdateScanFolderPayload } from '@/api/scan'
import { getMediaTypes } from '@/api/media'
import { useBackgroundActivity } from '@/contexts/BackgroundActivityContext'
import type { ScanGroupResult, MediaTypeOption, ScanFolder } from '@/types'
import PathInput from '@/components/PathInput'
import ScanGroupCard, { groupToPayload } from './ScanGroupCard'
import styles from './ScanPage.module.css'

type Step = 'configure' | 'review' | 'done'

// ── Saved Folders Panel ───────────────────────────────────────────────────────

interface AddRowState {
  path: string
  mediaTypeId: number | ''
  recursive: boolean
  pathError: string | null
  validating: boolean
}

interface EditRowState {
  path: string
  mediaTypeId: number | ''
  recursive: boolean
  isEnabled: boolean
  pathError: string | null
  validating: boolean
}

function emptyAddRow(): AddRowState {
  return { path: '', mediaTypeId: '', recursive: true, pathError: null, validating: false }
}

function folderToEditRow(f: ScanFolder): EditRowState {
  return {
    path: f.path,
    mediaTypeId: f.mediaTypeId,
    recursive: f.recursive,
    isEnabled: f.isEnabled,
    pathError: null,
    validating: false,
  }
}

interface SavedFoldersPanelProps {
  open: boolean
  onToggle: () => void
  onScanNow: (folder: ScanFolder) => void
  supportedTypes: MediaTypeOption[]
}

function SavedFoldersPanel({ open, onToggle, onScanNow, supportedTypes }: SavedFoldersPanelProps) {
  const queryClient = useQueryClient()
  const [addingFolder, setAddingFolder] = useState(false)
  const [addRow, setAddRow] = useState<AddRowState>(emptyAddRow())
  const [editingFolderId, setEditingFolderId] = useState<number | null>(null)
  const [editRow, setEditRow] = useState<EditRowState | null>(null)

  const { data: folders = [], isLoading } = useQuery({
    queryKey: ['scan-folders'],
    queryFn: getScanFolders,
  })

  const createMut = useMutation({
    mutationFn: (payload: CreateScanFolderPayload) => createScanFolder(payload),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['scan-folders'] })
      setAddingFolder(false)
      setAddRow(emptyAddRow())
    },
  })

  const updateMut = useMutation({
    mutationFn: ({ id, payload }: { id: number; payload: UpdateScanFolderPayload }) =>
      updateScanFolder(id, payload),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['scan-folders'] })
      setEditingFolderId(null)
      setEditRow(null)
    },
  })

  const deleteMut = useMutation({
    mutationFn: (id: number) => deleteScanFolder(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['scan-folders'] })
    },
  })

  // ── Add row handlers ────────────────────────────────────────────────────────

  async function handleAddBlur() {
    if (!addRow.path.trim()) return
    setAddRow(prev => ({ ...prev, validating: true, pathError: null }))
    try {
      const result = await validatePath(addRow.path.trim())
      setAddRow(prev => ({ ...prev, validating: false, pathError: result.valid ? null : (result.error ?? 'Invalid path') }))
    } catch {
      setAddRow(prev => ({ ...prev, validating: false, pathError: 'Could not validate path' }))
    }
  }

  function handleAddSave() {
    if (!addRow.path.trim() || addRow.mediaTypeId === '' || addRow.pathError) return
    createMut.mutate({
      path: addRow.path.trim(),
      mediaTypeId: Number(addRow.mediaTypeId),
      recursive: addRow.recursive,
    })
  }

  function handleAddCancel() {
    setAddingFolder(false)
    setAddRow(emptyAddRow())
  }

  // ── Edit row handlers ───────────────────────────────────────────────────────

  function startEdit(folder: ScanFolder) {
    setEditingFolderId(folder.id)
    setEditRow(folderToEditRow(folder))
  }

  function cancelEdit() {
    setEditingFolderId(null)
    setEditRow(null)
  }

  async function handleEditBlur() {
    if (!editRow || !editRow.path.trim()) return
    setEditRow(prev => prev ? { ...prev, validating: true, pathError: null } : prev)
    try {
      const result = await validatePath(editRow.path.trim())
      setEditRow(prev => prev ? { ...prev, validating: false, pathError: result.valid ? null : (result.error ?? 'Invalid path') } : prev)
    } catch {
      setEditRow(prev => prev ? { ...prev, validating: false, pathError: 'Could not validate path' } : prev)
    }
  }

  function handleEditSave(folder: ScanFolder) {
    if (!editRow || !editRow.path.trim() || editRow.mediaTypeId === '' || editRow.pathError) return
    updateMut.mutate({
      id: folder.id,
      payload: {
        path: editRow.path.trim(),
        mediaTypeId: Number(editRow.mediaTypeId),
        recursive: editRow.recursive,
        isEnabled: editRow.isEnabled,
      },
    })
  }

  // ── Toggle enabled (quick action, no edit mode needed) ───────────────────────

  function handleToggleEnabled(folder: ScanFolder) {
    updateMut.mutate({
      id: folder.id,
      payload: {
        path: folder.path,
        mediaTypeId: folder.mediaTypeId,
        recursive: folder.recursive,
        isEnabled: !folder.isEnabled,
      },
    })
  }

  return (
    <div className={styles.foldersCard}>
      {/* Panel header / toggle */}
      <button className={styles.foldersPanelToggle} onClick={onToggle} type="button">
        <span className={styles.foldersChevron}>{open ? '▼' : '▶'}</span>
        <span className={styles.foldersPanelTitle}>Saved Folders</span>
        <span className={styles.foldersPanelCount}>
          {folders.length > 0 ? `${folders.length} folder${folders.length !== 1 ? 's' : ''}` : ''}
        </span>
      </button>

      {open && (
        <div className={styles.foldersPanelBody}>
          {isLoading && (
            <p className={styles.foldersEmpty}>Loading…</p>
          )}

          {!isLoading && folders.length === 0 && !addingFolder && (
            <p className={styles.foldersEmpty}>No saved folders yet. Add one below.</p>
          )}

          {/* Folder rows */}
          {!isLoading && folders.map(folder => (
            <div key={folder.id} className={styles.folderRow}>
              {editingFolderId === folder.id && editRow ? (
                /* ── Edit mode ─────────────────────────────────────────── */
                <div className={styles.folderEditBlock}>
                  <div className={styles.folderEditFields}>
                    <input
                      className={`${styles.textInput} ${editRow.pathError ? styles.inputError : ''}`}
                      value={editRow.path}
                      onChange={e => setEditRow(prev => prev ? { ...prev, path: e.target.value, pathError: null } : prev)}
                      onBlur={handleEditBlur}
                      placeholder="Path…"
                      disabled={updateMut.isPending}
                    />
                    {editRow.pathError && (
                      <span className={styles.fieldError}>{editRow.pathError}</span>
                    )}
                    <select
                      className={styles.select}
                      value={editRow.mediaTypeId}
                      onChange={e => setEditRow(prev => prev ? { ...prev, mediaTypeId: e.target.value === '' ? '' : Number(e.target.value) } : prev)}
                      disabled={updateMut.isPending}
                    >
                      <option value="">— select type —</option>
                      {supportedTypes.map(t => (
                        <option key={t.id} value={t.id}>{t.displayName}</option>
                      ))}
                    </select>
                    <label className={styles.checkLabel}>
                      <input
                        type="checkbox"
                        checked={editRow.recursive}
                        onChange={e => setEditRow(prev => prev ? { ...prev, recursive: e.target.checked } : prev)}
                        disabled={updateMut.isPending}
                      />
                      Recursive
                    </label>
                    <label className={styles.checkLabel}>
                      <input
                        type="checkbox"
                        checked={editRow.isEnabled}
                        onChange={e => setEditRow(prev => prev ? { ...prev, isEnabled: e.target.checked } : prev)}
                        disabled={updateMut.isPending}
                      />
                      Enabled
                    </label>
                  </div>
                  <div className={styles.folderEditActions}>
                    <button
                      className={styles.scanBtn}
                      onClick={() => handleEditSave(folder)}
                      disabled={updateMut.isPending || !!editRow.pathError || editRow.validating || !editRow.path.trim() || editRow.mediaTypeId === ''}
                      type="button"
                    >
                      {updateMut.isPending ? 'Saving…' : 'Save'}
                    </button>
                    <button
                      className={styles.secondaryBtn}
                      onClick={cancelEdit}
                      disabled={updateMut.isPending}
                      type="button"
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              ) : (
                /* ── View mode ─────────────────────────────────────────── */
                <div className={styles.folderViewRow}>
                  <div className={styles.folderInfo}>
                    <span className={styles.folderPath} title={folder.path}>{folder.path}</span>
                    <span className={styles.mediaTypeBadge}>{folder.mediaTypeName}</span>
                    <span className={styles.folderMeta}>
                      {folder.lastScannedAt
                        ? `Last scanned ${new Date(folder.lastScannedAt).toLocaleDateString()}`
                        : 'Never scanned'}
                    </span>
                  </div>
                  <div className={styles.folderActions}>
                    <label className={styles.enabledToggle} title={folder.isEnabled ? 'Enabled' : 'Disabled'}>
                      <input
                        type="checkbox"
                        checked={folder.isEnabled}
                        onChange={() => handleToggleEnabled(folder)}
                        disabled={updateMut.isPending}
                      />
                      <span>{folder.isEnabled ? 'Enabled' : 'Disabled'}</span>
                    </label>
                    <button
                      className={styles.secondaryBtn}
                      onClick={() => onScanNow(folder)}
                      type="button"
                    >
                      Scan Now
                    </button>
                    <button
                      className={styles.secondaryBtn}
                      onClick={() => startEdit(folder)}
                      type="button"
                    >
                      Edit
                    </button>
                    <button
                      className={styles.deleteBtn}
                      onClick={() => {
                        if (window.confirm(`Remove "${folder.path}" from saved folders?`)) {
                          deleteMut.mutate(folder.id);
                        }
                      }}
                      disabled={deleteMut.isPending}
                      type="button"
                      title="Delete folder"
                    >
                      ✕
                    </button>
                  </div>
                </div>
              )}
            </div>
          ))}

          {/* Add folder row */}
          {addingFolder ? (
            <div className={styles.folderAddBlock}>
              <div className={styles.folderEditFields}>
                <input
                  className={`${styles.textInput} ${addRow.pathError ? styles.inputError : ''}`}
                  value={addRow.path}
                  onChange={e => setAddRow(prev => ({ ...prev, path: e.target.value, pathError: null }))}
                  onBlur={handleAddBlur}
                  placeholder="C:\Movies or /mnt/media/movies"
                  disabled={createMut.isPending}
                  autoFocus
                />
                {addRow.pathError && (
                  <span className={styles.fieldError}>{addRow.pathError}</span>
                )}
                <select
                  className={styles.select}
                  value={addRow.mediaTypeId}
                  onChange={e => setAddRow(prev => ({ ...prev, mediaTypeId: e.target.value === '' ? '' : Number(e.target.value) }))}
                  disabled={createMut.isPending}
                >
                  <option value="">— select type —</option>
                  {supportedTypes.map(t => (
                    <option key={t.id} value={t.id}>{t.displayName}</option>
                  ))}
                </select>
                <label className={styles.checkLabel}>
                  <input
                    type="checkbox"
                    checked={addRow.recursive}
                    onChange={e => setAddRow(prev => ({ ...prev, recursive: e.target.checked }))}
                    disabled={createMut.isPending}
                  />
                  Include subdirectories
                </label>
              </div>
              <div className={styles.folderEditActions}>
                <button
                  className={styles.scanBtn}
                  onClick={handleAddSave}
                  disabled={createMut.isPending || !!addRow.pathError || addRow.validating || !addRow.path.trim() || addRow.mediaTypeId === ''}
                  type="button"
                >
                  {createMut.isPending ? 'Saving…' : 'Save'}
                </button>
                <button
                  className={styles.secondaryBtn}
                  onClick={handleAddCancel}
                  disabled={createMut.isPending}
                  type="button"
                >
                  Cancel
                </button>
              </div>
            </div>
          ) : (
            <button
              className={styles.addFolderBtn}
              onClick={() => setAddingFolder(true)}
              type="button"
            >
              + Add folder
            </button>
          )}
        </div>
      )}
    </div>
  )
}

// ── Main Page ─────────────────────────────────────────────────────────────────

export default function ScanPage() {
  // ── Configuration state ──────────────────────────────────────────────────
  const [path, setPath] = useState('')
  const [recursive, setRecursive] = useState(true)
  const [mediaTypeId, setMediaTypeId] = useState<number | ''>('')

  // ── Saved folders panel state ─────────────────────────────────────────────
  const [foldersOpen, setFoldersOpen] = useState(true)

  // ── Pipeline state ───────────────────────────────────────────────────────
  const [step, setStep] = useState<Step>('configure')
  const [groupResult, setGroupResult] = useState<ScanGroupResult | null>(null)
  const [rejectedKeys, setRejectedKeys] = useState<Set<string>>(new Set())
  const [importResult, setImportResult] = useState<{ imported: number; failed: number; duplicates: number } | null>(null)
  const [error, setError] = useState<string | null>(null)

  // ── Scan progress (polled while preview mutation is pending) ─────────────
  const [scanProgress, setScanProgress] = useState<ScanProgress | null>(null)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  // ── Import progress (polled after import-groups returns 202) ─────────────
  const [importProgress, setImportProgress] = useState<ImportProgressState | null>(null)
  const importPollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  // ── Ref to configure form for "Scan Now" scroll ───────────────────────────
  const configureRef = useRef<HTMLDivElement | null>(null)

  const queryClient = useQueryClient()
  const { addJob, completeJob, failJob } = useBackgroundActivity()

  // ── Queries ──────────────────────────────────────────────────────────────
  const { data: status } = useQuery({ queryKey: ['scan-status'], queryFn: getScanStatus })
  const { data: allMediaTypes = [] } = useQuery({ queryKey: ['media-types'], queryFn: getMediaTypes })

  const supportedTypes: MediaTypeOption[] = allMediaTypes.filter((t) =>
    status?.supportedMediaTypeNames.includes(t.name),
  )

  // ── Mutations ─────────────────────────────────────────────────────────────
  const previewMut = useMutation({
    mutationFn: () => {
      if (!mediaTypeId) throw new Error('Select a media type.')
      return previewGrouped({ path: path.trim(), recursive, mediaTypeId: Number(mediaTypeId) })
    },
    onSuccess: (data) => {
      setGroupResult(data)
      setRejectedKeys(new Set())
      setError(null)
      setStep('review')
      setFoldersOpen(false) // auto-collapse when results arrive
    },
    onError: (err: Error) => setError(err.message),
  })

  const importMut = useMutation({
    mutationFn: () => {
      if (!groupResult) throw new Error('No scan result.')
      const toImport = groupResult.groups
        .filter(g => !rejectedKeys.has(g.groupKey))
        .map(groupToPayload)
      if (toImport.length === 0) throw new Error('No groups selected for import.')
      return importGroups({ groups: toImport, mediaTypeId: Number(mediaTypeId) })
    },
    onMutate: () => {
      const count = groupResult?.groups.filter(g => !rejectedKeys.has(g.groupKey)).length ?? 0
      setImportProgress(null)
      return addJob(`Importing ${count} groups…`)
    },
    onSuccess: (_data, _vars, jobId) => {
      // API returned 202 — start polling import-progress
      importPollRef.current = setInterval(async () => {
        try {
          const p = await getImportProgress()
          setImportProgress(p)
          if (p.isComplete) {
            clearInterval(importPollRef.current!)
            importPollRef.current = null
            if (p.error) {
              setError(p.error)
              failJob(jobId as string, p.error)
            } else if (p.result) {
              setImportResult({
                imported: p.result.imported,
                failed: p.result.failed,
                duplicates: p.result.duplicates,
              })
              setStep('done')
              completeJob(jobId as string, `${p.result.imported} imported`)
              // Invalidate library/media caches so the library page refreshes
              void queryClient.invalidateQueries({ queryKey: ['library'] })
              void queryClient.invalidateQueries({ queryKey: ['media'] })
            }
          }
        } catch {
          // ignore transient polling errors
        }
      }, 500)
    },
    onError: (err: Error, _vars, jobId) => {
      setError(err.message)
      failJob(jobId as string, err.message)
    },
  })

  // ── Scan progress polling ────────────────────────────────────────────────
  useEffect(() => {
    if (previewMut.isPending) {
      setScanProgress(null)
      pollRef.current = setInterval(async () => {
        try {
          const p = await getScanProgress()
          setScanProgress(p)
        } catch {
          // ignore polling errors
        }
      }, 500)
    } else {
      if (pollRef.current) {
        clearInterval(pollRef.current)
        pollRef.current = null
      }
      if (!previewMut.isPending) setScanProgress(null)
    }
    return () => {
      if (pollRef.current) {
        clearInterval(pollRef.current)
        pollRef.current = null
      }
    }
  }, [previewMut.isPending])

  // ── Cleanup import poll on unmount ──────────────────────────────────────
  useEffect(() => {
    return () => {
      if (importPollRef.current) {
        clearInterval(importPollRef.current)
        importPollRef.current = null
      }
    }
  }, [])

  // ── Helpers ───────────────────────────────────────────────────────────────
  const toggleRejected = (key: string) => {
    setRejectedKeys(prev => {
      const next = new Set(prev)
      next.has(key) ? next.delete(key) : next.add(key)
      return next
    })
  }

  const canScan = path.trim() !== '' && mediaTypeId !== '' && !previewMut.isPending

  function reset() {
    if (importPollRef.current) {
      clearInterval(importPollRef.current)
      importPollRef.current = null
    }
    setStep('configure')
    setGroupResult(null)
    setRejectedKeys(new Set())
    setImportResult(null)
    setImportProgress(null)
    setError(null)
  }

  // ── "Scan Now" from saved folder ─────────────────────────────────────────
  function handleScanNow(folder: ScanFolder) {
    setPath(folder.path)
    setMediaTypeId(folder.mediaTypeId)
    setRecursive(folder.recursive)
    // Re-open configure step if in done/review state
    reset()
    // Scroll to configure form
    setTimeout(() => {
      configureRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })
    }, 50)
  }

  // ── Render ────────────────────────────────────────────────────────────────
  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1 className={styles.title}>File Scan</h1>
        {step !== 'configure' && step !== 'done' && (
          <button className={styles.resetBtn} onClick={reset}>Start over</button>
        )}
      </div>

      {/* Step indicator */}
      <div className={styles.stepBar}>
        {(['configure', 'review', 'done'] as const).map((s, i) => (
          <div key={s} className={`${styles.stepItem} ${step === s ? styles.stepActive : ''} ${isStepDone(step, s) ? styles.stepDone : ''}`}>
            <span className={styles.stepNum}>{i + 1}</span>
            <span className={styles.stepLabel}>{stepLabel(s)}</span>
          </div>
        ))}
      </div>

      {error && <p className={styles.errorMsg}>{error}</p>}

      {/* ── Saved Folders panel (always above configure form) ─────────────── */}
      <SavedFoldersPanel
        open={foldersOpen}
        onToggle={() => setFoldersOpen(v => !v)}
        onScanNow={handleScanNow}
        supportedTypes={supportedTypes}
      />

      {/* ── Step 1: Configure ────────────────────────────────────────────── */}
      {step === 'configure' && (
        <div className={styles.formCard} ref={configureRef}>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="scan-path">Directory path</label>
            <PathInput
              id="scan-path"
              className={styles.textInput}
              placeholder="C:\Movies or /mnt/media/movies"
              value={path}
              onChange={setPath}
            />
          </div>

          <div className={styles.row}>
            <div className={styles.field}>
              <label className={styles.label} htmlFor="media-type">Media type</label>
              <select
                id="media-type"
                className={styles.select}
                value={mediaTypeId}
                onChange={(e) => setMediaTypeId(e.target.value === '' ? '' : Number(e.target.value))}
              >
                <option value="">— select type —</option>
                {supportedTypes.map((t) => (
                  <option key={t.id} value={t.id}>{t.displayName}</option>
                ))}
              </select>
            </div>
          </div>

          <div className={styles.checkRow}>
            <label className={styles.checkLabel}>
              <input type="checkbox" checked={recursive} onChange={(e) => setRecursive(e.target.checked)} />
              Include subdirectories
            </label>
          </div>

          <button
            className={styles.scanBtn}
            disabled={!canScan}
            onClick={() => previewMut.mutate()}
          >
            {previewMut.isPending ? 'Scanning…' : 'Scan Directory'}
          </button>

          {/* Real-time per-folder progress shown while the scan runs */}
          {previewMut.isPending && (
            <div className={styles.progressPanel}>
              {scanProgress?.currentFolder ? (
                <>
                  <div className={styles.progressRow}>
                    <span className={styles.progressSpinner} />
                    <span className={styles.progressLabel}>
                      Folder {scanProgress.foldersScanned} of {scanProgress.totalFolders}
                      {scanProgress.filesFound > 0 && ` · ${scanProgress.filesFound} files found`}
                    </span>
                  </div>
                  <div className={styles.progressFolder} title={scanProgress.currentFolder}>
                    {scanProgress.currentFolder}
                  </div>
                </>
              ) : (
                <div className={styles.progressRow}>
                  <span className={styles.progressSpinner} />
                  <span className={styles.progressLabel}>Enumerating directories…</span>
                </div>
              )}
            </div>
          )}
        </div>
      )}

      {/* ── Step 2: Review ───────────────────────────────────────────────── */}
      {step === 'review' && groupResult && (
        <div className={styles.resultCard}>
          <div className={styles.resultHeader}>
            <h2 className={styles.resultTitle}>
              Found {groupResult.totalGroups} group{groupResult.totalGroups !== 1 ? 's' : ''}
              <span className={styles.subtitle}> · {groupResult.groups.length - rejectedKeys.size} selected</span>
              <span className={styles.subtitle}> ({groupResult.totalFiles} files)</span>
            </h2>
            <button
              className={styles.scanBtn}
              disabled={
                (groupResult.groups.length - rejectedKeys.size) === 0 ||
                importMut.isPending ||
                (importProgress?.isRunning ?? false)
              }
              onClick={() => importMut.mutate()}
            >
              {importMut.isPending || importProgress?.isRunning
                ? 'Starting…'
                : `Import ${groupResult.groups.length - rejectedKeys.size} groups →`}
            </button>
          </div>
          <p className={styles.reviewHint}>
            Accepting a group imports it and all its children into Chronicle.
            TMDB metadata enrichment runs automatically in the background.
          </p>

          {/* ── Import progress bar ─────────────────────────────────────── */}
          {importProgress?.isRunning && (
            <div className={styles.importProgressPanel}>
              <div className={styles.importProgressHeader}>
                <span className={styles.progressSpinner} />
                <span className={styles.importProgressLabel}>
                  Importing {importProgress.processed} of {importProgress.total} group{importProgress.total !== 1 ? 's' : ''}…
                </span>
              </div>
              <div className={styles.importProgressTrack}>
                <div
                  className={styles.importProgressFill}
                  style={{
                    width: importProgress.total > 0
                      ? `${Math.round((importProgress.processed / importProgress.total) * 100)}%`
                      : '0%',
                  }}
                />
              </div>
              {importProgress.currentItemName && (
                <div className={styles.importProgressItem} title={importProgress.currentItemName}>
                  {importProgress.currentItemName}
                </div>
              )}
            </div>
          )}

          <div className={styles.groupList}>
            {groupResult.groups.map(g => (
              <ScanGroupCard
                key={g.groupKey}
                group={g}
                checked={!rejectedKeys.has(g.groupKey)}
                onToggle={toggleRejected}
              />
            ))}
          </div>

          {groupResult.ungrouped.length > 0 && (
            <details className={styles.ungroupedSection}>
              <summary className={styles.ungroupedSummary}>
                {groupResult.ungrouped.length} ungrouped file{groupResult.ungrouped.length !== 1 ? 's' : ''} (will not be imported)
              </summary>
              <ul className={styles.ungroupedList}>
                {groupResult.ungrouped.map(f => <li key={f} className={styles.ungroupedFile}>{f}</li>)}
              </ul>
            </details>
          )}
        </div>
      )}

      {/* ── Step 3: Done ─────────────────────────────────────────────────── */}
      {step === 'done' && importResult && (
        <div className={styles.resultCard}>
          <h2 className={styles.resultTitle}>Import complete</h2>
          <p className={styles.reviewHint}>
            TMDB metadata is being downloaded in the background. Check your library in a moment.
          </p>
          <div className={styles.resultStats}>
            <div className={styles.stat}>
              <span className={styles.statValue}>{importResult.imported}</span>
              <span className={styles.statLabel}>Imported</span>
            </div>
            {importResult.duplicates > 0 && (
              <div className={styles.stat}>
                <span className={styles.statValue}>{importResult.duplicates}</span>
                <span className={styles.statLabel}>Already in library</span>
              </div>
            )}
            <div className={styles.stat}>
              <span className={styles.statValue}>{importResult.failed}</span>
              <span className={styles.statLabel}>Failed</span>
            </div>
          </div>
          <button className={styles.scanBtn} onClick={reset}>
            Scan another directory
          </button>
        </div>
      )}
    </div>
  )
}

// ── Utilities ─────────────────────────────────────────────────────────────────

function stepLabel(s: Step): string {
  return { configure: 'Configure', review: 'Review', done: 'Done' }[s]
}

function isStepDone(current: Step, check: Step): boolean {
  const order: Step[] = ['configure', 'review', 'done']
  return order.indexOf(current) > order.indexOf(check)
}
