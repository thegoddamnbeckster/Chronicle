import { useEffect, useRef, useState } from 'react'
import { Outlet, NavLink } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { useAuth } from '@/hooks/useAuth'
import { getScanStatus } from '@/api/scan'
import { getDiagnostics } from '@/api/diagnostics'
import { useScrollRestoration } from '@/hooks/useScrollRestoration'
import NavGroup from './NavGroup'
import ActivityPanel from './ActivityPanel'
import AppFooter from './AppFooter'
import GlobalSearch from './GlobalSearch'
import styles from './Layout.module.css'

export default function Layout() {
  const { user, logout } = useAuth()
  const [version, setVersion] = useState<string | undefined>()
  const mainRef = useRef<HTMLElement>(null)
  useScrollRestoration(mainRef)

  useEffect(() => {
    getDiagnostics()
      .then(d => setVersion(`${d.version} · ${d.commitHash} · ${d.branch}`))
      .catch(() => {})
  }, [])

  const { data: scanStatus } = useQuery({
    queryKey: ['scan-status'],
    queryFn: getScanStatus,
    staleTime: 60_000,
  })

  // RequireAuth guarantees user is non-null before Layout mounts
  if (!user) return null

  return (
    <div className={styles.shell}>
      <header className={styles.header}>
        <span className={styles.logo}>Chronicle</span>
        <GlobalSearch />
        <div className={styles.headerRight}>
          <span className={styles.username}>{user.username}</span>
          <button className={styles.logoutBtn} onClick={logout}>Logout</button>
        </div>
      </header>

      <nav className={styles.sidebar}>
        {/* Standalone */}
        <NavLink to="/" end className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          Dashboard
        </NavLink>

        {/* Media group — default open */}
        <NavGroup label="Media" storageKey="nav_group_media" defaultOpen={true}>
          <NavLink to="/history" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
            History
          </NavLink>
          <NavGroup label="Library" storageKey="nav_group_library" defaultOpen={true}>
            <NavLink to="/library" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
              Library
            </NavLink>
            <NavLink to="/media/add" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
              Add Media
            </NavLink>
            {scanStatus?.available && (
              <NavLink to="/scan" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
                File Scan
              </NavLink>
            )}
            <NavLink to="/import" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
              Import
            </NavLink>
            <NavLink to="/lists" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
              Lists
            </NavLink>
          </NavGroup>
        </NavGroup>

        {/* Settings group — default closed */}
        <NavGroup label="Settings" storageKey="nav_group_settings" defaultOpen={false}>
          <NavLink to="/settings/background-tasks" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
            Background Tasks
          </NavLink>
          <NavLink to="/settings/metadata-assignment" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
            Metadata Assignment
          </NavLink>
          <NavLink to="/settings/api-keys" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
            API Keys
          </NavLink>
          <NavLink to="/settings/library" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
            Library
          </NavLink>
          <NavLink to="/plugins" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
            Plugins
          </NavLink>
          <NavLink to="/preferences" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
            Preferences
          </NavLink>
          <NavLink to="/settings/service" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
            Service
          </NavLink>
        </NavGroup>

        {/* Standalone at bottom */}
        <NavLink to="/reports" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
          Reports
        </NavLink>

        <ActivityPanel />
      </nav>

      <main ref={mainRef} className={styles.content}>
        <Outlet />
      </main>

      <div className={styles.footer}>
        <AppFooter
          showDiagnostics={user?.showDiagnostics ?? false}
          version={version}
        />
      </div>
    </div>
  )
}
