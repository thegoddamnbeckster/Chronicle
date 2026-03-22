import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  listPlugins,
  installPlugin,
  enablePlugin,
  disablePlugin,
  uninstallPlugin,
  healthCheckPlugin,
  listCatalog,
  installFromCatalog,
  getPluginSettings,
  getPluginSettingsSchema,
  updatePluginSettings,
  SettingType,
  type PluginDto,
  type PluginCatalogEntry,
  type PluginSettingsSchema,
  type PluginHealthResult,
} from '@/api/plugins'
import { useAuth } from '@/hooks/useAuth'
import { useTheme, THEME_REGISTRY } from '@/contexts/ThemeContext'
import styles from './PluginsPage.module.css'

// ── Plugin page types ──────────────────────────────────────────────────────────

type HealthState = 'unknown' | 'checking' | PluginHealthResult

export default function PluginsPage() {
  const { user } = useAuth()
  const isAdmin = user?.isAdmin ?? false
  const { theme: activeTheme, setTheme } = useTheme()
  const queryClient = useQueryClient()

  const [plugins, setPlugins] = useState<PluginDto[]>([])
  const [loading, setLoading] = useState(true)
  const [healthStates, setHealthStates] = useState<Record<number, HealthState>>({})
  const [busyIds, setBusyIds] = useState<Set<number>>(new Set())

  // Install panel
  const [showInstall, setShowInstall] = useState(false)
  const [dllPath, setDllPath] = useState('')
  const [installing, setInstalling] = useState(false)
  const [installError, setInstallError] = useState('')

  // Browse catalog panel
  const [showBrowse, setShowBrowse] = useState(false)
  const [catalog, setCatalog] = useState<PluginCatalogEntry[]>([])
  const [catalogLoading, setCatalogLoading] = useState(false)
  const [catalogError, setCatalogError] = useState('')
  const [installingId, setInstallingId] = useState<string | null>(null)

  // Settings panel
  const [configOpenId, setConfigOpenId] = useState<number | null>(null)
  const [schema, setSchema] = useState<PluginSettingsSchema | null>(null)
  const [schemaLoading, setSchemaLoading] = useState(false)
  const [formValues, setFormValues] = useState<Record<string, string>>({})
  const [secretVisible, setSecretVisible] = useState<Record<string, boolean>>({})
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [schemaError, setSchemaError] = useState<string | null>(null)

  useEffect(() => {
    loadPlugins()
  }, [])

  async function loadPlugins() {
    setLoading(true)
    try {
      const data = await listPlugins()
      setPlugins(data)
    } catch {
      // silent — list stays empty
    } finally {
      setLoading(false)
    }
  }

  function setBusy(id: number, busy: boolean) {
    setBusyIds(prev => {
      const next = new Set(prev)
      busy ? next.add(id) : next.delete(id)
      return next
    })
  }

  async function handleInstall(e: React.FormEvent) {
    e.preventDefault()
    if (!dllPath.trim()) return
    setInstalling(true)
    setInstallError('')
    try {
      const plugin = await installPlugin(dllPath.trim())
      setPlugins(prev => [plugin, ...prev])
      setDllPath('')
      setShowInstall(false)
      // Refresh nav immediately so File Scan appears without a page reload
      void queryClient.invalidateQueries({ queryKey: ['scan-status'] })
    } catch (err: unknown) {
      setInstallError(err instanceof Error ? err.message : 'Installation failed. Check the DLL path and try again.')
    } finally {
      setInstalling(false)
    }
  }

  async function openBrowse() {
    const next = !showBrowse
    setShowInstall(false)
    setInstallError('')
    setShowBrowse(next)
    if (next && catalog.length === 0) {
      setCatalogLoading(true)
      setCatalogError('')
      try {
        const entries = await listCatalog()
        setCatalog(entries)
      } catch {
        setCatalogError('Failed to load plugin catalog. Check your connection.')
      } finally {
        setCatalogLoading(false)
      }
    }
  }

  async function handleInstallFromCatalog(pluginId: string) {
    setInstallingId(pluginId)
    try {
      const plugin = await installFromCatalog(pluginId)
      setPlugins(prev => [plugin, ...prev])
      setCatalog(prev => prev.map(e => e.pluginId === pluginId ? { ...e, isInstalled: true } : e))
      // Refresh nav immediately so File Scan appears without a page reload
      void queryClient.invalidateQueries({ queryKey: ['scan-status'] })
    } catch (err: unknown) {
      alert(err instanceof Error ? err.message : 'Installation failed.')
    } finally {
      setInstallingId(null)
    }
  }

  async function handleEnable(id: number) {
    setBusy(id, true)
    try {
      await enablePlugin(id)
      setPlugins(prev => prev.map(p => p.id === id ? { ...p, isEnabled: true } : p))
    } catch {
      alert('Failed to enable plugin.')
    } finally {
      setBusy(id, false)
    }
  }

  async function handleDisable(id: number) {
    setBusy(id, true)
    try {
      await disablePlugin(id)
      setPlugins(prev => prev.map(p => p.id === id ? { ...p, isEnabled: false } : p))
    } catch {
      alert('Failed to disable plugin.')
    } finally {
      setBusy(id, false)
    }
  }

  async function handleUninstall(id: number, name: string) {
    if (!confirm(`Uninstall "${name}"? The plugin record will be removed from the database.`)) return
    setBusy(id, true)
    try {
      await uninstallPlugin(id)
      setPlugins(prev => prev.filter(p => p.id !== id))
    } catch (err: unknown) {
      alert(err instanceof Error ? err.message : 'Failed to uninstall plugin.')
    } finally {
      setBusy(id, false)
    }
  }

  async function handleHealthCheck(id: number) {
    setHealthStates(prev => ({ ...prev, [id]: 'checking' }))
    try {
      const result = await healthCheckPlugin(id)
      setHealthStates(prev => ({ ...prev, [id]: result }))
    } catch {
      setHealthStates(prev => ({
        ...prev,
        [id]: { healthy: false, failureReason: 'Health check request failed.', isCritical: true },
      }))
    }
  }

  async function handleOpenConfig(id: number) {
    if (configOpenId === id) {
      // Toggle closed
      setConfigOpenId(null)
      setSchema(null)
      setFormValues({})
      setSaveError(null)
      setSchemaError(null)
      setSecretVisible({})
      return
    }
    setConfigOpenId(id)
    setSchema(null)
    setFormValues({})
    setSaveError(null)
    setSchemaError(null)
    setSchemaLoading(true)
    try {
      const [s, saved] = await Promise.all([
        getPluginSettingsSchema(id),
        getPluginSettings(id),
      ])
      setSchema(s)
      // Start with schema defaults, then overlay any saved values
      const values: Record<string, string> = {}
      for (const def of s.settings) {
        if (def.defaultValue !== undefined) values[def.key] = def.defaultValue
      }
      Object.assign(values, saved)
      setFormValues(values)
    } catch {
      setSchemaError('Failed to load plugin settings. Please try again.')
    } finally {
      setSchemaLoading(false)
    }
  }

  async function handleSave(pluginId: number) {
    setSaving(true)
    setSaveError(null)
    try {
      await updatePluginSettings(pluginId, formValues)
      setConfigOpenId(null)
      setSchema(null)
      setFormValues({})
      // Refresh health state so badge updates after settings change
      setHealthStates(prev => ({ ...prev, [pluginId]: 'unknown' }))
    } catch (err: unknown) {
      setSaveError(err instanceof Error ? err.message : 'Failed to save settings. Check the values and try again.')
    } finally {
      setSaving(false)
    }
  }

  function handleFieldChange(key: string, value: string) {
    setFormValues(prev => ({ ...prev, [key]: value }))
  }

  function toggleSecretVisible(key: string) {
    setSecretVisible(prev => ({ ...prev, [key]: !prev[key] }))
  }

  function healthLabel(state: HealthState): string {
    if (state === 'checking') return 'Checking…'
    if (state === 'unknown') return 'Test'
    return state.healthy ? 'Healthy' : 'Unhealthy'
  }

  function healthBadgeClass(state: HealthState): string {
    if (state === 'unknown' || state === 'checking') return `${styles.badge} ${styles.healthUnk}`
    if (state.healthy) return `${styles.badge} ${styles.healthOk}`
    if (!state.isCritical) return `${styles.badge} ${styles.healthWarn}`
    return `${styles.badge} ${styles.healthFail}`
  }

  function formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString(undefined, {
      year: 'numeric', month: 'short', day: 'numeric',
    })
  }

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1 className={styles.title}>Plugins</h1>
        {isAdmin && (
          <div className={styles.headerActions}>
            <button className={styles.browseBtn} onClick={openBrowse}>
              {showBrowse ? 'Close Catalog' : 'Browse Catalog'}
            </button>
            <button
              className={styles.installBtn}
              onClick={() => { setShowInstall(v => !v); setShowBrowse(false); setInstallError('') }}
            >
              {showInstall ? 'Cancel' : '+ Install Plugin'}
            </button>
          </div>
        )}
      </div>

      {/* ── Browse catalog panel ──────────────────────────────────── */}
      {showBrowse && isAdmin && (
        <div className={styles.browsePanel}>
          <p className={styles.installTitle}>Plugin Catalog</p>
          {catalogLoading && <p className={styles.loading}>Loading catalog…</p>}
          {catalogError && <p className={styles.errorMsg}>{catalogError}</p>}
          {!catalogLoading && !catalogError && (
            <div className={styles.catalogList}>
              {catalog.map(entry => (
                <div key={entry.pluginId} className={styles.catalogCard}>
                  <div className={styles.catalogCardLeft}>
                    {entry.iconUrl && (
                      <img
                        src={entry.iconUrl}
                        alt={`${entry.name} icon`}
                        className={styles.catalogIcon}
                        onError={e => { (e.target as HTMLImageElement).style.display = 'none' }}
                      />
                    )}
                    <div className={styles.catalogInfo}>
                      <div className={styles.catalogName}>{entry.name}</div>
                      <div className={styles.catalogDesc}>{entry.description}</div>
                      <div className={styles.catalogMeta}>by {entry.author}</div>
                      <div className={styles.catalogTags}>
                        {entry.tags.map(tag => (
                          <span key={tag} className={styles.tag}>{tag}</span>
                        ))}
                      </div>
                    </div>
                  </div>
                  <div className={styles.catalogCardRight}>
                    {entry.isInstalled ? (
                      <span className={`${styles.badge} ${styles.enabled}`}>Installed</span>
                    ) : (
                      <button
                        className={styles.catalogInstallBtn}
                        onClick={() => handleInstallFromCatalog(entry.pluginId)}
                        disabled={installingId === entry.pluginId}
                      >
                        {installingId === entry.pluginId ? 'Installing…' : 'Install'}
                      </button>
                    )}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {/* ── Install panel ─────────────────────────────────────────── */}
      {showInstall && isAdmin && (
        <div className={styles.installPanel}>
          <p className={styles.installTitle}>Install Plugin from DLL</p>
          <p className={styles.installHint}>
            Enter the absolute path to the plugin DLL on the Chronicle server.
            The DLL must be accompanied by a <code>manifest.json</code> in the same directory.
          </p>
          <form onSubmit={handleInstall}>
            <div className={styles.installRow}>
              <input
                type="text"
                className={styles.textInput}
                placeholder="e.g. C:\Chronicle\plugins\chronicle.plugin.tmdb.dll"
                value={dllPath}
                onChange={e => setDllPath(e.target.value)}
                autoFocus
              />
              <button
                type="submit"
                className={styles.submitBtn}
                disabled={installing || !dllPath.trim()}
              >
                {installing ? 'Installing…' : 'Install'}
              </button>
              <button
                type="button"
                className={styles.cancelBtn}
                onClick={() => { setShowInstall(false); setInstallError('') }}
              >
                Cancel
              </button>
            </div>
            {installError && <p className={styles.errorMsg}>{installError}</p>}
          </form>
        </div>
      )}

      {/* ── Themes section ────────────────────────────────────────── */}
      <div className={styles.section}>
        <h2 className={styles.sectionTitle}>Themes</h2>
        <div className={styles.themeGrid}>
          {THEME_REGISTRY.map(({ key, label, description, swatches }) => {
            const isActive = activeTheme === key
            return (
              <div
                key={key}
                className={`${styles.themeCard} ${isActive ? styles.themeCardActive : ''}`}
              >
                <div className={styles.themeCardLeft}>
                  <div className={styles.swatchRow}>
                    {swatches.map((color, i) => (
                      <span
                        key={i}
                        className={styles.swatch}
                        style={{ background: color }}
                      />
                    ))}
                  </div>
                  <div>
                    <div className={styles.themeName}>{label}</div>
                    <div className={styles.themeDesc}>{description}</div>
                  </div>
                </div>
                <div className={styles.themeCardRight}>
                  {isActive
                    ? <span className={`${styles.badge} ${styles.enabled}`}>Active</span>
                    : (
                      <button
                        className={styles.activateBtn}
                        onClick={() => setTheme(key)}
                      >
                        Activate
                      </button>
                    )
                  }
                </div>
              </div>
            )
          })}
        </div>
      </div>

      {/* ── Plugin list ────────────────────────────────────────────── */}
      <div className={styles.section}>
        <h2 className={styles.sectionTitle}>Installed Plugins</h2>
        {loading ? (
          <p className={styles.loading}>Loading plugins…</p>
        ) : (
          <div className={styles.pluginList}>
            {/* ── Built-in: Default Themes ─────────────────────────── */}
            <div className={styles.pluginCard}>
              <div className={styles.cardHeader}>
                <div className={styles.cardLeft}>
                  <span className={styles.pluginName}>Default Themes</span>
                  <span className={styles.versionBadge}>v1.0.0</span>
                  <span className={`${styles.badge} ${styles.builtIn}`}>Built-in</span>
                </div>
              </div>
              <div className={styles.cardMeta}>by Chronicle · built-in</div>
              <div className={styles.pluginId}>chronicle.themes.default</div>
              <p className={styles.description}>
                Provides the built-in themes: {THEME_REGISTRY.map(t => t.label).join(', ')}.
              </p>
            </div>

            {plugins.map(plugin => {
              const busy = busyIds.has(plugin.id)
              const health = healthStates[plugin.id] ?? 'unknown'

              const isConfigOpen = configOpenId === plugin.id

              return (
                <div key={plugin.id} className={styles.pluginCard}>
                  <div className={styles.cardHeader}>
                    <div className={styles.cardLeft}>
                      {plugin.iconUrl && (
                        <img
                          src={plugin.iconUrl}
                          alt={`${plugin.name} icon`}
                          className={styles.pluginIcon}
                          onError={e => { (e.target as HTMLImageElement).style.display = 'none' }}
                        />
                      )}
                      <span className={styles.pluginName}>{plugin.name}</span>
                      <span className={styles.versionBadge}>v{plugin.version}</span>
                      <span className={`${styles.badge} ${plugin.isEnabled ? styles.enabled : styles.disabled}`}>
                        {plugin.isEnabled ? 'Enabled' : 'Disabled'}
                      </span>
                      {health !== 'unknown' && (
                        <span
                          className={healthBadgeClass(health)}
                          title={
                            typeof health === 'object' && !health.healthy && health.failureReason
                              ? health.failureReason
                              : undefined
                          }
                        >
                          {health === 'checking'
                            ? 'Checking…'
                            : typeof health === 'object' && health.healthy
                              ? '✓ Healthy'
                              : '✗ Unhealthy'}
                        </span>
                      )}
                    </div>
                  </div>

                  {typeof health === 'object' && !health.healthy && health.failureReason && (
                    <div className={`${styles.healthReasonRow} ${health.isCritical ? styles.healthReasonCritical : styles.healthReasonWarn}`}>
                      {health.failureReason}
                    </div>
                  )}

                  <div className={styles.cardMeta}>
                    by {plugin.author} · installed {formatDate(plugin.installedAt)}
                  </div>
                  <div className={styles.pluginId}>{plugin.pluginId}</div>

                  {plugin.description && (
                    <p className={styles.description}>{plugin.description}</p>
                  )}

                  <div className={styles.actions}>
                    {/* Enable / Disable toggle */}
                    {isAdmin && (
                      plugin.isEnabled ? (
                        <button
                          className={`${styles.actionBtn} ${styles.disableBtn}`}
                          onClick={() => handleDisable(plugin.id)}
                          disabled={busy}
                        >
                          Disable
                        </button>
                      ) : (
                        <button
                          className={`${styles.actionBtn} ${styles.enableBtn}`}
                          onClick={() => handleEnable(plugin.id)}
                          disabled={busy}
                        >
                          Enable
                        </button>
                      )
                    )}

                    {/* Health check */}
                    {plugin.isEnabled && (
                      <button
                        className={styles.actionBtn}
                        onClick={() => handleHealthCheck(plugin.id)}
                        disabled={busy || health === 'checking'}
                      >
                        {healthLabel(health)}
                      </button>
                    )}

                    {/* Configure */}
                    {isAdmin && (
                      <button
                        className={`${styles.actionBtn} ${isConfigOpen ? styles.configBtnActive : ''}`}
                        onClick={() => handleOpenConfig(plugin.id)}
                        disabled={busy}
                      >
                        {isConfigOpen ? 'Close Settings' : 'Configure'}
                      </button>
                    )}

                    {/* Uninstall */}
                    {isAdmin && (
                      <button
                        className={`${styles.actionBtn} ${styles.uninstallBtn}`}
                        onClick={() => handleUninstall(plugin.id, plugin.name)}
                        disabled={busy}
                      >
                        Uninstall
                      </button>
                    )}
                  </div>

                  {/* ── Inline settings panel ── */}
                  {isConfigOpen && (
                    <div className={styles.settingsPanel}>
                      {schemaLoading && (
                        <p className={styles.loading}>Loading settings…</p>
                      )}

                      {!schemaLoading && schemaError && (
                        <p className={styles.errorMsg}>{schemaError}</p>
                      )}

                      {!schemaLoading && !schemaError && schema && schema.settings.length === 0 && (
                        <p className={styles.settingsEmpty}>
                          This plugin has no configurable settings.
                        </p>
                      )}

                      {!schemaLoading && !schemaError && schema && schema.settings.length > 0 && (
                        <div className={styles.settingsForm}>
                          {schema.settings.map(def => {
                            const isTmdbApiKey =
                              plugin.pluginId.toLowerCase().includes('tmdb') &&
                              def.key === 'api_key'

                            return (
                              <div key={def.key} className={styles.fieldGroup}>
                                <label className={styles.fieldLabel}>
                                  {def.label}
                                  {def.required && (
                                    <span className={styles.requiredMark}> *</span>
                                  )}
                                </label>

                                {def.type === SettingType.Boolean ? (
                                  <div className={styles.checkboxRow}>
                                    <input
                                      type="checkbox"
                                      id={`field-${plugin.id}-${def.key}`}
                                      className={styles.checkboxInput}
                                      checked={formValues[def.key] === 'true'}
                                      onChange={e =>
                                        handleFieldChange(def.key, e.target.checked ? 'true' : 'false')
                                      }
                                    />
                                    <label
                                      htmlFor={`field-${plugin.id}-${def.key}`}
                                      className={styles.checkboxLabel}
                                    >
                                      {def.description ?? def.label}
                                    </label>
                                  </div>
                                ) : def.type === SettingType.Password ? (
                                  <div className={styles.secretRow}>
                                    <input
                                      type={secretVisible[def.key] ? 'text' : 'password'}
                                      className={styles.settingsInput}
                                      value={formValues[def.key] ?? ''}
                                      onChange={e => handleFieldChange(def.key, e.target.value)}
                                      placeholder={def.required ? 'Required' : 'Optional'}
                                      autoComplete="off"
                                    />
                                    <button
                                      type="button"
                                      className={styles.showHideBtn}
                                      onClick={() => toggleSecretVisible(def.key)}
                                    >
                                      {secretVisible[def.key] ? 'Hide' : 'Show'}
                                    </button>
                                  </div>
                                ) : (
                                  <input
                                    type={def.type === SettingType.Number ? 'number' : 'text'}
                                    className={styles.settingsInput}
                                    value={formValues[def.key] ?? ''}
                                    onChange={e => handleFieldChange(def.key, e.target.value)}
                                    placeholder={def.required ? 'Required' : 'Optional'}
                                  />
                                )}

                                {def.type !== SettingType.Boolean && def.description && (
                                  <p className={styles.fieldDesc}>{def.description}</p>
                                )}

                                {isTmdbApiKey && (
                                  <p className={styles.fieldHint}>
                                    Get a free API key at{' '}
                                    <a
                                      href="https://www.themoviedb.org/settings/api"
                                      target="_blank"
                                      rel="noopener noreferrer"
                                      className={styles.fieldHintLink}
                                    >
                                      themoviedb.org/settings/api
                                    </a>
                                  </p>
                                )}
                              </div>
                            )
                          })}

                          <div className={styles.settingsActions}>
                            <button
                              className={styles.submitBtn}
                              onClick={() => handleSave(plugin.id)}
                              disabled={saving}
                            >
                              {saving ? 'Saving…' : 'Save Settings'}
                            </button>
                            <button
                              type="button"
                              className={styles.cancelBtn}
                              onClick={() => {
                                setConfigOpenId(null)
                                setSchema(null)
                                setFormValues({})
                                setSaveError(null)
                                setSchemaError(null)
                                setSecretVisible({})
                              }}
                            >
                              Cancel
                            </button>
                          </div>

                          {saveError && (
                            <p className={styles.errorMsg}>{saveError}</p>
                          )}
                        </div>
                      )}
                    </div>
                  )}
                </div>
              )
            })}
          </div>
        )}
      </div>
    </div>
  )
}
