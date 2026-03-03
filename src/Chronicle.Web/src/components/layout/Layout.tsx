import { Outlet, NavLink, useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { useAuth } from '@/hooks/useAuth'
import { getScanStatus } from '@/api/scan'
import styles from './Layout.module.css'

export default function Layout() {
  const { user, logout } = useAuth()
  const navigate = useNavigate()

  const { data: scanStatus } = useQuery({
    queryKey: ['scan-status'],
    queryFn: getScanStatus,
    staleTime: 60_000,
  })

  if (!user) {
    navigate('/login')
    return null
  }

  return (
    <div className={styles.shell}>
      <header className={styles.header}>
        <span className={styles.logo}>Chronicle</span>
        <div className={styles.headerRight}>
          <span className={styles.username}>{user.username}</span>
          <button className={styles.logoutBtn} onClick={logout}>Logout</button>
        </div>
      </header>

      <nav className={styles.sidebar}>
        <NavLink to="/" end className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          Dashboard
        </NavLink>
        <NavLink to="/library" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          Library
        </NavLink>
        {scanStatus?.available && (
          <NavLink to="/scan" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
            File Scan
          </NavLink>
        )}
        <NavLink to="/history" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          History
        </NavLink>
        <NavLink to="/media/add" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          Add Media
        </NavLink>
        <NavLink to="/settings/service" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          Settings
        </NavLink>
        <NavLink to="/settings/api-keys" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          API Keys
        </NavLink>
        <NavLink to="/import" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          Import
        </NavLink>
        <NavLink to="/reports" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          Reports
        </NavLink>
        <NavLink to="/lists" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          Lists
        </NavLink>
        <NavLink to="/plugins" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          Plugins
        </NavLink>
      </nav>

      <main className={styles.content}>
        <Outlet />
      </main>
    </div>
  )
}
