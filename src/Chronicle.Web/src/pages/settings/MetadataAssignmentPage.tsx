import { useState, useEffect, useCallback } from 'react'
import {
  DndContext,
  closestCenter,
  PointerSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core'
import {
  arrayMove,
  SortableContext,
  useSortable,
  horizontalListSortingStrategy,
} from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import { getMetadataAssignment, putMetadataAssignment, putPluginDisplayOrder, type MetadataAssignmentConfig, type PluginInfo } from '@/api/settings'
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

// ── Sortable chip ────────────────────────────────────────────────────────────

interface SortableChipProps {
  id: string
  plugin: PluginInfo
  disabled: boolean
}

function SortableChip({ id, plugin, disabled }: SortableChipProps) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } =
    useSortable({ id, disabled })

  const style: React.CSSProperties = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.5 : 1,
    zIndex: isDragging ? 9999 : undefined,
  }

  return (
    <div ref={setNodeRef} style={style} className={styles.chip}>
      <span
        className={`${styles.gripHandle} ${disabled ? styles.gripDisabled : ''}`}
        {...(disabled ? {} : { ...attributes, ...listeners })}
        title={disabled ? 'Admin access required' : 'Drag to reorder'}
      >
        ⠿
      </span>
      {plugin.iconUrl && (
        <img
          src={plugin.iconUrl}
          alt=""
          className={styles.chipIcon}
          onError={e => { e.currentTarget.style.display = 'none' }}
        />
      )}
      <span className={styles.chipName}>{plugin.name}</span>
    </div>
  )
}

// ── Sortable list (used for both field rows and the default priority box) ────

interface SortableListProps {
  id: string          // unique key for DndContext (prevents cross-list interference)
  order: string[]
  plugins: PluginInfo[]
  disabled: boolean
  onReorder: (newOrder: string[]) => void
}

function SortableList({ id, order, plugins, disabled, onReorder }: SortableListProps) {
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }))

  function handleDragEnd(event: DragEndEvent) {
    const { active, over } = event
    if (!over || active.id === over.id) return
    const oldIdx = order.indexOf(String(active.id))
    const newIdx = order.indexOf(String(over.id))
    onReorder(arrayMove(order, oldIdx, newIdx))
  }

  const visibleOrder = order.filter(id => plugins.some(p => p.pluginId === id))

  return (
    <DndContext id={id} sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
      <SortableContext items={visibleOrder} strategy={horizontalListSortingStrategy}>
        <div className={styles.chipRow}>
          {visibleOrder.map(pluginId => {
            const plugin = plugins.find(p => p.pluginId === pluginId)
            if (!plugin) return null
            return <SortableChip key={pluginId} id={pluginId} plugin={plugin} disabled={disabled} />
          })}
        </div>
      </SortableContext>
    </DndContext>
  )
}

// ── Main page ────────────────────────────────────────────────────────────────

export default function MetadataAssignmentPage() {
  const { user }                              = useAuth()
  const isAdmin                               = user?.isAdmin ?? false
  const [config, setConfig]                   = useState<MetadataAssignmentConfig | null>(null)
  const [assignments, setAssignments]         = useState<Record<string, Record<string, string[]>>>({})
  // Staged default orders per media type (shown in the default box, not yet applied)
  const [defaultOrders, setDefaultOrders]     = useState<Record<string, string[]>>({})
  const [saving, setSaving]                   = useState(false)
  const [saved, setSaved]                     = useState(false)
  const [error, setError]                     = useState<string | null>(null)
  const FOLD_KEY = 'chronicle_metadataAssignment_folds'
  const [openSections, setOpenSections]       = useState<Record<string, boolean>>(() => {
    try { return JSON.parse(localStorage.getItem(FOLD_KEY) ?? '{}') } catch { return {} }
  })

  useEffect(() => {
    getMetadataAssignment()
      .then(cfg => {
        setConfig(cfg)
        setAssignments(cfg.assignments)
        const defaults: Record<string, boolean> = {}
        const orders: Record<string, string[]>  = {}
        for (const mt of Object.keys(cfg.assignableFields)) {
          defaults[mt] = true
          // Use the saved display order if present; otherwise fall back to available plugins order
          if (cfg.displayOrder?.[mt]?.length) {
            // Merge: saved IDs first (in saved order), then any new plugins not yet in the order
            const saved    = cfg.displayOrder[mt]
            const available = cfg.availablePlugins[mt]?.map(p => p.pluginId) ?? []
            const merged   = [...saved.filter(id => available.includes(id)),
                              ...available.filter(id => !saved.includes(id))]
            orders[mt] = merged
          } else {
            orders[mt] = cfg.availablePlugins[mt]?.map(p => p.pluginId) ?? []
          }
        }
        // Merge: use saved fold state if present, default new sections to open
        setOpenSections(prev => {
          const merged = { ...defaults, ...prev }
          localStorage.setItem(FOLD_KEY, JSON.stringify(merged))
          return merged
        })
        setDefaultOrders(orders)
      })
      .catch(e => setError(String(e)))
  }, [])

  function toggleSection(mediaType: string) {
    setOpenSections(prev => {
      const next = { ...prev, [mediaType]: !prev[mediaType] }
      localStorage.setItem(FOLD_KEY, JSON.stringify(next))
      return next
    })
  }

  const save = useCallback(async (next: Record<string, Record<string, string[]>>) => {
    setSaving(true)
    setError(null)
    try {
      await putMetadataAssignment(next)
      setSaved(true)
      setTimeout(() => setSaved(false), 2000)
    } catch (e) {
      setError(String(e))
    } finally {
      setSaving(false)
    }
  }, [])

  function handleFieldReorder(mediaType: string, field: string, newOrder: string[]) {
    const next = { ...assignments, [mediaType]: { ...(assignments[mediaType] ?? {}), [field]: newOrder } }
    setAssignments(next)
    save(next)
  }

  function handleDefaultReorder(mediaType: string, newOrder: string[]) {
    const next = { ...defaultOrders, [mediaType]: newOrder }
    setDefaultOrders(next)
    // Auto-save the display order immediately (independent of field assignments)
    putPluginDisplayOrder(next).catch(e => setError(String(e)))
  }

  function applyDefaultToAll(mediaType: string) {
    if (!config) return
    const order = defaultOrders[mediaType] ?? []
    const fieldAssignments: Record<string, string[]> = {}
    for (const field of config.assignableFields[mediaType] ?? []) {
      // Only include a plugin for this field if it actually declares support for it.
      // Plugins with no entry in fieldPlugins for this field (e.g. artwork-only plugins
      // like Fanart.tv) are excluded from text/metadata fields automatically.
      const supportedPluginIds = config.fieldPlugins?.[mediaType]?.[field]
      const filtered = supportedPluginIds != null
        ? order.filter(id => supportedPluginIds.includes(id))
        : order
      fieldAssignments[field] = filtered
    }
    const next = { ...assignments, [mediaType]: fieldAssignments }
    setAssignments(next)
    save(next)
    // Keep display order in sync — if the user says "apply this order to all fields",
    // the detail page should also show plugins in that same order.
    putPluginDisplayOrder({ ...defaultOrders, [mediaType]: order }).catch(e => setError(String(e)))
  }

  if (!config) return (
    <div className={styles.page}>
      {error ? <p className={styles.error}>{error}</p> : <p>Loading…</p>}
    </div>
  )

  const mediaTypes = Object.keys(config.assignableFields)

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1 className={styles.title}>Metadata Assignment</h1>
        <p className={styles.subtitle}>
          Drag plugins to set priority order. The first plugin in each row is the primary source;
          the rest are fallbacks.
          {!isAdmin && <span className={styles.readOnlyNote}> (read-only — admin access required)</span>}
        </p>
        {saving && <span className={styles.saveStatus}>Saving…</span>}
        {!saving && saved && <span className={`${styles.saveStatus} ${styles.saveStatusOk}`}>Saved ✓</span>}
        {error && <p className={styles.error}>{error}</p>}
      </div>

      {mediaTypes.map(mediaType => {
        const plugins: PluginInfo[] = config.availablePlugins[mediaType] ?? []
        const isOpen      = openSections[mediaType] ?? true
        const isChild     = mediaType.includes('.')
        const displayName = config.mediaTypeDisplayNames?.[mediaType] ?? mediaType
        const defaultOrder = defaultOrders[mediaType] ?? plugins.map(p => p.pluginId)

        return (
          <section key={mediaType} className={`${styles.section} ${isChild ? styles.sectionChild : ''}`}>
            <button
              className={`${styles.sectionHeader} ${isChild ? styles.sectionHeaderChild : ''}`}
              onClick={() => toggleSection(mediaType)}
              aria-expanded={isOpen}
            >
              {isChild && <span className={styles.hierarchyConnector}>└</span>}
              <h2 className={`${styles.sectionTitle} ${isChild ? styles.sectionTitleChild : ''}`}>
                {displayName}
              </h2>
              <span className={`${styles.chevron} ${isOpen ? styles.chevronOpen : ''}`}>›</span>
            </button>

            {isOpen && (
              plugins.length === 0 ? (
                <p className={styles.noPlugins}>No installed plugins support this level.</p>
              ) : (
                <div className={styles.tableWrap}>

                  {/* ── Display order / default priority box ── */}
                  <div className={styles.defaultBox}>
                    <div className={styles.defaultBoxLeft}>
                      <span className={styles.defaultBoxLabel}>Display Order</span>
                      <span className={styles.defaultBoxHint}>
                        Controls the order of plugin boxes on the media detail page.
                        Drag to reorder, then optionally apply as default field priority below.
                      </span>
                    </div>
                    <SortableList
                      id={`default-${mediaType}`}
                      order={defaultOrder}
                      plugins={plugins}
                      disabled={!isAdmin || saving}
                      onReorder={order => handleDefaultReorder(mediaType, order)}
                    />
                    <button
                      className={styles.applyBtn}
                      onClick={() => applyDefaultToAll(mediaType)}
                      disabled={!isAdmin || saving}
                      title={!isAdmin ? 'Admin access required' : 'Apply this order to every field below'}
                    >
                      Apply to all fields
                    </button>
                  </div>

                  {/* ── Per-field rows ── */}
                  <div className={styles.tableHead}>
                    <div className={styles.colField}>Field</div>
                    <div className={styles.colPlugins}>Plugin Priority</div>
                  </div>

                  {config.assignableFields[mediaType].map(field => {
                    // Restrict to plugins that declare support for this specific field.
                    // If the server returned no fieldPlugins entry, fall back to all plugins
                    // (handles old API responses gracefully).
                    const supportedIds = config.fieldPlugins?.[mediaType]?.[field]
                    const fieldPluginList = supportedIds != null
                      ? plugins.filter(p => supportedIds.includes(p.pluginId))
                      : plugins
                    const defaultPluginOrder = fieldPluginList.map(p => p.pluginId)
                    const currentOrder = (assignments[mediaType]?.[field] ?? defaultPluginOrder)
                      .filter(id => fieldPluginList.some(p => p.pluginId === id))

                    return (
                      <div key={field} className={styles.row}>
                        <div className={styles.colField}>{FIELD_LABELS[field] ?? field}</div>
                        <div className={styles.colPlugins}>
                          <SortableList
                            id={`${mediaType}-${field}`}
                            order={currentOrder}
                            plugins={fieldPluginList}
                            disabled={!isAdmin || saving}
                            onReorder={order => handleFieldReorder(mediaType, field, order)}
                          />
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
