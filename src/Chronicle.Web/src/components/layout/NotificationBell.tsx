import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import {
  clearReadNotifications, deleteNotification, listNotifications, markAllNotificationsRead, markNotificationRead,
  type NotificationItem,
} from '@/api/notifications'
import { UNREAD_KEY } from '@/hooks/useLiveUpdates'
import styles from './NotificationBell.module.css'

function ago(iso: string, now = Date.now()): string {
  const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000))
  if (seconds < 60) return 'just now'
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} h ago`
  return `${Math.round(hours / 24)} d ago`
}

/** The bell in the header: an unread count, and a panel of recent notices that open the page they are about. */
export default function NotificationBell() {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)

  // The unread count is kept fresh by the background poll (useLiveUpdates), which writes it into this key.
  const { data: unread = 0 } = useQuery<number>({ queryKey: UNREAD_KEY, queryFn: () => 0, enabled: false, initialData: 0 })

  const { data: page, isFetching } = useQuery({
    queryKey: ['notifications', 'list', unread],
    queryFn: () => listNotifications(30),
    enabled: open,
  })

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => { if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false) }
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  function refresh() {
    void qc.invalidateQueries({ queryKey: ['notifications', 'list'] })
  }

  function setUnread(n: number) { qc.setQueryData(UNREAD_KEY, Math.max(0, n)) }

  async function openItem(n: NotificationItem) {
    if (!n.isRead) {
      setUnread(unread - 1)
      try { await markNotificationRead(n.id) } catch { /* the next poll corrects the count */ }
    }
    setOpen(false)
    if (n.link) navigate(n.link)
    else refresh()
  }

  async function markAll() {
    setUnread(0)
    try { await markAllNotificationsRead() } finally { refresh() }
  }

  async function remove(n: NotificationItem) {
    if (!n.isRead) setUnread(unread - 1)
    try { await deleteNotification(n.id) } finally { refresh() }
  }

  async function clearRead() {
    try { await clearReadNotifications() } finally { refresh() }
  }

  const items = page?.items ?? []

  return (
    <div className={styles.root} ref={rootRef}>
      <button type="button" className={styles.bell} onClick={() => setOpen(o => !o)}
              aria-label={unread > 0 ? `Notifications, ${unread} unread` : 'Notifications'} aria-expanded={open} aria-haspopup="dialog">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9" />
          <path d="M13.7 21a2 2 0 0 1-3.4 0" />
        </svg>
        {unread > 0 && <span className={styles.badge} data-testid="unread-badge">{unread > 99 ? '99+' : unread}</span>}
      </button>

      {open && (
        <div className={styles.panel} role="dialog" aria-label="Notifications">
          <div className={styles.panelHeader}>
            <strong>Notifications</strong>
            <span className={styles.actions}>
              <button type="button" className={styles.linkBtn} disabled={unread === 0} onClick={() => void markAll()}>Mark all read</button>
              <button type="button" className={styles.linkBtn} disabled={!items.some(i => i.isRead)} onClick={() => void clearRead()}>Clear read</button>
            </span>
          </div>

          {items.length === 0 ? (
            <p className={styles.empty}>{isFetching ? 'Loading…' : 'Nothing new.'}</p>
          ) : (
            <ul className={styles.list}>
              {items.map(n => (
                <li key={n.id} className={n.isRead ? styles.read : styles.unread}>
                  <button type="button" className={styles.item} onClick={() => void openItem(n)}>
                    <span className={styles.title}>{n.title}</span>
                    {n.body && <span className={styles.body}>{n.body}</span>}
                    <span className={styles.when}>{ago(n.createdAt)}</span>
                  </button>
                  <button type="button" className={styles.dismiss} aria-label={`Dismiss ${n.title}`} onClick={() => void remove(n)}>×</button>
                </li>
              ))}
            </ul>
          )}

          <div className={styles.panelFooter}>
            <Link to="/preferences" onClick={() => setOpen(false)}>Choose what you are told about</Link>
          </div>
        </div>
      )}
    </div>
  )
}
