import { useEffect, useRef, useState } from 'react'
import { Outlet, NavLink, useLocation } from 'react-router-dom'
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
  const [mobileNavOpen, setMobileNavOpen] = useState(false)
  const mainRef = useRef<HTMLElement>(null)
  const location = useLocation()
  useScrollRestoration(mainRef)

  useEffect(() => {
    getDiagnostics()
      .then(d => setVersion(`${d.version} · ${d.commitHash} · ${d.branch}`))
      .catch(() => {})
  }, [])

  // Auto-hide the mobile nav drawer whenever the route changes
  useEffect(() => {
    setMobileNavOpen(false)
  }, [location.pathname])

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
        <button
          type="button"
          className={styles.menuToggle}
          onClick={() => setMobileNavOpen(open => !open)}
          aria-label="Toggle navigation menu"
          aria-expanded={mobileNavOpen}
        >
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <line x1="3" y1="6" x2="21" y2="6" />
            <line x1="3" y1="12" x2="21" y2="12" />
            <line x1="3" y1="18" x2="21" y2="18" />
          </svg>
        </button>
        <span className={styles.logo}>Chronicle</span>
        <GlobalSearch />
        <div className={styles.headerRight}>
          <span className={styles.username}>{user.username}</span>
          <button className={styles.logoutBtn} onClick={logout}>Logout</button>
        </div>
      </header>

      <div
        className={`${styles.overlay} ${mobileNavOpen ? styles.overlayVisible : ''}`}
        onClick={() => setMobileNavOpen(false)}
        aria-hidden="true"
      />

      <nav className={`${styles.sidebar} ${mobileNavOpen ? styles.sidebarOpen : ''}`}>
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
            <NavGroup label="Add" storageKey="nav_group_add" defaultOpen={false}>
              <NavLink to="/media/add" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
                Media
              </NavLink>
              <NavLink to="/media/add-collection" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
                Collection
              </NavLink>
            </NavGroup>
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
          <NavLink to="/settings/field-aliases" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
            Field Aliases
          </NavLink>
          <NavLink to="/settings/duplicates" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
            Duplicates
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

          {/* Own section, alphabetically last within Settings */}
          <NavGroup label="Users" storageKey="nav_group_users" defaultOpen={false}>
            <NavLink to="/settings/profile" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
              My Profile
            </NavLink>
            {user.isAdmin && (
              <NavLink to="/settings/users" className={({ isActive }) => isActive ? styles.activeLink : styles.link}>
                Manage Users
              </NavLink>
            )}
          </NavGroup>
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
