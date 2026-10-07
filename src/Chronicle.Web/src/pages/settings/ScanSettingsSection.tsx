import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getAppSettings, putAppSetting } from '@/api/settings'
import styles from './LibrarySettingsPage.module.css'

export const SCAN_SETTING_KEYS = {
  bundle: 'scan.bundle_related_files',
  mismatch: 'scan.mismatch_action',
  extensions: 'scan.sidecar_extensions',
  folders: 'scan.sidecar_folders',
} as const

const DEFAULT_EXTENSIONS = '.jpg, .jpeg, .png, .webp, .bmp, .tbn, .txt, .xml, .srt, .sub, .idx, .ass, .cue, .log'
const DEFAULT_FOLDERS = 'theme-music, theme music, .theme, .actors, extrafanart, extrathumbs, behind the scenes, behindthescenes, deleted scenes, deletedscenes, featurettes, interviews, scenes, shorts, trailers, extras'

/** Settings -> Library -> Scanning: what the nightly scan does with related files and with folders that look like the wrong type. */
export default function ScanSettingsSection() {
  const qc = useQueryClient()
  const { data: settings } = useQuery({ queryKey: ['appSettings'], queryFn: getAppSettings })
  const save = useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => putAppSetting(key, value),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['appSettings'] }),
  })

  const bundle = settings?.[SCAN_SETTING_KEYS.bundle] === 'true'
  const mismatch = settings?.[SCAN_SETTING_KEYS.mismatch] === 'ignore' ? 'ignore' : 'flag'

  const [extensions, setExtensions] = useState('')
  const [folders, setFolders] = useState('')
  useEffect(() => {
    if (!settings) return
    setExtensions(settings[SCAN_SETTING_KEYS.extensions] ?? '')
    setFolders(settings[SCAN_SETTING_KEYS.folders] ?? '')
  }, [settings])

  return (
    <section className={styles.section}>
      <div className={styles.sectionHeader}>
        <h3 className={styles.sectionTitle}>Scanning</h3>
        <p className={styles.sectionDesc}>How the nightly scan treats the extra files beside your media, and folders that look like the wrong kind of media.</p>
      </div>

      <div className={styles.sortCard}>
        <label className={styles.toggleRow}>
          <span className={styles.toggleLabel}>
            <span className={styles.toggleTitle}>Remember subtitles, artwork and extras with each item</span>
            <span className={styles.toggleDesc}>
              When on, the nightly scan records the files that live with each item (they appear under &quot;Related files&quot; on its page).
              Nothing is deleted if one goes missing; it is just marked.
            </span>
          </span>
          <button
            role="switch" aria-label="Remember related files" aria-checked={bundle}
            className={`${styles.toggle} ${bundle ? styles.toggleOn : ''}`}
            onClick={() => save.mutate({ key: SCAN_SETTING_KEYS.bundle, value: bundle ? 'false' : 'true' })}
          >
            <span className={styles.toggleThumb} />
          </button>
        </label>
      </div>

      <div className={styles.sortCard}>
        <label className={styles.toggleRow}>
          <span className={styles.toggleLabel}>
            <span className={styles.toggleTitle}>When files look like a different kind of media</span>
            <span className={styles.toggleDesc}>
              <strong>Hold back and tell me</strong> (default) skips those items in the nightly scan and adds a notice to the bell, so
              nothing lands in the wrong type. <strong>Import anyway</strong> ignores the check.
            </span>
          </span>
          <select
            aria-label="When files look like a different kind of media" className={styles.textInput} value={mismatch}
            onChange={e => save.mutate({ key: SCAN_SETTING_KEYS.mismatch, value: e.target.value })}
          >
            <option value="flag">Hold back and tell me</option>
            <option value="ignore">Import anyway</option>
          </select>
        </label>
      </div>

      <div className={styles.sortCard}>
        <div className={styles.toggleTitle}>Extra-file types</div>
        <p className={styles.toggleDesc}>
          Files with these extensions, and anything inside these folders, are treated as extras rather than media. Comma separated.
          Leave empty to use the built-in list.
        </p>
        <label className={styles.toggleTitle} htmlFor="scan-ext">Extensions</label>
        <input id="scan-ext" className={styles.textInput} value={extensions} placeholder={DEFAULT_EXTENSIONS} onChange={e => setExtensions(e.target.value)} />
        <button className={styles.saveBtn} disabled={save.isPending}
          onClick={() => save.mutate({ key: SCAN_SETTING_KEYS.extensions, value: extensions.trim() })}>Save extensions</button>
        <label className={styles.toggleTitle} htmlFor="scan-folders">Folder names</label>
        <input id="scan-folders" className={styles.textInput} value={folders} placeholder={DEFAULT_FOLDERS} onChange={e => setFolders(e.target.value)} />
        <button className={styles.saveBtn} disabled={save.isPending}
          onClick={() => save.mutate({ key: SCAN_SETTING_KEYS.folders, value: folders.trim() })}>Save folder names</button>
        <p className={styles.toggleDesc}>A change takes effect within about a minute.</p>
      </div>
    </section>
  )
}
