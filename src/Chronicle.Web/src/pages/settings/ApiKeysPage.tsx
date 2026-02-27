import { useEffect, useState } from 'react'
import {
  listApiTokens,
  createApiToken,
  revokeApiToken,
  type ApiTokenDto,
  type CreateTokenResponse,
} from '@/api/apiTokens'
import styles from './ApiKeysPage.module.css'

function formatDate(iso: string | null): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
}

export default function ApiKeysPage() {
  const [tokens, setTokens] = useState<ApiTokenDto[]>([])
  const [loading, setLoading] = useState(true)

  // Create form state
  const [name, setName] = useState('')
  const [expiresAt, setExpiresAt] = useState('')
  const [creating, setCreating] = useState(false)
  const [createError, setCreateError] = useState('')

  // Newly-created token reveal
  const [newToken, setNewToken] = useState<CreateTokenResponse | null>(null)
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    loadTokens()
  }, [])

  async function loadTokens() {
    try {
      const data = await listApiTokens()
      setTokens(data)
    } catch {
      // error handled silently — list stays empty
    } finally {
      setLoading(false)
    }
  }

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) return
    setCreating(true)
    setCreateError('')
    setNewToken(null)
    try {
      const result = await createApiToken(name.trim(), expiresAt || null)
      setNewToken(result)
      setName('')
      setExpiresAt('')
      await loadTokens()
    } catch {
      setCreateError('Failed to create token. Please try again.')
    } finally {
      setCreating(false)
    }
  }

  async function handleRevoke(id: number) {
    if (!confirm('Revoke this API key? Any scrobblers using it will stop working.')) return
    try {
      await revokeApiToken(id)
      setTokens(prev => prev.filter(t => t.id !== id))
      if (newToken?.id === id) setNewToken(null)
    } catch {
      alert('Failed to revoke token.')
    }
  }

  async function handleCopy() {
    if (!newToken) return
    await navigator.clipboard.writeText(newToken.token)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>API Keys</h1>

      {/* Create new key */}
      <div className={styles.card}>
        <h2 className={styles.cardTitle}>Create New API Key</h2>
        <p className={styles.hint}>
          API keys let scrobblers (Plex, Jellyfin clients, etc.) submit playback events without
          needing your password. Each key is specific to one device or application.
        </p>

        <form onSubmit={handleCreate}>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="key-name">Name</label>
              <input
                id="key-name"
                type="text"
                className={styles.textInput}
                placeholder="e.g. Plex on Living Room TV"
                value={name}
                onChange={e => setName(e.target.value)}
                maxLength={100}
              />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="key-expires">Expires (optional)</label>
              <input
                id="key-expires"
                type="date"
                className={styles.textInput}
                value={expiresAt}
                onChange={e => setExpiresAt(e.target.value)}
              />
            </div>
            <button
              type="submit"
              className={styles.createBtn}
              disabled={creating || !name.trim()}
            >
              {creating ? 'Creating…' : 'Create Key'}
            </button>
          </div>
          {createError && <p className={styles.errorMsg}>{createError}</p>}
        </form>

        {newToken && (
          <div className={styles.newTokenBox}>
            <p className={styles.newTokenLabel}>✓ Key created: {newToken.name}</p>
            <p className={styles.newTokenWarning}>
              Copy this key now — it will not be shown again.
            </p>
            <p className={styles.tokenValue}>{newToken.token}</p>
            <button className={styles.copyBtn} onClick={handleCopy}>
              {copied ? 'Copied!' : 'Copy to Clipboard'}
            </button>
          </div>
        )}
      </div>

      {/* Existing keys */}
      <div className={styles.card}>
        <h2 className={styles.cardTitle}>Your API Keys</h2>

        {loading ? (
          <p className={styles.loading}>Loading…</p>
        ) : tokens.length === 0 ? (
          <p className={styles.emptyMsg}>No API keys yet. Create one above to get started.</p>
        ) : (
          <div className={styles.tokenList}>
            {tokens.map(t => (
              <div key={t.id} className={styles.tokenRow}>
                <div className={styles.tokenInfo}>
                  <span className={styles.tokenName}>{t.name}</span>
                  <span className={styles.tokenMeta}>
                    Created {formatDate(t.createdAt)}
                    {t.lastUsedAt ? ` · Last used ${formatDate(t.lastUsedAt)}` : ' · Never used'}
                    {t.expiresAt ? ` · Expires ${formatDate(t.expiresAt)}` : ''}
                  </span>
                </div>
                <button
                  className={styles.revokeBtn}
                  onClick={() => handleRevoke(t.id)}
                >
                  Revoke
                </button>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
