import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  getLists,
  createList,
  deleteList,
  type MediaListDto,
} from '@/api/lists'
import styles from './ListsPage.module.css'

export default function ListsPage() {
  const [lists, setLists] = useState<MediaListDto[]>([])
  const [loading, setLoading] = useState(true)

  // Create modal state
  const [showCreate, setShowCreate] = useState(false)
  const [newName, setNewName] = useState('')
  const [newDesc, setNewDesc] = useState('')
  const [newOrdered, setNewOrdered] = useState(true)
  const [creating, setCreating] = useState(false)
  const [createError, setCreateError] = useState('')

  const [deletingId, setDeletingId] = useState<number | null>(null)

  useEffect(() => {
    load()
  }, [])

  async function load() {
    setLoading(true)
    try {
      setLists(await getLists())
    } catch {
      // silent
    } finally {
      setLoading(false)
    }
  }

  async function handleCreate() {
    if (!newName.trim()) return
    setCreating(true)
    setCreateError('')
    try {
      const list = await createList(newName.trim(), newDesc.trim() || null, newOrdered)
      setLists(prev => [list, ...prev])
      setShowCreate(false)
      setNewName('')
      setNewDesc('')
      setNewOrdered(true)
    } catch {
      setCreateError('Failed to create list. Please try again.')
    } finally {
      setCreating(false)
    }
  }

  async function handleDelete(id: number, name: string) {
    if (!confirm(`Delete list "${name}"? This cannot be undone.`)) return
    setDeletingId(id)
    try {
      await deleteList(id)
      setLists(prev => prev.filter(l => l.id !== id))
    } catch {
      alert('Failed to delete list.')
    } finally {
      setDeletingId(null)
    }
  }

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1 className={styles.title}>Lists</h1>
        <button className={styles.createBtn} onClick={() => setShowCreate(true)}>
          + New List
        </button>
      </div>

      {loading ? (
        <p className={styles.empty}>Loading…</p>
      ) : lists.length === 0 ? (
        <div className={styles.emptyState}>
          <p>No lists yet.</p>
          <p className={styles.hint}>
            Create a list to organise media into ordered sequences — like the MCU Infinity Saga — or
            unordered collections.
          </p>
          <button className={styles.createBtn} onClick={() => setShowCreate(true)}>
            Create your first list
          </button>
        </div>
      ) : (
        <div className={styles.grid}>
          {lists.map(list => (
            <div key={list.id} className={styles.card}>
              <Link to={`/lists/${list.id}`} className={styles.cardLink}>
                <div className={styles.cardTop}>
                  <span className={styles.listType}>
                    {list.isOrdered ? '🔢 Ordered' : '📋 Unordered'}
                  </span>
                  <span className={styles.itemCount}>{list.itemCount} item{list.itemCount !== 1 ? 's' : ''}</span>
                </div>
                <h2 className={styles.cardName}>{list.name}</h2>
                {list.description && (
                  <p className={styles.cardDesc}>{list.description}</p>
                )}
              </Link>
              <div className={styles.cardActions}>
                <Link to={`/lists/${list.id}`} className={styles.viewBtn}>
                  View
                </Link>
                <button
                  className={styles.deleteBtn}
                  onClick={() => handleDelete(list.id, list.name)}
                  disabled={deletingId === list.id}
                >
                  {deletingId === list.id ? '…' : 'Delete'}
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* ── Create list modal ──────────────────────────────────────────── */}
      {showCreate && (
        <div className={styles.modalOverlay} onClick={() => setShowCreate(false)}>
          <div className={styles.modal} onClick={e => e.stopPropagation()}>
            <h2 className={styles.modalTitle}>New List</h2>

            <label className={styles.label}>
              Name <span className={styles.required}>*</span>
              <input
                className={styles.input}
                value={newName}
                onChange={e => setNewName(e.target.value)}
                placeholder="e.g. MCU Infinity Saga"
                autoFocus
                onKeyDown={e => e.key === 'Enter' && handleCreate()}
              />
            </label>

            <label className={styles.label}>
              Description
              <input
                className={styles.input}
                value={newDesc}
                onChange={e => setNewDesc(e.target.value)}
                placeholder="Optional"
              />
            </label>

            <label className={styles.checkboxLabel}>
              <input
                type="checkbox"
                checked={newOrdered}
                onChange={e => setNewOrdered(e.target.checked)}
              />
              Ordered list (items have a specific sequence)
            </label>

            {createError && <p className={styles.error}>{createError}</p>}

            <div className={styles.modalActions}>
              <button className={styles.cancelBtn} onClick={() => setShowCreate(false)}>
                Cancel
              </button>
              <button
                className={styles.createBtn}
                onClick={handleCreate}
                disabled={!newName.trim() || creating}
              >
                {creating ? 'Creating…' : 'Create List'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
