import { useState, useEffect } from 'react'
import { getMetadataAssignment, putMetadataAssignment, type MetadataAssignmentConfig, type PluginInfo } from '@/api/settings'
import { useAuth } from '@/hooks/useAuth'
import styles from './MetadataAssignmentPage.module.css'

const FIELD_LABELS: Record<string, string> = {
  title:           'Title',
  overview:        'Description',
  year:            'Year',
  poster_url:      'Poster Image',
  backdrop_url:    'Backdrop Image',
  runtime_minutes: 'Runtime',
  rating:          'Rating',
  genres:          'Genres',
  cast:            'Cast',
  directors:       'Directors',
  tags:            'Tags',
}

export default function MetadataAssignmentPage() {
  const { user }                      = useAuth()
  const isAdmin                       = user?.isAdmin ?? false
  const [config, setConfig]           = useState<MetadataAssignmentConfig | null>(null)
  const [assignments, setAssignments] = useState<Record<string, Record<string, string[]>>>({})
  const [saving, setSaving]           = useState(false)
  const [saved, setSaved]             = useState(false)
  const [error, setError]             = useState<string | null>(null)
  const [openSections, setOpenSections] = useState<Record<string, boolean>>({})

  useEffect(() => {
    getMetadataAssignment()
      .then(cfg => {
        setConfig(cfg)
        setAssignments(cfg.assignments)
        // Default all sections open
        const defaults: Record<string, boolean> = {}
        for (const mt of Object.keys(cfg.assignableFields)) {
          defaults[mt] = true
        }
        setOpenSections(defaults)
      })
      .catch(e => setError(String(e)))
  }, [])

  function toggleSection(mediaType: string) {
    setOpenSections(prev => ({ ...prev, [mediaType]: !prev[mediaType] }))
  }

  function movePlugin(mediaType: string, field: string, pluginId: string, direction: 'up' | 'down') {
    setAssignments(prev => {
      const defaultOrder = config?.availablePlugins[mediaType]?.map(p => p.pluginId) ?? []
      const list = [...(prev[mediaType]?.[field] ?? defaultOrder)]
      const idx  = list.indexOf(pluginId)
      if (idx === -1) return prev
      const swapIdx = direction === 'up' ? idx - 1 : idx + 1
      if (swapIdx < 0 || swapIdx >= list.length) return prev
      ;[list[idx], list[swapIdx]] = [list[swapIdx], list[idx]]
      return { ...prev, [mediaType]: { ...(prev[mediaType] ?? {}), [field]: list } }
    })
  }

  async function handleSave() {
    setSaving(true)
    setError(null)
    try {
      await putMetadataAssignment(assignments)
      setSaved(true)
      setTimeout(() => setSaved(false), 2000)
    } catch (e) {
      setError(String(e))
    } finally {
      setSaving(false)
    }
  }

  if (!config) return (
    <div className={styles.page}>
      {error ? <p className={styles.error}>{error}</p> : <p>Loading…</p>}
    </div>
  )

  // Show all active media types; types with no plugin support will show an empty state
  const mediaTypes = Object.keys(config.assignableFields)

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1 className={styles.title}>Metadata Assignment</h1>
        <p className={styles.subtitle}>
          Control which plugin's data is used for each field. The first plugin in each list is the
          primary source; the rest are fallbacks in order.
        </p>
        <button
          className={styles.saveBtn}
          onClick={handleSave}
          disabled={saving || !isAdmin}
          title={!isAdmin ? 'Admin access required to change metadata assignment' : undefined}
        >
          {saving ? 'Saving…' : saved ? 'Saved ✓' : 'Save Changes'}
        </button>
        {error && <p className={styles.error}>{error}</p>}
      </div>

      {mediaTypes.map(mediaType => {
        const plugins: PluginInfo[] = config.availablePlugins[mediaType] ?? []
        const isOpen = openSections[mediaType] ?? true

        return (
          <section key={mediaType} className={styles.section}>
            <button
              className={styles.sectionHeader}
              onClick={() => toggleSection(mediaType)}
              aria-expanded={isOpen}
            >
              <h2 className={styles.sectionTitle}>
                {config.mediaTypeDisplayNames[mediaType] ?? mediaType}
              </h2>
              <span className={`${styles.chevron} ${isOpen ? styles.chevronOpen : ''}`}>›</span>
            </button>

            {isOpen && (
              plugins.length === 0 ? (
                <p className={styles.noPlugins}>No installed plugins support this media type.</p>
              ) : (
              <div className={styles.table}>
                <div className={styles.tableHead}>
                  <div className={styles.colField}>Field</div>
                  <div className={styles.colPlugins}>Plugin Priority</div>
                </div>

                {config.assignableFields[mediaType].map(field => {
                  const defaultOrder = plugins.map(p => p.pluginId)
                  const currentOrder = assignments[mediaType]?.[field] ?? defaultOrder

                  return (
                    <div key={field} className={styles.row}>
                      <div className={styles.colField}>{FIELD_LABELS[field] ?? field}</div>
                      <div className={styles.colPlugins}>
                        {currentOrder.map((pluginId, idx) => {
                          const plugin = plugins.find(p => p.pluginId === pluginId)
                          if (!plugin) return null
                          return (
                            <div key={pluginId} className={styles.pluginRow}>
                              {plugin.iconUrl && (
                                <img
                                  src={plugin.iconUrl}
                                  alt=""
                                  className={styles.pluginIcon}
                                  onError={e => { e.currentTarget.style.display = 'none' }}
                                />
                              )}
                              <span className={styles.pluginName}>{plugin.name}</span>
                              <div className={styles.arrows}>
                                <button
                                  className={styles.arrowBtn}
                                  onClick={() => movePlugin(mediaType, field, pluginId, 'up')}
                                  disabled={idx === 0}
                                  title="Move up (higher priority)"
                                >↑</button>
                                <button
                                  className={styles.arrowBtn}
                                  onClick={() => movePlugin(mediaType, field, pluginId, 'down')}
                                  disabled={idx === currentOrder.length - 1}
                                  title="Move down (lower priority)"
                                >↓</button>
                              </div>
                            </div>
                          )
                        })}
                      </div>
                    </div>
                  )
                })}
              </div>
              )
            )}
          </section>
        )
      })}
    </div>
  )
}
