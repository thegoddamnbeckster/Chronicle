import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  SCAN_STRATEGIES, createMediaType, deleteMediaType, listMediaTypesAdmin, releaseMediaType, updateMediaType,
  type MediaTypeAdmin, type MediaTypeInput,
} from '@/api/mediaTypes'
import { isKnownVerb } from '@/utils/typeWording'
import styles from './UsersPage.module.css'

const VERB_SUGGESTIONS = ['watched', 'listened', 'read', 'played']
const FAMILY_SUGGESTIONS = ['tv', 'movie', 'music']

function message(err: unknown, fallback: string): string {
  return err instanceof Error && err.message ? err.message : fallback
}

interface FormState {
  name: string
  displayName: string
  description: string
  levels: string
  labels: string
  verb: string
  unit: string
  collections: boolean
  trackable: boolean
  scan: string
  hints: string
  family: string
  castHeading: string
  active: boolean
}

const BLANK: FormState = {
  name: '', displayName: '', description: '', levels: '1', labels: '', verb: 'watched', unit: 'minutes',
  collections: false, trackable: true, scan: '', hints: '', family: '', castHeading: '', active: true,
}

function fromType(t: MediaTypeAdmin): FormState {
  return {
    name: t.name, displayName: t.displayName, description: t.description ?? '', levels: String(t.hierarchyLevels),
    labels: t.hierarchyLabels.join(', '), verb: t.interactionVerb, unit: t.progressUnit,
    collections: t.supportsCollections, trackable: t.isTrackable, scan: t.scanStrategy ?? '', hints: t.scanHints ?? '', family: t.providerFamily ?? '', castHeading: t.castHeading ?? '', active: t.isActive,
  }
}

function toInput(f: FormState, creating: boolean): MediaTypeInput {
  return {
    ...(creating ? { name: f.name.trim() } : {}),
    displayName: f.displayName.trim(),
    description: f.description.trim(),
    hierarchyLevels: Number(f.levels),
    hierarchyLabels: f.labels.split(',').map(l => l.trim()).filter(Boolean),
    interactionVerb: f.verb.trim().toLowerCase(),
    progressUnit: f.unit.trim().toLowerCase(),
    supportsCollections: f.collections,
    isTrackable: f.trackable,
    scanStrategy: f.scan === '' ? null : f.scan,
    isActive: f.active,
    scanHints: f.hints.trim(),
    providerFamily: f.family.trim().toLowerCase(),
    castHeading: f.castHeading.trim(),
  }
}

export default function MediaTypesPage() {
  const [types, setTypes] = useState<MediaTypeAdmin[] | null>(null)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  // null = closed, 'new' = adding, a number = editing that type
  const [editing, setEditing] = useState<number | 'new' | null>(null)
  const [form, setForm] = useState<FormState>(BLANK)

  const load = useCallback(async () => {
    try {
      setTypes(await listMediaTypesAdmin())
      setError('')
    } catch (err) {
      setError(message(err, 'Could not load the media types.'))
    }
  }, [])

  useEffect(() => { void load() }, [load])

  function open(target: number | 'new', t?: MediaTypeAdmin) {
    setEditing(target)
    setForm(t ? fromType(t) : BLANK)
    setError('')
    setNotice('')
  }

  async function run(action: () => Promise<string | void>) {
    setBusy(true)
    setError('')
    setNotice('')
    try {
      const done = await action()
      if (done) setNotice(done)
      await load()
    } catch (err) {
      setError(message(err, 'That did not work.'))
    } finally {
      setBusy(false)
    }
  }

  async function save() {
    const creating = editing === 'new'
    await run(async () => {
      if (creating) await createMediaType(toInput(form, true))
      else await updateMediaType(editing as number, toInput(form, false))
      setEditing(null)
      return creating ? `Added ${form.displayName.trim()}.` : `Saved ${form.displayName.trim()}.`
    })
  }

  function set<K extends keyof FormState>(key: K, value: FormState[K]) {
    setForm(f => ({ ...f, [key]: value }))
  }

  if (!types) {
    return <div className={styles.page}><h1 className={styles.title}>Media Types</h1>{error ? <p className={styles.error}>{error}</p> : <p className={styles.loading}>Loading…</p>}</div>
  }

  const current = typeof editing === 'number' ? types.find(t => t.id === editing) : undefined
  const levelsLocked = !!current && current.itemCount > 0

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Media Types</h1>
      {error && <p className={styles.error} role="alert">{error}</p>}
      {notice && <p className={styles.success}>{notice}</p>}

      <div className={styles.card}>
        <div className={styles.cardHeader}>
          <h2 className={styles.cardTitle}>Types ({types.length})</h2>
          <button className={styles.createBtn} disabled={busy} onClick={() => open('new')}>Add a type</button>
        </div>
        <p className={styles.hint}>
          Installed plugins register the types they handle automatically. Add your own for anything else; a plugin that
          handles it can be installed later. Each type chooses its own action word (watched, listened, read, played, ...)
          and the names of its levels, and that is what the rest of Chronicle uses to word its screens.
        </p>

        {types.map(t => (
          <div key={t.id} className={styles.userRow}>
            <div className={styles.userInfo}>
              <span className={styles.userName}>
                {t.displayName} <span className={styles.userMeta}>({t.name})</span>
                {!t.isActive && ' · switched off'}{t.isBuiltIn && ' · built in'}
              </span>
              <span className={styles.userMeta}>
                {t.hierarchyLabels.join(' > ') || 'no levels named'} · {t.interactionVerb} · {t.itemCount} item{t.itemCount === 1 ? '' : 's'}
                {' · '}
                {t.plugins.length > 0
                  ? `handled by ${t.plugins.map(p => p.name).join(', ')}`
                  : 'no installed plugin handles it'}
                {t.isUserModified && ' · edited by you'}
              </span>
              {t.plugins.length === 0 && t.itemCount === 0 && (
                <span className={styles.userMeta}>
                  Nothing will fill this type in until a plugin for it is installed - <Link to="/plugins">browse plugins</Link>.
                </span>
              )}
            </div>
            <div className={styles.userActions}>
              <button className={styles.smallBtn} disabled={busy} onClick={() => open(t.id, t)}>Edit</button>
              {t.isUserModified && t.plugins.length > 0 && (
                <button className={styles.smallBtn} disabled={busy} title="Let the installed plugins describe this type again"
                        onClick={() => void run(async () => { await releaseMediaType(t.id); return `${t.displayName} will follow its plugins again at the next start.` })}>
                  Hand back to plugins
                </button>
              )}
              {!t.isBuiltIn && (
                <button className={styles.dangerBtn} disabled={busy}
                        onClick={() => { if (window.confirm(`Delete the type "${t.displayName}"?`)) void run(async () => { await deleteMediaType(t.id); return `Deleted ${t.displayName}.` }) }}>
                  Delete
                </button>
              )}
            </div>
          </div>
        ))}
      </div>

      {editing !== null && (
        <div className={styles.card} role="dialog" aria-label={editing === 'new' ? 'Add a media type' : 'Edit media type'}>
          <h2 className={styles.cardTitle}>{editing === 'new' ? 'Add a media type' : `Edit ${current?.displayName ?? ''}`}</h2>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="mt-display">Display name</label>
              <input id="mt-display" className={styles.textInput} value={form.displayName} onChange={e => set('displayName', e.target.value)} />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="mt-name">Internal name</label>
              <input id="mt-name" className={styles.textInput} value={form.name} disabled={editing !== 'new'}
                     onChange={e => set('name', e.target.value)} placeholder="comics" />
              <span className={styles.hint}>
                {editing === 'new' ? 'Lowercase letters, digits, dashes. Plugins refer to this, so it cannot change later.' : 'Fixed: plugins refer to it.'}
              </span>
            </div>
          </div>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="mt-levels">Levels</label>
              <input id="mt-levels" type="number" min={1} max={5} className={styles.textInput} value={form.levels}
                     disabled={levelsLocked} onChange={e => set('levels', e.target.value)} />
              {levelsLocked && <span className={styles.hint}>Fixed while this type holds items.</span>}
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="mt-labels">Level names, top first (comma separated)</label>
              <input id="mt-labels" className={styles.textInput} value={form.labels} onChange={e => set('labels', e.target.value)}
                     placeholder="Series, Volume, Issue" />
            </div>
          </div>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="mt-verb">Action word (past tense)</label>
              <input id="mt-verb" className={styles.textInput} value={form.verb} list="mt-verbs" onChange={e => set('verb', e.target.value)} />
              <datalist id="mt-verbs">{VERB_SUGGESTIONS.map(v => <option key={v} value={v} />)}</datalist>
              {form.verb.trim() !== '' && !isKnownVerb(form.verb) && (
                <span className={styles.hint}>Chronicle has no special wording for this word, so statuses read &quot;Planned&quot; and &quot;In progress&quot;.</span>
              )}
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="mt-unit">Progress unit</label>
              <input id="mt-unit" className={styles.textInput} value={form.unit} onChange={e => set('unit', e.target.value)} placeholder="minutes, pages, tracks" />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="mt-scan">Folder scanning</label>
              <select id="mt-scan" className={styles.textInput} value={form.scan} onChange={e => set('scan', e.target.value)}>
                {SCAN_STRATEGIES.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
              </select>
            </div>
          </div>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="mt-family">Also served by providers for</label>
              <input id="mt-family" className={styles.textInput} value={form.family} list="mt-families" onChange={e => set('family', e.target.value)} placeholder="tv, movie, music or blank" />
              <datalist id="mt-families">{FAMILY_SUGGESTIONS.map(v => <option key={v} value={v} />)}</datalist>
              <span className={styles.hint}>A metadata plugin that supports this family is also used for this type (anime -&gt; tv). Blank: only plugins that name this type.</span>
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="mt-cast">Heading for credited people</label>
              <input id="mt-cast" className={styles.textInput} value={form.castHeading} onChange={e => set('castHeading', e.target.value)} placeholder="Cast" />
              <span className={styles.hint}>Shown above the people on an item&apos;s page: &quot;Band Members&quot;, &quot;Narrators&quot;. Blank: &quot;Cast&quot;.</span>
            </div>
          </div>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="mt-hints">What its files look like (optional)</label>
              <textarea
                id="mt-hints" className={styles.textInput} rows={3} spellCheck={false} value={form.hints}
                onChange={e => set('hints', e.target.value)}
                placeholder={'{"filePatterns":[],"folderPatterns":[],"extensions":[".mkv"]}'}
              />
              <span className={styles.hint}>
                Lets the scanner notice a folder that was scanned as the wrong type. Patterns are regular expressions
                matched against file and folder names; extensions are the file types this kind holds. Leave empty to
                never flag this type.
              </span>
            </div>
          </div>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="mt-desc">Description</label>
              <input id="mt-desc" className={styles.textInput} value={form.description} onChange={e => set('description', e.target.value)} />
            </div>
          </div>
          <div className={styles.formRow}>
            <label className={styles.checkLabel}><input type="checkbox" checked={form.trackable} onChange={e => set('trackable', e.target.checked)} /> Tracked in each person&apos;s library</label>
            <label className={styles.checkLabel}><input type="checkbox" checked={form.collections} onChange={e => set('collections', e.target.checked)} /> Top level is a collection of separate works</label>
            <label className={styles.checkLabel}><input type="checkbox" checked={form.active} onChange={e => set('active', e.target.checked)} /> Switched on</label>
          </div>
          <div className={styles.userActions}>
            <button className={styles.createBtn} disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button>
            <button className={styles.smallBtn} disabled={busy} onClick={() => setEditing(null)}>Cancel</button>
          </div>
        </div>
      )}
    </div>
  )
}
