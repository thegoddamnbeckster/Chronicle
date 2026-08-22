import { useEffect, useState } from 'react'
import { useAuth } from '@/hooks/useAuth'
import {
  listUsers,
  createUser,
  updateUserProfile,
  setUserAdmin,
  setUserActive,
  resetUserPassword,
  deleteUser,
  addUserContact,
  updateUserContact,
  deleteUserContact,
  type UserAccountDto,
} from '@/api/users'
import ContactsEditor from '@/components/settings/ContactsEditor'
import styles from './UsersPage.module.css'

function formatDate(iso: string | null): string {
  if (!iso) return 'never'
  return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
}

/** Surfaces the API's own message (LAST_ADMIN, USERNAME_TAKEN, …) instead of a generic one. */
function apiMessage(err: unknown, fallback: string): string {
  const message = (err as { response?: { data?: { error?: { message?: string } } } })
    ?.response?.data?.error?.message
  return message ?? fallback
}

export default function UsersPage() {
  const { user: me } = useAuth()
  const [users, setUsers] = useState<UserAccountDto[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [expandedId, setExpandedId] = useState<number | null>(null)
  const [busyId, setBusyId] = useState<number | null>(null)

  // Create form
  const [showCreate, setShowCreate] = useState(false)
  const [nUsername, setNUsername] = useState('')
  const [nPassword, setNPassword] = useState('')
  const [nEmail, setNEmail] = useState('')
  const [nFirst, setNFirst] = useState('')
  const [nLast, setNLast] = useState('')
  const [nHandle, setNHandle] = useState('')
  const [nAdmin, setNAdmin] = useState(false)
  const [creating, setCreating] = useState(false)
  const [createError, setCreateError] = useState('')

  useEffect(() => { void load() }, [])

  async function load() {
    try {
      setUsers(await listUsers())
      setError('')
    } catch (err) {
      setError(apiMessage(err, 'Could not load users.'))
    } finally {
      setLoading(false)
    }
  }

  async function run(id: number, action: () => Promise<unknown>, fallback: string) {
    setBusyId(id)
    setError('')
    try {
      await action()
      await load()
    } catch (err) {
      setError(apiMessage(err, fallback))
    } finally {
      setBusyId(null)
    }
  }

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault()
    setCreating(true)
    setCreateError('')
    try {
      await createUser({
        username:  nUsername.trim(),
        password:  nPassword,
        email:     nEmail.trim() || null,
        firstName: nFirst.trim() || null,
        lastName:  nLast.trim() || null,
        handle:    nHandle.trim() || null,
        isAdmin:   nAdmin,
      })
      setNUsername(''); setNPassword(''); setNEmail('')
      setNFirst(''); setNLast(''); setNHandle(''); setNAdmin(false)
      setShowCreate(false)
      await load()
    } catch (err) {
      setCreateError(apiMessage(err, 'Could not create that user.'))
    } finally {
      setCreating(false)
    }
  }

  function handleDelete(u: UserAccountDto) {
    const ok = confirm(
      `Permanently delete ${u.username}?\n\n` +
      'This removes their library, watch history, API keys, and lists. ' +
      'Shared media and its metadata are not affected.\n\n' +
      'Deactivating instead keeps everything and can be undone.'
    )
    if (!ok) return
    void run(u.id, () => deleteUser(u.id), 'Could not delete that user.')
  }

  async function handleResetPassword(u: UserAccountDto) {
    const pw = prompt(`Set a new password for ${u.username} (at least 8 characters):`)
    if (pw === null) return
    if (pw.length < 8) { setError('Password must be at least 8 characters.'); return }
    await run(u.id, () => resetUserPassword(u.id, pw), 'Could not reset that password.')
  }

  if (loading) return <div className={styles.page}><p className={styles.loading}>Loading…</p></div>

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Users</h1>

      {error && <p className={styles.error}>{error}</p>}

      <div className={styles.card}>
        <div className={styles.cardHeader}>
          <h2 className={styles.cardTitle}>Accounts ({users.length})</h2>
          <button className={styles.createBtn} onClick={() => setShowCreate(s => !s)}>
            {showCreate ? 'Cancel' : 'Add User'}
          </button>
        </div>

        {showCreate && (
          <form className={styles.createForm} onSubmit={handleCreate}>
            <div className={styles.formRow}>
              <div className={styles.formGroup}>
                <label className={styles.label} htmlFor="n-username">Username *</label>
                <input id="n-username" className={styles.textInput} value={nUsername}
                       onChange={e => setNUsername(e.target.value)} minLength={3} maxLength={50} required />
              </div>
              <div className={styles.formGroup}>
                <label className={styles.label} htmlFor="n-password">Password *</label>
                <input id="n-password" type="password" className={styles.textInput} value={nPassword}
                       onChange={e => setNPassword(e.target.value)} minLength={8} required
                       autoComplete="new-password" />
              </div>
              <div className={styles.formGroup}>
                <label className={styles.label} htmlFor="n-email">Email</label>
                <input id="n-email" type="email" className={styles.textInput} value={nEmail}
                       onChange={e => setNEmail(e.target.value)} />
              </div>
            </div>
            <div className={styles.formRow}>
              <div className={styles.formGroup}>
                <label className={styles.label} htmlFor="n-first">First name</label>
                <input id="n-first" className={styles.textInput} value={nFirst}
                       onChange={e => setNFirst(e.target.value)} maxLength={100} />
              </div>
              <div className={styles.formGroup}>
                <label className={styles.label} htmlFor="n-last">Last name</label>
                <input id="n-last" className={styles.textInput} value={nLast}
                       onChange={e => setNLast(e.target.value)} maxLength={100} />
              </div>
              <div className={styles.formGroup}>
                <label className={styles.label} htmlFor="n-handle">Handle</label>
                <input id="n-handle" className={styles.textInput} value={nHandle}
                       onChange={e => setNHandle(e.target.value)} maxLength={50} placeholder="@them" />
              </div>
              <label className={styles.checkLabel}>
                <input type="checkbox" checked={nAdmin} onChange={e => setNAdmin(e.target.checked)} />
                Administrator
              </label>
              <button type="submit" className={styles.createBtn}
                      disabled={creating || nUsername.trim().length < 3 || nPassword.length < 8}>
                {creating ? 'Creating…' : 'Create'}
              </button>
            </div>
            {createError && <p className={styles.error}>{createError}</p>}
          </form>
        )}

        <div className={styles.userList}>
          {users.map(u => {
            const isMe = u.id === me?.id
            const busy = busyId === u.id
            return (
              <div key={u.id} className={u.isActive ? styles.userRow : styles.userRowInactive}>
                <div className={styles.userMain}>
                  <button className={styles.expandBtn}
                          onClick={() => setExpandedId(expandedId === u.id ? null : u.id)}
                          aria-expanded={expandedId === u.id}>
                    {expandedId === u.id ? '▾' : '▸'}
                  </button>
                  <div className={styles.userInfo}>
                    <span className={styles.userName}>
                      {u.resolvedDisplayName}
                      {u.resolvedDisplayName !== u.username && (
                        <span className={styles.userHandle}> ({u.username})</span>
                      )}
                      {isMe && <span className={styles.youTag}>you</span>}
                    </span>
                    <span className={styles.userMeta}>
                      {u.isAdmin ? 'Administrator' : 'User'}
                      {!u.isActive && ' · deactivated'}
                      {' · joined '}{formatDate(u.createdAt)}
                      {' · last login '}{formatDate(u.lastLoginAt)}
                      {u.contacts.length > 0 && ` · ${u.contacts.length} contact${u.contacts.length === 1 ? '' : 's'}`}
                    </span>
                  </div>
                  <div className={styles.userActions}>
                    <button className={styles.smallBtn} disabled={busy}
                            onClick={() => run(u.id, () => setUserAdmin(u.id, !u.isAdmin),
                              u.isAdmin ? 'Could not demote that user.' : 'Could not promote that user.')}>
                      {u.isAdmin ? 'Demote' : 'Promote'}
                    </button>
                    <button className={styles.smallBtn} disabled={busy || (isMe && u.isActive)}
                            title={isMe && u.isActive ? 'You cannot deactivate your own account' : undefined}
                            onClick={() => run(u.id, () => setUserActive(u.id, !u.isActive),
                              'Could not change that account’s status.')}>
                      {u.isActive ? 'Deactivate' : 'Reactivate'}
                    </button>
                    <button className={styles.smallBtn} disabled={busy || isMe}
                            title={isMe ? 'Change your own password from My Profile' : undefined}
                            onClick={() => handleResetPassword(u)}>
                      Reset Password
                    </button>
                    <button className={styles.dangerBtn} disabled={busy || isMe}
                            title={isMe ? 'You cannot delete your own account' : undefined}
                            onClick={() => handleDelete(u)}>
                      Delete
                    </button>
                  </div>
                </div>

                {expandedId === u.id && (
                  <UserDetail user={u} onChanged={load} />
                )}
              </div>
            )
          })}
        </div>
      </div>
    </div>
  )
}

function UserDetail({ user, onChanged }: { user: UserAccountDto; onChanged: () => Promise<void> }) {
  const [firstName, setFirstName] = useState(user.firstName ?? '')
  const [lastName, setLastName] = useState(user.lastName ?? '')
  const [handle, setHandle] = useState(user.handle ?? '')
  const [displayName, setDisplayName] = useState(user.displayName ?? '')
  const [email, setEmail] = useState(user.email ?? '')
  const [saving, setSaving] = useState(false)
  const [detailError, setDetailError] = useState('')

  async function handleSave(e: React.FormEvent) {
    e.preventDefault()
    setSaving(true)
    setDetailError('')
    try {
      await updateUserProfile(user.id, {
        email:       email.trim() || null,
        firstName:   firstName.trim() || null,
        lastName:    lastName.trim() || null,
        handle:      handle.trim() || null,
        displayName: displayName.trim() || null,
      })
      await onChanged()
    } catch (err) {
      setDetailError(apiMessage(err, 'Could not save that profile.'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className={styles.detail}>
      <form onSubmit={handleSave}>
        <div className={styles.formRow}>
          <div className={styles.formGroup}>
            <label className={styles.label}>First name</label>
            <input className={styles.textInput} value={firstName}
                   onChange={e => setFirstName(e.target.value)} maxLength={100} />
          </div>
          <div className={styles.formGroup}>
            <label className={styles.label}>Last name</label>
            <input className={styles.textInput} value={lastName}
                   onChange={e => setLastName(e.target.value)} maxLength={100} />
          </div>
          <div className={styles.formGroup}>
            <label className={styles.label}>Handle</label>
            <input className={styles.textInput} value={handle}
                   onChange={e => setHandle(e.target.value)} maxLength={50} />
          </div>
        </div>
        <div className={styles.formRow}>
          <div className={styles.formGroup}>
            <label className={styles.label}>Email</label>
            <input type="email" className={styles.textInput} value={email}
                   onChange={e => setEmail(e.target.value)} />
          </div>
          <div className={styles.formGroup}>
            <label className={styles.label}>Display name override</label>
            <input className={styles.textInput} value={displayName}
                   onChange={e => setDisplayName(e.target.value)} maxLength={100} />
          </div>
          <button
            type="submit"
            className={styles.createBtn}
            disabled={
              saving ||
              (firstName === (user.firstName ?? '') &&
                lastName === (user.lastName ?? '') &&
                handle === (user.handle ?? '') &&
                displayName === (user.displayName ?? '') &&
                email === (user.email ?? ''))
            }
          >
            {saving ? 'Saving…' : 'Save'}
          </button>
        </div>
        {detailError && <p className={styles.error}>{detailError}</p>}
      </form>

      <h3 className={styles.detailHeading}>Contact Methods</h3>
      <ContactsEditor
        contacts={user.contacts}
        onAdd={async input => { await addUserContact(user.id, input); await onChanged() }}
        onUpdate={async (id, input) => { await updateUserContact(user.id, id, input); await onChanged() }}
        onDelete={async id => { await deleteUserContact(user.id, id); await onChanged() }}
      />
    </div>
  )
}
