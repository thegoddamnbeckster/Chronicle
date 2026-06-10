import { useNavigate } from 'react-router-dom'
import { createPortal } from 'react-dom'
import { useAuthFailures } from '@/context/AuthFailureContext'
import type { PluginAuthFailure } from '@/api/plugins'
import styles from './AuthFailureBanner.module.css'

/**
 * Full-screen overlay shown when one or more plugins have authentication failures.
 * The user can navigate to the affected plugin's settings or dismiss and continue.
 */
export default function AuthFailureBanner() {
  const { failures, dismiss } = useAuthFailures()
  const navigate = useNavigate()

  if (failures.length === 0) return null

  // Show the first pending failure
  const f: PluginAuthFailure = failures[0]

  const goToSettings = () => {
    dismiss(f.pluginId)
    // Navigate to the Plugins page; if we have a dbId we can anchor to that plugin
    navigate(f.dbId != null ? `/plugins?highlight=${f.dbId}` : '/plugins')
  }

  return createPortal(
    <div className={styles.overlay}>
      <div className={styles.modal} role="dialog" aria-modal="true" aria-labelledby="auth-fail-title">
        <div className={styles.iconWrap}>
          <svg className={styles.icon} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="10" />
            <line x1="12" y1="8" x2="12" y2="12" />
            <line x1="12" y1="16" x2="12.01" y2="16" />
          </svg>
        </div>

        <h2 id="auth-fail-title" className={styles.title}>Plugin Authentication Failed</h2>

        <p className={styles.body}>
          <strong>{f.pluginName}</strong> could not log in to its upstream service.
          {failures.length > 1 && (
            <> {failures.length - 1} other plugin{failures.length > 2 ? 's' : ''} also need attention.</>
          )}
        </p>

        <p className={styles.detail}>
          Chronicle attempted to re-authenticate automatically, but the login failed.
          Please update the credentials in <strong>{f.pluginName}</strong>&rsquo;s settings.
        </p>

        <div className={styles.footer}>
          <button className={styles.cancelBtn} onClick={() => dismiss(f.pluginId)}>
            Dismiss
          </button>
          <button className={styles.settingsBtn} onClick={goToSettings}>
            Go to {f.pluginName} Settings
          </button>
        </div>

        {failures.length > 1 && (
          <p className={styles.moreNote}>
            {failures.slice(1).map(x => x.pluginName).join(', ')} will alert again when you dismiss this one.
          </p>
        )}
      </div>
    </div>,
    document.body,
  )
}
