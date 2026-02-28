import { useEffect, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { getDeviceAuthInfo, approveDevice, denyDevice, type DeviceAuthInfoDto } from '@/api/deviceAuth'
import styles from './DeviceAuthPage.module.css'

type PageState = 'loading' | 'login-required' | 'ready' | 'approving' | 'approved' | 'denied' | 'expired' | 'error'

function isLoggedIn() {
  return !!localStorage.getItem('chronicle_token')
}

export default function DeviceAuthPage() {
  const { code } = useParams<{ code: string }>()
  const navigate  = useNavigate()

  const [state, setState]   = useState<PageState>('loading')
  const [info, setInfo]     = useState<DeviceAuthInfoDto | null>(null)
  const [error, setError]   = useState('')

  useEffect(() => {
    if (!code) { setState('error'); return }

    if (!isLoggedIn()) {
      // Store return URL and redirect to login
      sessionStorage.setItem('chronicle_device_auth_return', `/device-auth/${code}`)
      setState('login-required')
      return
    }

    loadInfo()
  }, [code])

  async function loadInfo() {
    setState('loading')
    try {
      const data = await getDeviceAuthInfo(code!)
      setInfo(data)

      if (data.status === 'approved' || data.status === 'retrieved') {
        setState('approved')
      } else if (data.status === 'denied') {
        setState('denied')
      } else if (data.status === 'expired') {
        setState('expired')
      } else {
        setState('ready')
      }
    } catch {
      setState('error')
      setError('Unable to load device info. The link may have expired or already been used.')
    }
  }

  async function handleApprove() {
    setState('approving')
    try {
      await approveDevice(code!)
      setState('approved')
    } catch {
      setError('Failed to approve. Please try again.')
      setState('ready')
    }
  }

  async function handleDeny() {
    try {
      await denyDevice(code!)
    } catch {
      // Best-effort
    }
    setState('denied')
  }

  function handleLoginRedirect() {
    navigate(`/login?return=${encodeURIComponent(`/device-auth/${code}`)}`)
  }

  // ── Render ────────────────────────────────────────────────────────────────

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <div className={styles.logo}>Chronicle</div>

        {state === 'loading' && (
          <p className={styles.message}>Loading…</p>
        )}

        {state === 'login-required' && (
          <>
            <h1 className={styles.heading}>Sign in to authorise this device</h1>
            <p className={styles.sub}>
              A device is requesting access to your Chronicle account. You must be
              signed in to approve or deny the request.
            </p>
            <button className={styles.allowBtn} onClick={handleLoginRedirect}>
              Sign in to Chronicle
            </button>
          </>
        )}

        {(state === 'ready' || state === 'approving') && info && (
          <>
            <h1 className={styles.heading}>Authorise device?</h1>

            <div className={styles.codeBox}>
              <span className={styles.codeLabel}>Your code</span>
              <span className={styles.displayCode}>{info.displayCode}</span>
            </div>

            {info.deviceName && (
              <p className={styles.deviceName}>
                Device: <strong>{info.deviceName}</strong>
              </p>
            )}

            <p className={styles.sub}>
              This will create a permanent API key allowing the device to scrobble
              watch progress to your Chronicle account. You can revoke it at any
              time from <strong>Settings → API Keys</strong>.
            </p>

            {error && <p className={styles.errorMsg}>{error}</p>}

            <div className={styles.actions}>
              <button
                className={styles.allowBtn}
                onClick={handleApprove}
                disabled={state === 'approving'}
              >
                {state === 'approving' ? 'Authorising…' : '✓ Allow'}
              </button>
              <button
                className={styles.denyBtn}
                onClick={handleDeny}
                disabled={state === 'approving'}
              >
                ✕ Deny
              </button>
            </div>
          </>
        )}

        {state === 'approved' && (
          <>
            <div className={styles.successIcon}>✓</div>
            <h1 className={styles.heading}>Device authorised!</h1>
            <p className={styles.sub}>
              The device has been granted access to your Chronicle account.
              You can safely close this tab.
            </p>
          </>
        )}

        {state === 'denied' && (
          <>
            <div className={styles.deniedIcon}>✕</div>
            <h1 className={styles.heading}>Access denied</h1>
            <p className={styles.sub}>
              The device was not granted access. You can safely close this tab.
            </p>
          </>
        )}

        {state === 'expired' && (
          <>
            <h1 className={styles.heading}>Link expired</h1>
            <p className={styles.sub}>
              This authorisation link has expired (codes are valid for 5 minutes).
              Please start a new connection from your device.
            </p>
          </>
        )}

        {state === 'error' && (
          <>
            <h1 className={styles.heading}>Something went wrong</h1>
            <p className={styles.sub}>{error || 'The link may be invalid or already used.'}</p>
          </>
        )}
      </div>
    </div>
  )
}
