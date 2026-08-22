import { useEffect, useState } from 'react'
import {
  getMyProfile,
  updateMyProfile,
  changeMyPassword,
  addMyContact,
  updateMyContact,
  deleteMyContact,
  type UserAccountDto,
} from '@/api/users'
import ContactsEditor from '@/components/settings/ContactsEditor'
import styles from './UsersPage.module.css'

export default function ProfilePage() {
  const [profile, setProfile] = useState<UserAccountDto | null>(null)
  const [loading, setLoading] = useState(true)

  const [firstName, setFirstName] = useState('')
  const [lastName, setLastName] = useState('')
  const [handle, setHandle] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [email, setEmail] = useState('')
  const [saving, setSaving] = useState(false)
  const [savedAt, setSavedAt] = useState(0)
  const [saveError, setSaveError] = useState('')

  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [pwBusy, setPwBusy] = useState(false)
  const [pwMessage, setPwMessage] = useState('')
  const [pwError, setPwError] = useState('')

  useEffect(() => { void load() }, [])

  async function load() {
    try {
      const p = await getMyProfile()
      applyProfile(p)
    } finally {
      setLoading(false)
    }
  }

  function applyProfile(p: UserAccountDto) {
    setProfile(p)
    setFirstName(p.firstName ?? '')
    setLastName(p.lastName ?? '')
    setHandle(p.handle ?? '')
    setDisplayName(p.displayName ?? '')
    setEmail(p.email ?? '')
  }

  async function handleSave(e: React.FormEvent) {
    e.preventDefault()
    setSaving(true)
    setSaveError('')
    try {
      const updated = await updateMyProfile({
        email:       email.trim() || null,
        firstName:   firstName.trim() || null,
        lastName:    lastName.trim() || null,
        handle:      handle.trim() || null,
        displayName: displayName.trim() || null,
      })
      applyProfile(updated)
      setSavedAt(Date.now())
    } catch {
      setSaveError('Could not save your profile.')
    } finally {
      setSaving(false)
    }
  }

  async function handlePasswordChange(e: React.FormEvent) {
    e.preventDefault()
    setPwError('')
    setPwMessage('')
    if (newPassword.length < 8) { setPwError('New password must be at least 8 characters.'); return }
    if (newPassword !== confirmPassword) { setPwError('New passwords do not match.'); return }

    setPwBusy(true)
    try {
      await changeMyPassword(currentPassword, newPassword)
      setPwMessage('Password changed.')
      setCurrentPassword('')
      setNewPassword('')
      setConfirmPassword('')
    } catch {
      setPwError('Could not change your password — check your current password.')
    } finally {
      setPwBusy(false)
    }
  }

  async function reloadContacts() {
    applyProfile(await getMyProfile())
  }

  if (loading) return <div className={styles.page}><p className={styles.loading}>Loading…</p></div>
  if (!profile) return <div className={styles.page}><p className={styles.error}>Could not load your profile.</p></div>

  // Mirrors the server's fallback chain so the effect of a change is visible before saving.
  const previewName =
    displayName.trim() || handle.trim() || [firstName.trim(), lastName.trim()].filter(Boolean).join(' ') || profile.username

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>My Profile</h1>

      <div className={styles.card}>
        <h2 className={styles.cardTitle}>Identity</h2>
        <p className={styles.hint}>
          Chronicle shows you as <strong>{previewName}</strong>. Your handle wins if set,
          otherwise your name, and failing that your username ({profile.username}).
        </p>

        <form onSubmit={handleSave}>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="p-first">First name</label>
              <input id="p-first" className={styles.textInput} value={firstName}
                     onChange={e => setFirstName(e.target.value)} maxLength={100} />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="p-last">Last name</label>
              <input id="p-last" className={styles.textInput} value={lastName}
                     onChange={e => setLastName(e.target.value)} maxLength={100} />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="p-handle">Handle</label>
              <input id="p-handle" className={styles.textInput} value={handle}
                     onChange={e => setHandle(e.target.value)} maxLength={50} placeholder="@you" />
            </div>
          </div>

          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="p-email">Email</label>
              <input id="p-email" type="email" className={styles.textInput} value={email}
                     onChange={e => setEmail(e.target.value)} />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="p-display">Display name override</label>
              <input id="p-display" className={styles.textInput} value={displayName}
                     onChange={e => setDisplayName(e.target.value)} maxLength={100}
                     placeholder="leave blank to use the rule above" />
            </div>
            <button
              type="submit"
              className={styles.createBtn}
              disabled={
                saving ||
                (firstName === (profile.firstName ?? '') &&
                  lastName === (profile.lastName ?? '') &&
                  handle === (profile.handle ?? '') &&
                  displayName === (profile.displayName ?? '') &&
                  email === (profile.email ?? ''))
              }
            >
              {saving ? 'Saving…' : 'Save Profile'}
            </button>
          </div>

          {saveError && <p className={styles.error}>{saveError}</p>}
          {savedAt > 0 && !saveError && <p className={styles.success}>Profile saved.</p>}
        </form>
      </div>

      <div className={styles.card}>
        <h2 className={styles.cardTitle}>Contact Methods</h2>
        <p className={styles.hint}>
          Any number of ways to reach you — email, phone, social profiles, or anything else.
          One entry per kind can be marked primary.
        </p>
        <ContactsEditor
          contacts={profile.contacts}
          onAdd={async input => { await addMyContact(input); await reloadContacts() }}
          onUpdate={async (id, input) => { await updateMyContact(id, input); await reloadContacts() }}
          onDelete={async id => { await deleteMyContact(id); await reloadContacts() }}
        />
      </div>

      <div className={styles.card}>
        <h2 className={styles.cardTitle}>Change Password</h2>
        <form onSubmit={handlePasswordChange}>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="p-cur">Current password</label>
              <input id="p-cur" type="password" className={styles.textInput} value={currentPassword}
                     onChange={e => setCurrentPassword(e.target.value)} autoComplete="current-password" />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="p-new">New password</label>
              <input id="p-new" type="password" className={styles.textInput} value={newPassword}
                     onChange={e => setNewPassword(e.target.value)} autoComplete="new-password" />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="p-confirm">Confirm new password</label>
              <input id="p-confirm" type="password" className={styles.textInput} value={confirmPassword}
                     onChange={e => setConfirmPassword(e.target.value)} autoComplete="new-password" />
            </div>
            <button type="submit" className={styles.createBtn}
                    disabled={pwBusy || !currentPassword || !newPassword}>
              {pwBusy ? 'Changing…' : 'Change Password'}
            </button>
          </div>
          {pwError && <p className={styles.error}>{pwError}</p>}
          {pwMessage && <p className={styles.success}>{pwMessage}</p>}
        </form>
      </div>
    </div>
  )
}
