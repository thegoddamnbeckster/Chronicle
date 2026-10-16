import { useEffect, useMemo, useState } from 'react'
import { getTunables, setTunable, type Tunable } from '@/api/tunables'
import styles from './UsersPage.module.css'

function message(err: unknown, fallback: string): string {
  return err instanceof Error && err.message ? err.message : fallback
}

/** Adjust the policy numbers Chronicle's rules use (how much counts as watched, how long a failed lookup rests ...). */
export default function TuningPage() {
  const [rows, setRows] = useState<Tunable[] | null>(null)
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')

  function apply(list: Tunable[]) {
    setRows(list)
    setDrafts(Object.fromEntries(list.map(t => [t.key, String(t.value)])))
  }

  useEffect(() => {
    getTunables().then(apply).catch(err => setError(message(err, 'Could not load the settings.')))
  }, [])

  const groups = useMemo(() => {
    const byGroup = new Map<string, Tunable[]>()
    for (const t of rows ?? []) byGroup.set(t.group, [...(byGroup.get(t.group) ?? []), t])
    return [...byGroup.entries()]
  }, [rows])

  async function save(t: Tunable, value: number | null) {
    setBusy(t.key); setError(''); setNotice('')
    try {
      await setTunable(t.key, value)
      apply(await getTunables())
      setNotice(value === null ? `${t.label} is back to its default.` : `${t.label} saved.`)
    } catch (err) {
      setError(message(err, 'Could not save.'))
    } finally {
      setBusy('')
    }
  }

  if (!rows) {
    return <div className={styles.page}><h1 className={styles.title}>Tuning</h1>{error ? <p className={styles.error}>{error}</p> : <p className={styles.loading}>Loading…</p>}</div>
  }

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Tuning</h1>
      <p className={styles.hint}>
        The numbers behind Chronicle's rules. A change takes effect within about a minute; &quot;Default&quot; puts the built-in value back.
      </p>
      {error && <p className={styles.error} role="alert">{error}</p>}
      {notice && <p className={styles.success}>{notice}</p>}

      {groups.map(([group, list]) => (
        <div className={styles.card} key={group}>
          <h2 className={styles.cardTitle}>{group}</h2>
          {list.map(t => {
            const draft = drafts[t.key] ?? ''
            const parsed = Number(draft)
            const valid = draft.trim() !== '' && Number.isFinite(parsed) && parsed >= t.min && parsed <= t.max
            return (
              <div className={styles.formRow} key={t.key}>
                <div className={styles.formGroup}>
                  <label className={styles.label} htmlFor={t.key}>{t.label}</label>
                  <p className={styles.hint}>{t.description}</p>
                </div>
                <div className={styles.formGroup}>
                  <input
                    id={t.key} type="number" className={styles.textInput} value={draft}
                    min={t.min} max={t.max} step="any"
                    onChange={e => setDrafts(d => ({ ...d, [t.key]: e.target.value }))}
                  />
                  <span className={styles.hint}>{t.unit} (default {t.default}, {t.min} to {t.max}){t.isCustom ? ' - changed' : ''}</span>
                </div>
                <div className={styles.formGroup}>
                  <button type="button" disabled={!valid || busy === t.key || parsed === t.value}
                          onClick={() => save(t, parsed)}>Save</button>
                  {' '}
                  <button type="button" disabled={!t.isCustom || busy === t.key}
                          onClick={() => save(t, null)}>Default</button>
                </div>
              </div>
            )
          })}
        </div>
      ))}
    </div>
  )
}
