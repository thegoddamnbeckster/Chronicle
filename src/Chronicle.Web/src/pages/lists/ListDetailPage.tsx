import { useEffect, useState, useRef } from 'react'
import { useParams, Link } from 'react-router-dom'
import {
  getList,
  updateList,
  removeItemFromList,
  reorderListItems,
  type MediaListDetailDto,
  type MediaListItemDto,
} from '@/api/lists'
import { searchMedia } from '@/api/media'
import type { MediaItem } from '@/types'
import { addItemToList } from '@/api/lists'
import styles from './ListDetailPage.module.css'

export default function ListDetailPage() {
  const { id } = useParams<{ id: string }>()
  const listId = Number(id)

  const [list, setList] = useState<MediaListDetailDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  // Edit name inline
  const [editingName, setEditingName] = useState(false)
  const [nameValue, setNameValue] = useState('')

  // Add item panel
  const [showAdd, setShowAdd] = useState(false)
  const [searchQuery, setSearchQuery] = useState('')
  const [searchResults, setSearchResults] = useState<MediaItem[]>([])
  const [searching, setSearching] = useState(false)
  const [addingId, setAddingId] = useState<number | null>(null)
  const searchTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

  // Drag-and-drop reorder
  const [dragIdx, setDragIdx] = useState<number | null>(null)
  const [dragOverIdx, setDragOverIdx] = useState<number | null>(null)

  useEffect(() => {
    load()
  }, [listId])

  async function load() {
    setLoading(true)
    setError('')
    try {
      const data = await getList(listId)
      setList(data)
      setNameValue(data.name)
    } catch {
      setError('List not found.')
    } finally {
      setLoading(false)
    }
  }

  // ── Inline name edit ────────────────────────────────────────────────────────

  async function saveName() {
    if (!list || !nameValue.trim() || nameValue === list.name) {
      setEditingName(false)
      setNameValue(list?.name ?? '')
      return
    }
    try {
      const updated = await updateList(listId, { name: nameValue.trim() })
      setList(prev => prev ? { ...prev, name: updated.name } : prev)
    } catch {
      setNameValue(list.name)
    }
    setEditingName(false)
  }

  // ── Search + add ────────────────────────────────────────────────────────────

  function handleSearchChange(q: string) {
    setSearchQuery(q)
    if (searchTimer.current) clearTimeout(searchTimer.current)
    if (!q.trim()) {
      setSearchResults([])
      return
    }
    searchTimer.current = setTimeout(async () => {
      setSearching(true)
      try {
        setSearchResults(await searchMedia(q, undefined, 1, true))
      } catch {
        setSearchResults([])
      } finally {
        setSearching(false)
      }
    }, 300)
  }

  async function handleAddItem(media: MediaItem) {
    if (!list) return
    // Already in list?
    if (list.items.some(i => i.mediaItem.id === media.id)) return

    setAddingId(media.id)
    try {
      await addItemToList(listId, media.id)
      await load()   // reload to get updated positions
      setSearchQuery('')
      setSearchResults([])
    } catch {
      alert('Failed to add item.')
    } finally {
      setAddingId(null)
    }
  }

  // ── Remove ──────────────────────────────────────────────────────────────────

  async function handleRemove(item: MediaListItemDto) {
    if (!confirm(`Remove "${item.mediaItem.name}" from this list?`)) return
    try {
      await removeItemFromList(listId, item.id)
      setList(prev =>
        prev ? { ...prev, items: prev.items.filter(i => i.id !== item.id) } : prev,
      )
    } catch {
      alert('Failed to remove item.')
    }
  }

  // ── Drag-and-drop reorder (ordered lists only) ──────────────────────────────

  function handleDragStart(idx: number) {
    setDragIdx(idx)
  }

  function handleDragOver(e: React.DragEvent, idx: number) {
    e.preventDefault()
    setDragOverIdx(idx)
  }

  async function handleDrop(dropIdx: number) {
    if (dragIdx === null || !list || dragIdx === dropIdx) {
      setDragIdx(null)
      setDragOverIdx(null)
      return
    }

    // Reorder locally
    const newItems = [...list.items]
    const [moved] = newItems.splice(dragIdx, 1)
    newItems.splice(dropIdx, 0, moved)

    // Assign sequential positions
    const withPositions = newItems.map((item, idx) => ({ ...item, position: idx }))
    setList(prev => prev ? { ...prev, items: withPositions } : prev)
    setDragIdx(null)
    setDragOverIdx(null)

    // Persist to API
    try {
      await reorderListItems(
        listId,
        withPositions.map(item => ({ itemId: item.id, position: item.position })),
      )
    } catch {
      await load()   // rollback on failure
    }
  }

  // ── Render ──────────────────────────────────────────────────────────────────

  if (loading) return <div className={styles.page}><p className={styles.status}>Loading…</p></div>
  if (error || !list) return (
    <div className={styles.page}>
      <p className={styles.status}>{error || 'Not found.'}</p>
      <Link to="/lists" className={styles.backLink}>← Back to Lists</Link>
    </div>
  )

  const alreadyInList = new Set(list.items.map(i => i.mediaItem.id))

  return (
    <div className={styles.page}>
      {/* ── Header ── */}
      <div className={styles.header}>
        <Link to="/lists" className={styles.backLink}>← Lists</Link>

        <div className={styles.titleRow}>
          {editingName ? (
            <input
              className={styles.titleInput}
              value={nameValue}
              onChange={e => setNameValue(e.target.value)}
              onBlur={saveName}
              onKeyDown={e => { if (e.key === 'Enter') saveName() }}
              autoFocus
            />
          ) : (
            <h1 className={styles.title} onClick={() => setEditingName(true)} title="Click to rename">
              {list.name}
            </h1>
          )}
          <button
            className={styles.badge}
            onClick={async () => {
              try {
                const updated = await updateList(listId, { isOrdered: !list.isOrdered })
                setList(prev => prev ? { ...prev, isOrdered: updated.isOrdered } : prev)
              } catch { /* silent */ }
            }}
            title="Click to toggle ordered/unordered"
          >
            {list.isOrdered ? '🔢 Ordered' : '📋 Unordered'}
          </button>
        </div>

        {list.description && <p className={styles.description}>{list.description}</p>}
      </div>

      {/* ── Add item panel ── */}
      <div className={styles.addSection}>
        {showAdd ? (
          <div className={styles.searchBox}>
            <input
              className={styles.searchInput}
              value={searchQuery}
              onChange={e => handleSearchChange(e.target.value)}
              placeholder="Search your media library…"
              autoFocus
            />
            {searching && <p className={styles.searchStatus}>Searching…</p>}
            {searchResults.length > 0 && (
              <ul className={styles.searchResults}>
                {searchResults.map(media => (
                  <li key={media.id} className={styles.searchResult}>
                    <div>
                      <span className={styles.resultName}>{media.name}</span>
                      {media.year && <span className={styles.resultYear}> ({media.year})</span>}
                      <span className={styles.resultType}>{media.mediaTypeName}</span>
                    </div>
                    <button
                      className={styles.addBtn}
                      onClick={() => handleAddItem(media)}
                      disabled={addingId === media.id || alreadyInList.has(media.id)}
                    >
                      {alreadyInList.has(media.id) ? 'Added' : addingId === media.id ? '…' : '+ Add'}
                    </button>
                  </li>
                ))}
              </ul>
            )}
            <button className={styles.cancelSearchBtn} onClick={() => { setShowAdd(false); setSearchQuery(''); setSearchResults([]) }}>
              Done
            </button>
          </div>
        ) : (
          <button className={styles.addItemBtn} onClick={() => setShowAdd(true)}>
            + Add Item
          </button>
        )}
      </div>

      {/* ── Item list ── */}
      {list.items.length === 0 ? (
        <p className={styles.empty}>No items yet. Add some media above.</p>
      ) : (
        <ul className={styles.itemList}>
          {list.items.map((item, idx) => (
            <li
              key={item.id}
              className={`${styles.item} ${dragOverIdx === idx ? styles.dragOver : ''}`}
              draggable={list.isOrdered}
              onDragStart={() => handleDragStart(idx)}
              onDragOver={e => handleDragOver(e, idx)}
              onDrop={() => handleDrop(idx)}
              onDragEnd={() => { setDragIdx(null); setDragOverIdx(null) }}
            >
              {list.isOrdered && (
                <span className={styles.position} title="Drag to reorder">
                  ⠿ {item.position + 1}
                </span>
              )}
              <div className={styles.itemInfo}>
                <Link
                  to={`/media/${item.mediaItem.id}`}
                  state={{ listIds: list.items.map(i => i.mediaItem.id), listLabel: list.name }}
                  className={styles.itemName}
                >
                  {item.mediaItem.name}
                </Link>
                <span className={styles.itemMeta}>
                  {item.mediaItem.mediaTypeName}
                  {item.mediaItem.year ? ` · ${item.mediaItem.year}` : ''}
                  {item.mediaItem.runtimeMinutes ? ` · ${item.mediaItem.runtimeMinutes} min` : ''}
                </span>
                {item.notes && <span className={styles.itemNotes}>{item.notes}</span>}
              </div>
              {item.mediaItem.posterUrl && (
                <img
                  src={item.mediaItem.posterUrl}
                  alt={item.mediaItem.name}
                  className={styles.poster}
                  onError={e => { e.currentTarget.style.display = 'none' }}
                />
              )}
              <button
                className={styles.removeBtn}
                onClick={() => handleRemove(item)}
                title="Remove from list"
              >
                ✕
              </button>
            </li>
          ))}
        </ul>
      )}

      {list.isOrdered && list.items.length > 1 && (
        <p className={styles.dragHint}>Drag rows to reorder</p>
      )}
    </div>
  )
}
