import { useState, useEffect, useCallback } from 'react'
import { getFieldAliases, putFieldAliases, type FieldAliasConfig } from '@/api/settings'
import { useAuth } from '@/hooks/useAuth'
import styles from './FieldAliasesPage.module.css'

const FIELD_LABELS: Record<string, string> = {
  title:              'Title',
  overview:           'Description',
  year:               'Year',
  poster_url:         'Poster Image',
  backdrop_url:       'Backdrop Image',
  runtime_minutes:    'Runtime',
  rating:             'Rating',
  genres:             'Genres',
  cast:               'Cast',
  directors:          'Directors',
  tags:               'Tags',
  logo_url:           'Logo Image',
  banner_url:         'Banner Image',
  thumb_url:          'Thumbnail Image',
  clearart_url:       'Clear Art',
  disc_url:           'Disc Art',
  character_art_url:  'Character Art',
  collection:         'Collection',
  composer:           'Composer',
  label:              'Label',
  bpm:                'BPM',
  mood:               'Mood',
  language:           'Language',
  isrc:               'ISRC',
}

export default function FieldAliasesPage() {
  const { user }                       = useAuth()
  const isAdmin                        = user?.isAdmin ?? false
  const [config, setConfig]            = useState<FieldAliasConfig | null>(null)
  const [aliases, setAliases]          = useState<Record<string, string[]>>({})
  const [drafts, setDrafts]            = useState<Record<string, string>>({})
  const [saving, setSaving]            = useState(false)
  const [saved, setSaved]              = useState(false)
  const [error, setError]              = useState<string | null>(null)

  useEffect(() => {
    getFieldAliases()
      .then(cfg => {
        setConfig(cfg)
        setAliases(cfg.aliases)
      })
      .catch(e => setError(String(e)))
  }, [])

  const save = useCallback(async (next: Record<string, string[]>) => {
    setSaving(true)
    setError(null)
    try {
      await putFieldAliases(next)
      setSaved(true)
      setTimeout(() => setSaved(false), 2000)
    } catch (e) {
      setError(String(e))
    } finally {
      setSaving(false)
    }
  }, [])

  function addAlias(field: string) {
    const value = (drafts[field] ?? '').trim()
    if (!value) return
    const current = aliases[field] ?? []
    if (current.some(a => a.toLowerCase() === value.toLowerCase())) {
      setDrafts(prev => ({ ...prev, [field]: '' }))
      return
    }
    const next = { ...aliases, [field]: [...current, value] }
    setAliases(next)
    setDrafts(prev => ({ ...prev, [field]: '' }))
    save(next)
  }

  function removeAlias(field: string, alias: string) {
    const remaining = (aliases[field] ?? []).filter(a => a !== alias)
    const next = { ...aliases }
    if (remaining.length > 0) next[field] = remaining
    else delete next[field]
    setAliases(next)
    save(next)
  }

  if (!config) return (
    <div className={styles.page}>
      {error ? <p className={styles.error}>{error}</p> : <p>Loading…</p>}
    </div>
  )

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1 className={styles.title}>Field Aliases</h1>
        <p className={styles.subtitle}>
          Extra names to recognize for each field, when a plugin uses a different key than
          Chronicle's canonical one (e.g. "recordLabel" alongside "label").
          {!isAdmin && <span className={styles.readOnlyNote}> (read-only — admin access required)</span>}
        </p>
        {saving && <span className={styles.saveStatus}>Saving…</span>}
        {!saving && saved && <span className={`${styles.saveStatus} ${styles.saveStatusOk}`}>Saved ✓</span>}
        {error && <p className={styles.error}>{error}</p>}
      </div>

      <div className={styles.tableWrap}>
        <div className={styles.tableHead}>
          <div className={styles.colField}>Field</div>
          <div className={styles.colAliases}>Extra Aliases</div>
        </div>

        {config.canonicalFields.map(field => {
          const current = aliases[field] ?? []
          return (
            <div key={field} className={styles.row}>
              <div className={styles.colField}>{FIELD_LABELS[field] ?? field}</div>
              <div className={styles.colAliases}>
                <div className={styles.chipRow}>
                  {current.map(alias => (
                    <span key={alias} className={styles.chip}>
                      <span className={styles.chipName}>{alias}</span>
                      {isAdmin && (
                        <button
                          type="button"
                          className={styles.chipRemove}
                          onClick={() => removeAlias(field, alias)}
                          disabled={saving}
                          aria-label={`Remove alias ${alias}`}
                        >
                          ×
                        </button>
                      )}
                    </span>
                  ))}
                  {isAdmin && (
                    <input
                      type="text"
                      className={styles.addInput}
                      placeholder="Add alias…"
                      value={drafts[field] ?? ''}
                      disabled={saving}
                      onChange={e => setDrafts(prev => ({ ...prev, [field]: e.target.value }))}
                      onKeyDown={e => {
                        if (e.key === 'Enter') {
                          e.preventDefault()
                          addAlias(field)
                        }
                      }}
                      onBlur={() => addAlias(field)}
                    />
                  )}
                  {current.length === 0 && !isAdmin && (
                    <span className={styles.noAliases}>None configured</span>
                  )}
                </div>
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}
