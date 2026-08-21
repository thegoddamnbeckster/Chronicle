import { useState } from 'react'
import { CONTACT_KINDS, type ContactInput, type UserContactDto } from '@/api/users'
import styles from './ContactsEditor.module.css'

const OTHER = '__other__'

interface Props {
  contacts: UserContactDto[]
  onAdd: (input: ContactInput) => Promise<void>
  onUpdate: (id: number, input: ContactInput) => Promise<void>
  onDelete: (id: number) => Promise<void>
}

/**
 * Add/edit/remove ways to reach a user. Kind is free-form on the backend, so the picker
 * offers common ones and falls back to a text box — a new network never needs a release.
 */
export default function ContactsEditor({ contacts, onAdd, onUpdate, onDelete }: Props) {
  const [kindChoice, setKindChoice] = useState<string>('email')
  const [customKind, setCustomKind] = useState('')
  const [label, setLabel] = useState('')
  const [value, setValue] = useState('')
  const [isPrimary, setIsPrimary] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const [editingId, setEditingId] = useState<number | null>(null)
  const [editValue, setEditValue] = useState('')
  const [editLabel, setEditLabel] = useState('')

  const resolvedKind = kindChoice === OTHER ? customKind.trim() : kindChoice

  async function handleAdd(e: React.FormEvent) {
    e.preventDefault()
    if (!resolvedKind || !value.trim()) return
    setBusy(true)
    setError('')
    try {
      await onAdd({ kind: resolvedKind, label: label.trim() || null, value: value.trim(), isPrimary })
      setValue('')
      setLabel('')
      setCustomKind('')
      setIsPrimary(false)
    } catch {
      setError('Could not add that contact.')
    } finally {
      setBusy(false)
    }
  }

  function startEdit(c: UserContactDto) {
    setEditingId(c.id)
    setEditValue(c.value)
    setEditLabel(c.label ?? '')
  }

  async function saveEdit(c: UserContactDto) {
    if (!editValue.trim()) return
    setBusy(true)
    try {
      await onUpdate(c.id, {
        kind: c.kind,
        label: editLabel.trim() || null,
        value: editValue.trim(),
        isPrimary: c.isPrimary,
      })
      setEditingId(null)
    } catch {
      setError('Could not save that contact.')
    } finally {
      setBusy(false)
    }
  }

  async function togglePrimary(c: UserContactDto) {
    setBusy(true)
    try {
      await onUpdate(c.id, { kind: c.kind, label: c.label, value: c.value, isPrimary: !c.isPrimary })
    } catch {
      setError('Could not change the primary contact.')
    } finally {
      setBusy(false)
    }
  }

  async function handleDelete(c: UserContactDto) {
    if (!confirm(`Remove ${c.kind} "${c.value}"?`)) return
    setBusy(true)
    try {
      await onDelete(c.id)
    } catch {
      setError('Could not remove that contact.')
    } finally {
      setBusy(false)
    }
  }

  // Grouped so several of the same kind (work/home/mobile) read as one block.
  const grouped = contacts.reduce<Record<string, UserContactDto[]>>((acc, c) => {
    const bucket = acc[c.kind] ?? (acc[c.kind] = [])
    bucket.push(c)
    return acc
  }, {})

  return (
    <div className={styles.wrap}>
      {contacts.length === 0 ? (
        <p className={styles.empty}>No contact methods yet.</p>
      ) : (
        Object.keys(grouped).sort().map(kind => (
          <div key={kind} className={styles.group}>
            <span className={styles.groupLabel}>{kind}</span>
            {grouped[kind].map(c => (
              <div key={c.id} className={styles.row}>
                {editingId === c.id ? (
                  <>
                    <input
                      className={styles.input}
                      value={editLabel}
                      placeholder="label (optional)"
                      onChange={e => setEditLabel(e.target.value)}
                    />
                    <input
                      className={styles.input}
                      value={editValue}
                      onChange={e => setEditValue(e.target.value)}
                    />
                    <button className={styles.smallBtn} disabled={busy} onClick={() => saveEdit(c)}>Save</button>
                    <button className={styles.smallBtn} onClick={() => setEditingId(null)}>Cancel</button>
                  </>
                ) : (
                  <>
                    <span className={styles.value}>{c.value}</span>
                    {c.label && <span className={styles.tag}>{c.label}</span>}
                    <button
                      className={c.isPrimary ? styles.primaryOn : styles.primaryOff}
                      disabled={busy}
                      title={c.isPrimary ? `Primary ${kind}` : `Make this the primary ${kind}`}
                      onClick={() => togglePrimary(c)}
                    >
                      {c.isPrimary ? '★ primary' : '☆'}
                    </button>
                    <button className={styles.smallBtn} onClick={() => startEdit(c)}>Edit</button>
                    <button className={styles.dangerBtn} disabled={busy} onClick={() => handleDelete(c)}>Remove</button>
                  </>
                )}
              </div>
            ))}
          </div>
        ))
      )}

      <form className={styles.addForm} onSubmit={handleAdd}>
        <select
          className={styles.select}
          value={kindChoice}
          onChange={e => setKindChoice(e.target.value)}
          aria-label="Contact type"
        >
          {CONTACT_KINDS.map(k => <option key={k} value={k}>{k}</option>)}
          <option value={OTHER}>Other…</option>
        </select>
        {kindChoice === OTHER && (
          <input
            className={styles.input}
            placeholder="type (e.g. pager)"
            value={customKind}
            onChange={e => setCustomKind(e.target.value)}
            maxLength={40}
          />
        )}
        <input
          className={styles.input}
          placeholder="label (optional)"
          value={label}
          onChange={e => setLabel(e.target.value)}
          maxLength={80}
        />
        <input
          className={styles.inputWide}
          placeholder="address, number, handle, or URL"
          value={value}
          onChange={e => setValue(e.target.value)}
          maxLength={500}
        />
        <label className={styles.checkLabel}>
          <input type="checkbox" checked={isPrimary} onChange={e => setIsPrimary(e.target.checked)} />
          Primary
        </label>
        <button className={styles.addBtn} type="submit" disabled={busy || !resolvedKind || !value.trim()}>
          Add
        </button>
      </form>

      {error && <p className={styles.error}>{error}</p>}
    </div>
  )
}
