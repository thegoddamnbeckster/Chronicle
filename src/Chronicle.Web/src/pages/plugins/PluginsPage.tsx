import { useEffect, useState } from 'react'
import {
  listPlugins,
  installPlugin,
  enablePlugin,
  disablePlugin,
  uninstallPlugin,
  healthCheckPlugin,
  listCatalog,
  installFromCatalog,
  type PluginDto,
  type PluginCatalogEntry,
} from '@/api/plugins'
import { useAuth } from '@/hooks/useAuth'
import styles from './PluginsPage.module.css'

type HealthState = 'unknown' | 'checking' | true | false

export default function PluginsPage() {
  const { user } = useAuth()
  const isAdmin = user?.isAdmin ?? false

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
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { error?: { message?: string } } } })
        ?.response?.data?.error?.message
      setInstallError(msg ?? 'Installation failed. Check the DLL path and try again.')
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
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { error?: { message?: string } } } })
        ?.response?.data?.error?.message
      alert(msg ?? 'Installation failed.')
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
    } catch {
      alert('Failed to uninstall plugin.')
    } finally {
      setBusy(id, false)
    }
  }

  async function handleHealthCheck(id: number) {
    setHealthStates(prev => ({ ...prev, [id]: 'checking' }))
    try {
      const result = await healthCheckPlugin(id)
      setHealthStates(prev => ({ ...prev, [id]: result ?? 'unknown' }))
    } catch {
      setHealthStates(prev => ({ ...prev, [id]: false }))
    }
  }

  function healthLabel(state: HealthState): string {
    if (state === 'checking') return 'Checking…'
    if (state === true) return 'Healthy'
    if (state === false) return 'Unhealthy'
    return 'Test'
  }

  function healthBadgeClass(state: HealthState): string {
    if (state === true) return `${styles.badge} ${styles.healthOk}`
    if (state === false) return `${styles.badge} ${styles.healthFail}`
    return `${styles.badge} ${styles.healthUnk}`
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

      {/* ── Plugin list ────────────────────────────────────────────── */}
      {loading ? (
        <p className={styles.loading}>Loading plugins…</p>
      ) : plugins.length === 0 ? (
        <div className={styles.empty}>
          <p className={styles.emptyTitle}>No plugins installed</p>
          <p className={styles.emptyHint}>
            {isAdmin
              ? 'Click "Install Plugin" above to add a metadata provider or widget plugin.'
              : 'No plugins have been installed yet. Ask an administrator to install plugins.'}
          </p>
        </div>
      ) : (
        <div className={styles.pluginList}>
          {plugins.map(plugin => {
            const busy = busyIds.has(plugin.id)
            const health = healthStates[plugin.id] ?? 'unknown'

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
                      <span className={healthBadgeClass(health)}>
                        {health === true ? '✓ Healthy' : health === false ? '✗ Unhealthy' : 'Checking…'}
                      </span>
                    )}
                  </div>
                </div>

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
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}
