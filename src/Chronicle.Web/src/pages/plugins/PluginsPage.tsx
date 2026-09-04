import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  getImportProviders,
  startAuth,
  pollAuth,
  getAuthStatus,
} from '@/api/import'
import type { ImportProvider, ImportAuthStart } from '@/types'
import {
  listPlugins,
  installPlugin,
  enablePlugin,
  disablePlugin,
  uninstallPlugin,
  healthCheckPlugin,
  listCatalog,
  installFromCatalog,
  updatePluginFromCatalog,
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
import { useTheme } from '@/contexts/ThemeContext'
import styles from './PluginsPage.module.css'

// ── Plugin page types ──────────────────────────────────────────────────────────

type HealthState = 'unknown' | 'checking' | PluginHealthResult

// ── Inline import/auth section ────────────────────────────────────────────────

function InlineImportSection({ provider }: { provider: ImportProvider }) {
  const [authenticated, setAuthenticated] = useState<boolean | null>(null)
  const [authFlow, setAuthFlow]           = useState<ImportAuthStart | null>(null)
  const [polling,   setPolling]           = useState(false)
  const [pollError, setPollError]         = useState<string | null>(null)
  const [starting,  setStarting]          = useState(false)
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null)

  useEffect(() => {
    void checkAuth()
    return () => {
      if (intervalRef.current) {
        clearInterval(intervalRef.current)
        intervalRef.current = null
      }
    }
  }, [provider.pluginId])

  async function checkAuth() {
    try {
      setAuthenticated(await getAuthStatus(provider.pluginId))
    } catch {
      setAuthenticated(false)
    }
  }

  async function handleConnect() {
    // Cancel any in-progress polling before starting fresh
    if (intervalRef.current) { clearInterval(intervalRef.current); intervalRef.current = null }
    setAuthFlow(null)
    setPolling(false)
    setPollError(null)
    setStarting(true)
    try {
      const data = await startAuth(provider.pluginId)
      setAuthFlow(data)
      startPollLoop(data.pollCode, data.pollingIntervalSeconds)
    } catch (err) {
      setPollError(err instanceof Error ? err.message : 'Failed to start auth')
    } finally {
      setStarting(false)
    }
  }

  function startPollLoop(pollCode: string, intervalSec: number) {
    setPolling(true)
    intervalRef.current = setInterval(async () => {
      try {
        const result = await pollAuth(provider.pluginId, pollCode)
        if (result.status !== 'pending') {
          if (intervalRef.current) { clearInterval(intervalRef.current); intervalRef.current = null }
          setPolling(false)
          if (result.status === 'authorized') {
            setAuthFlow(null)
            setAuthenticated(true)
          } else {
            setAuthFlow(null)
            setPollError(result.errorMessage ?? `Authorization ${result.status}. Click "Connect Account" to try again.`)
          }
        }
      } catch {
        if (intervalRef.current) { clearInterval(intervalRef.current); intervalRef.current = null }
        setPolling(false)
        setAuthFlow(null)
        setPollError('Polling failed — click "Connect Account" to try again.')
      }
    }, intervalSec * 1000)
  }

  if (authenticated === null) return null

  return (
    <div className={styles.importSection}>
      <div className={styles.importSectionTitle}>Account Connection</div>

      {authenticated ? (
        <>
          <div className={styles.connectedRow}>
            <span className={styles.connectedDot} />
            Connected
          </div>
          <p className={styles.syncHint}>
            Sync controls are available on the{' '}
            <a href="/settings/background-tasks" className={styles.pinLink}>Background Tasks</a>{' '}
            page.
          </p>
        </>
      ) : authFlow ? (
        <div className={styles.pinFlow}>
          <p className={styles.pinInstr}>
            Go to{' '}
            <a href={authFlow.verificationUrl} target="_blank" rel="noopener noreferrer"
              className={styles.pinLink}>
              {authFlow.verificationUrl}
            </a>{' '}
            and enter this PIN:
          </p>
          <div className={styles.pinCode}>{authFlow.userCode}</div>
          {polling   && <p className={styles.pinPolling}>Waiting for authorization…</p>}
          {pollError && <p className={styles.pinError}>{pollError}</p>}
          <button type="button" className={styles.newPinBtn}
            onClick={handleConnect} disabled={starting}>
            {starting ? 'Getting new PIN…' : '↺ Get New PIN'}
          </button>
        </div>
      ) : (
        <div>
          {pollError && <p className={styles.pinError}>{pollError}</p>}
          <button type="button" className={styles.connectBtn}
            onClick={handleConnect} disabled={starting}>
            {starting ? 'Starting…' : 'Connect Account'}
          </button>
          <p className={styles.pinHint}>Save your Client ID above before connecting.</p>
        </div>
      )}
    </div>
  )
}

export default function PluginsPage() {
  const { user } = useAuth()
  const isAdmin = user?.isAdmin ?? false
  const { themes: availableThemes, activeKey: activeTheme, setTheme, loading: themesLoading } = useTheme()
  const queryClient = useQueryClient()

  const [plugins, setPlugins]               = useState<PluginDto[]>([])
  const [loading, setLoading]               = useState(true)
  const [importProviders, setImportProviders] = useState<ImportProvider[]>([])
  const [healthStates, setHealthStates]     = useState<Record<number, HealthState>>({})
  const [busyIds, setBusyIds]               = useState<Set<number>>(new Set())
  const [savedPluginId, setSavedPluginId]   = useState<number | null>(null)

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
      const [pluginData, importData] = await Promise.allSettled([
        listPlugins(),
        getImportProviders(),
      ])
      if (pluginData.status === 'fulfilled')  setPlugins(pluginData.value)
      if (importData.status === 'fulfilled')  setImportProviders(importData.value)
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

  async function handleUpdate(pluginId: string, dbId: number) {
    setBusy(dbId, true)
    try {
      const updated = await updatePluginFromCatalog(pluginId)
      setPlugins(prev => prev.map(p => p.id === dbId ? updated : p))
    } catch (err: unknown) {
      alert(err instanceof Error ? err.message : 'Failed to update plugin.')
    } finally {
      setBusy(dbId, false)
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
      // Keep the panel open so import providers can proceed to Connect immediately
      setSavedPluginId(pluginId)
      setTimeout(() => setSavedPluginId(prev => prev === pluginId ? null : prev), 3000)
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
                      <div className={styles.catalogName}>
                        {entry.name}
                        {entry.version && (
                          <span className={styles.catalogVersion}>v{entry.version}</span>
                        )}
                      </div>
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
          {themesLoading && (
            <p className={styles.loading}>Loading themes…</p>
          )}
          {availableThemes.map((t) => {
            const storageKey = `${t.pluginId}:${t.key}`
            const isActive = activeTheme === storageKey
            return (
              <div
                key={storageKey}
                className={`${styles.themeCard} ${isActive ? styles.themeCardActive : ''}`}
              >
                <div className={styles.themeCardLeft}>
                  <div className={styles.swatchRow}>
                    {t.swatches.map((color, i) => (
                      <span
                        key={i}
                        className={styles.swatch}
                        style={{ background: color }}
                      />
                    ))}
                  </div>
                  <div>
                    <div className={styles.themeName}>{t.label}</div>
                    <div className={styles.themeDesc}>{t.description}</div>
                  </div>
                </div>
                <div className={styles.themeCardRight}>
                  {isActive
                    ? <span className={`${styles.badge} ${styles.enabled}`}>Active</span>
                    : (
                      <button
                        className={styles.activateBtn}
                        onClick={() => setTheme(storageKey)}
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
                      {plugin.latestVersionAvailable && (
                        <span
                          className={styles.badge}
                          title={`Checked ${plugin.updateCheckedAt ? formatDate(plugin.updateCheckedAt) : 'recently'}`}
                        >
                          Update available: v{plugin.latestVersionAvailable}
                        </span>
                      )}
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

                    {/* Update available */}
                    {isAdmin && plugin.latestVersionAvailable && (
                      <button
                        className={`${styles.actionBtn} ${styles.enableBtn}`}
                        onClick={() => handleUpdate(plugin.pluginId, plugin.id)}
                        disabled={busy}
                      >
                        Update to v{plugin.latestVersionAvailable}
                      </button>
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
                                setSavedPluginId(null)
                              }}
                            >
                              Cancel
                            </button>
                            {savedPluginId === plugin.id && (
                              <span className={styles.savedMsg}>✓ Saved</span>
                            )}
                          </div>

                          {saveError && (
                            <p className={styles.errorMsg}>{saveError}</p>
                          )}

                          {/* Inline connect + import for import providers */}
                          {(() => {
                            const ip = importProviders.find(
                              p => p.pluginId === plugin.pluginId && p.requiresDeviceAuth
                            )
                            return ip ? <InlineImportSection provider={ip} /> : null
                          })()}
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
