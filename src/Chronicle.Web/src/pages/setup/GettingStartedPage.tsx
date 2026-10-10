import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  SettingType, getPluginSettings, getPluginSettingsSchema, healthCheckPlugin, installFromCatalog, listCatalog, listPlugins,
  updatePluginSettings, type PluginCatalogEntry, type PluginDto, type PluginHealthResult,
} from '@/api/plugins'
import { getMediaTypes } from '@/api/media'
import styles from './GettingStartedPage.module.css'

/** What a catalog plugin does for you, worked out from the tags its catalog entry carries. */
export type PluginRole = 'scanner' | 'metadata' | 'tracking' | 'appearance' | 'other'

export function rolesOf(entry: Pick<PluginCatalogEntry, 'tags'>): PluginRole[] {
  const tags = new Set(entry.tags.map(t => t.toLowerCase()))
  const roles: PluginRole[] = []
  if (tags.has('filescanner')) roles.push('scanner')
  if (tags.has('metadata') || tags.has('artwork') || tags.has('ratings')) roles.push('metadata')
  if (tags.has('scrobbling') || tags.has('sync')) roles.push('tracking')
  if (tags.has('themes') || tags.has('ui')) roles.push('appearance')
  return roles.length > 0 ? roles : ['other']
}

function PluginCard({ entry, onInstalled }: { entry: PluginCatalogEntry; onInstalled: () => void }) {
  const install = useMutation({
    mutationFn: () => installFromCatalog(entry.pluginId),
    onSuccess: onInstalled,
  })
  return (
    <li className={styles.card}>
      <div className={styles.cardText}>
        <strong>{entry.name}</strong>
        <span className={styles.version}>v{entry.version}</span>
        <p>{entry.description}</p>
        {install.isError && <p className={styles.error} role="alert">{(install.error as Error).message}</p>}
      </div>
      {entry.isInstalled || install.isSuccess
        ? <span className={styles.done}>Installed</span>
        : <button className={styles.primary} onClick={() => install.mutate()} disabled={install.isPending}>
            {install.isPending ? 'Installing…' : `Install ${entry.name}`}
          </button>}
    </li>
  )
}

function Section({ step, title, children }: { step: number; title: string; children: React.ReactNode }) {
  return (
    <section className={styles.section} aria-labelledby={`step-${step}`}>
      <h2 id={`step-${step}`} className={styles.stepTitle}><span className={styles.stepNumber}>{step}</span>{title}</h2>
      {children}
    </section>
  )
}

/** The settings a plugin cannot work without, as a small form. Shown only while something required is still empty. */
function KeyForm({ plugin }: { plugin: PluginDto }) {
  const qc = useQueryClient()
  const { data } = useQuery({
    queryKey: ['setup', 'settings', plugin.id],
    queryFn: async () => {
      const [schema, saved] = await Promise.all([getPluginSettingsSchema(plugin.id), getPluginSettings(plugin.id)])
      return { schema, saved }
    },
  })
  const [values, setValues] = useState<Record<string, string>>({})
  const [health, setHealth] = useState<PluginHealthResult | null>(null)

  const required = (data?.schema.settings ?? []).filter(s => s.required && s.type !== SettingType.Notice)
  const missing = required.filter(s => !(data?.saved[s.key] ?? s.defaultValue ?? '').trim())

  const save = useMutation({
    mutationFn: async () => {
      await updatePluginSettings(plugin.id, { ...(data?.saved ?? {}), ...values })
      return healthCheckPlugin(plugin.id)
    },
    onSuccess: async result => {
      setHealth(result)
      await qc.invalidateQueries({ queryKey: ['setup', 'settings', plugin.id] })
    },
  })

  if (!data || (missing.length === 0 && !health)) return null
  return (
    <li className={styles.card}>
      <div className={styles.cardText}>
        <strong>{plugin.name}</strong>
        {missing.length > 0
          ? <p>{plugin.name} needs {missing.length === 1 ? 'one more thing' : `${missing.length} more things`} before it can work.</p>
          : <p>Saved.</p>}
        {missing.map(s => (
          <label key={s.key} className={styles.field}>
            <span>{s.label}</span>
            {s.description && <small>{s.description}</small>}
            <input
              type={s.type === SettingType.Password ? 'password' : 'text'}
              value={values[s.key] ?? ''}
              onChange={e => setValues(v => ({ ...v, [s.key]: e.target.value }))}
              autoComplete="off"
            />
          </label>
        ))}
        {health && (
          <p className={health.healthy ? styles.done : styles.error} role="status">
            {health.healthy ? `${plugin.name} is working.` : `${plugin.name} did not pass its check${health.failureReason ? `: ${health.failureReason}` : '.'}`}
          </p>
        )}
        {save.isError && <p className={styles.error} role="alert">{(save.error as Error).message}</p>}
      </div>
      {missing.length > 0 && (
        <button className={styles.primary} onClick={() => save.mutate()}
          disabled={save.isPending || missing.some(s => !(values[s.key] ?? '').trim())}>
          {save.isPending ? 'Saving…' : 'Save and test'}
        </button>
      )}
    </li>
  )
}

export default function GettingStartedPage() {
  const qc = useQueryClient()
  const { data: types = [] } = useQuery({ queryKey: ['media-types'], queryFn: getMediaTypes })
  const { data: installed = [] } = useQuery({ queryKey: ['plugins'], queryFn: listPlugins })
  const [chosen, setChosen] = useState<Set<string> | null>(null)

  // Until the person chooses, everything they could keep is shown.
  const selected = chosen ?? new Set(types.map(t => t.name))

  const catalogs = useQueries({
    queries: types.filter(t => selected.has(t.name)).map(t => ({
      queryKey: ['setup', 'catalog', t.name],
      queryFn: () => listCatalog(t.name),
    })),
  })
  const loading = catalogs.some(c => c.isLoading)
  const failed = catalogs.some(c => c.isError)

  // The union of what each chosen type can use. A scanner is useful whatever you chose.
  const entries = useMemo(() => {
    const byId = new Map<string, PluginCatalogEntry>()
    for (const c of catalogs) for (const e of c.data ?? []) byId.set(e.pluginId, e)
    return [...byId.values()].sort((a, b) => a.name.localeCompare(b.name))
  }, [catalogs])

  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['setup', 'catalog'] })
    void qc.invalidateQueries({ queryKey: ['plugins'] })
  }
  const withRole = (role: PluginRole) => entries.filter(e => rolesOf(e).includes(role))
  const scanners = withRole('scanner')
  const metadata = withRole('metadata').filter(e => !rolesOf(e).includes('scanner'))
  const tracking = withRole('tracking')
  const appearance = withRole('appearance')

  const toggle = (name: string) => {
    const next = new Set(selected)
    if (next.has(name)) next.delete(name); else next.add(name)
    setChosen(next)
  }

  const needsKeys = installed.filter(p => p.isEnabled)

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Getting started</h1>
      <p className={styles.lead}>
        Chronicle ships with no plugins. Plugins are what read your folders and look up information about what you have, so
        this walks you through choosing and setting up the ones you want. It takes a few minutes, you can leave and come back,
        and you can always add more later from <Link to="/plugins">Plugins</Link>.
      </p>

      <Section step={1} title="What do you keep?">
        <p>Tick the kinds of media you have. Chronicle will suggest plugins that handle them.</p>
        <div className={styles.types}>
          {types.map(t => (
            <label key={t.id} className={styles.type}>
              <input type="checkbox" checked={selected.has(t.name)} onChange={() => toggle(t.name)} />
              {t.displayName}
            </label>
          ))}
        </div>
      </Section>

      {failed && <p className={styles.error} role="alert">The plugin catalog could not be read. Check your internet connection and reload.</p>}
      {loading && <p>Loading the plugin catalog…</p>}

      <Section step={2} title="Let Chronicle find your files">
        <p>A file scanner reads your folders and works out what is in them. You need one to add anything from disk.</p>
        <ul className={styles.list}>
          {scanners.map(e => <PluginCard key={e.pluginId} entry={e} onInstalled={refresh} />)}
          {scanners.length === 0 && !loading && <li>No file scanner is listed in the catalog right now.</li>}
        </ul>
      </Section>

      <Section step={3} title="Look up information">
        <p>These fetch titles, descriptions, artwork and cast. Pick at least one for each kind of media you keep; several work together.</p>
        <ul className={styles.list}>
          {metadata.map(e => <PluginCard key={e.pluginId} entry={e} onInstalled={refresh} />)}
          {metadata.length === 0 && !loading && <li>Nothing in the catalog matches the kinds of media you ticked.</li>}
        </ul>
      </Section>

      {(tracking.length > 0 || appearance.length > 0) && (
        <Section step={4} title="Optional extras">
          {tracking.length > 0 && (
            <details>
              <summary>Keep your history in sync with other services</summary>
              <ul className={styles.list}>{tracking.map(e => <PluginCard key={e.pluginId} entry={e} onInstalled={refresh} />)}</ul>
            </details>
          )}
          {appearance.length > 0 && (
            <details>
              <summary>Change how Chronicle looks</summary>
              <ul className={styles.list}>{appearance.map(e => <PluginCard key={e.pluginId} entry={e} onInstalled={refresh} />)}</ul>
            </details>
          )}
        </Section>
      )}

      <Section step={5} title="Give them what they need">
        <p>Some services want an account key before they will answer. Anything installed that still needs one appears here.</p>
        <ul className={styles.list}>
          {needsKeys.map(p => <KeyForm key={p.id} plugin={p} />)}
        </ul>
        <p className={styles.hint}>Nothing listed above means everything you installed is ready.</p>
      </Section>

      <Section step={6} title="Point Chronicle at your folders">
        <p>
          Last step: tell Chronicle where your media lives. On the <Link to="/scan">Scan</Link> page, add your folders, choose
          &quot;Detect automatically&quot; if a folder mixes movies, shows and music, and import what it finds.
        </p>
        <Link className={styles.primary} to="/scan">Go to Scan</Link>
      </Section>
    </div>
  )
}
