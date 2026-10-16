import { lazy, useEffect } from 'react'
import { Routes, Route, Navigate, useNavigate } from 'react-router-dom'
import { useAuth } from '@/hooks/useAuth'
import { ErrorBoundary } from '@/components/ErrorBoundary'
import { AuthFailureProvider } from '@/context/AuthFailureContext'
import AuthFailureBanner from '@/components/AuthFailureBanner'
import Layout from '@/components/layout/Layout'
import LoginPage from '@/pages/auth/LoginPage'
import RegisterPage from '@/pages/auth/RegisterPage'
import ForgotPasswordPage from '@/pages/auth/ForgotPasswordPage'
import ResetPasswordPage from '@/pages/auth/ResetPasswordPage'
import DeviceAuthPage from '@/pages/device-auth/DeviceAuthPage'

// Every page behind the sign-in is its own chunk: a phone opening the library no longer downloads the settings, scan, plugin and
// report screens first.
const DashboardPage = lazy(() => import('@/pages/dashboard/DashboardPage'))
const LibraryPage = lazy(() => import('@/pages/library/LibraryPage'))
const PeopleLibraryPage = lazy(() => import('@/pages/people/PeopleLibraryPage'))
const PersonDetailPage = lazy(() => import('@/pages/people/PersonDetailPage'))
const AddMediaPage = lazy(() => import('@/pages/media/AddMediaPage'))
const AddCollectionPage = lazy(() => import('@/pages/media/AddCollectionPage'))
const MediaDetailPage = lazy(() => import('@/pages/media/MediaDetailPage'))
const HistoryPage = lazy(() => import('@/pages/media/HistoryPage'))
const ImportPage = lazy(() => import('@/pages/import/ImportPage'))
const ReportsPage = lazy(() => import('@/pages/reports/ReportsPage'))
const ServiceSettingsPage = lazy(() => import('@/pages/settings/ServiceSettingsPage'))
const ApiKeysPage = lazy(() => import('@/pages/settings/ApiKeysPage'))
const LibrarySettingsPage = lazy(() => import('@/pages/settings/LibrarySettingsPage'))
const BackgroundTasksPage = lazy(() => import('@/pages/settings/BackgroundTasksPage'))
const DatabasePage = lazy(() => import('@/pages/settings/DatabasePage'))
const EmailSettingsPage = lazy(() => import('@/pages/settings/EmailSettingsPage'))
const MediaTypesPage = lazy(() => import('@/pages/settings/MediaTypesPage'))
const EnrichmentDrillDownPage = lazy(() => import('@/pages/settings/EnrichmentDrillDownPage'))
const PluginsPage = lazy(() => import('@/pages/plugins/PluginsPage'))
const GettingStartedPage = lazy(() => import('@/pages/setup/GettingStartedPage'))
const ListsPage = lazy(() => import('@/pages/lists/ListsPage'))
const ListDetailPage = lazy(() => import('@/pages/lists/ListDetailPage'))
const ScanPage = lazy(() => import('@/pages/scan/ScanPage'))
const PreferencesPage = lazy(() => import('@/pages/preferences/PreferencesPage'))
const MetadataAssignmentPage = lazy(() => import('@/pages/settings/MetadataAssignmentPage'))
const FieldAliasesPage = lazy(() => import('@/pages/settings/FieldAliasesPage'))
const DuplicatesPage = lazy(() => import('@/pages/settings/DuplicatesPage'))
const UsersPage = lazy(() => import('@/pages/settings/UsersPage'))
const ProfilePage = lazy(() => import('@/pages/settings/ProfilePage'))

function RequireAuth({ children }: { children: React.ReactNode }) {
  const { user, loading } = useAuth()
  const navigate = useNavigate()

  useEffect(() => {
    if (!loading && !user) navigate('/login', { replace: true })
  }, [user, loading, navigate])

  if (loading || !user) return null
  return <>{children}</>
}

/** Sits inside RequireAuth, so `user` is already resolved by the time this renders. */
function RequireAdmin({ children }: { children: React.ReactNode }) {
  const { user } = useAuth()
  if (!user?.isAdmin) return <Navigate to="/settings/profile" replace />
  return <>{children}</>
}

export default function App() {
  return (
    <AuthFailureProvider>
      <AuthFailureBanner />
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      <Route path="/reset-password" element={<ResetPasswordPage />} />
      {/* Device-auth approval page — accessible without being logged in (page handles auth check).
          Short path on purpose: the code itself is now the whole point of brevity (a LAN
          pairing code someone might type by hand), so the route around it stays minimal too. */}
      <Route path="/a/:code" element={<DeviceAuthPage />} />
      <Route
        path="/"
        element={
          <RequireAuth>
            <ErrorBoundary>
              <Layout />
            </ErrorBoundary>
          </RequireAuth>
        }
      >
        <Route index element={<DashboardPage />} />
        <Route path="library" element={<LibraryPage />} />
        <Route path="people" element={<PeopleLibraryPage />} />
        <Route path="people/:id" element={
          <ErrorBoundary context="Person Detail">
            <PersonDetailPage />
          </ErrorBoundary>
        } />
        <Route path="history" element={<HistoryPage />} />
        <Route path="media/add" element={<AddMediaPage />} />
        <Route path="media/add-collection" element={<AddCollectionPage />} />
        <Route path="media/:id" element={
          <ErrorBoundary context="Media Detail">
            <MediaDetailPage />
          </ErrorBoundary>
        } />
        <Route path="import" element={<ImportPage />} />
        <Route path="reports" element={<ReportsPage />} />
        <Route path="settings/service" element={<ServiceSettingsPage />} />
        <Route path="settings/api-keys" element={<ApiKeysPage />} />
        <Route path="settings/library" element={<LibrarySettingsPage />} />
        <Route path="settings/background-tasks" element={<BackgroundTasksPage />} />
        <Route path="settings/database" element={<DatabasePage />} />
        <Route path="settings/email" element={<EmailSettingsPage />} />
        <Route path="settings/media-types" element={<MediaTypesPage />} />
        <Route path="settings/enrichment/:pluginId" element={<EnrichmentDrillDownPage />} />
        <Route path="settings/metadata-assignment" element={<MetadataAssignmentPage />} />
        <Route path="settings/field-aliases" element={<FieldAliasesPage />} />
        <Route path="settings/duplicates" element={<DuplicatesPage />} />
        {/* Admin-only in the API; RequireAdmin keeps a non-admin from reaching a page
            that would just render a wall of 403s. */}
        <Route path="settings/users" element={<RequireAdmin><UsersPage /></RequireAdmin>} />
        <Route path="settings/profile" element={<ProfilePage />} />
        <Route path="plugins" element={<PluginsPage />} />
        <Route path="getting-started" element={<RequireAdmin><GettingStartedPage /></RequireAdmin>} />
        <Route path="lists" element={<ListsPage />} />
        <Route path="lists/:id" element={<ListDetailPage />} />
        <Route path="scan" element={<ScanPage />} />
        <Route path="preferences" element={<PreferencesPage />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
    </AuthFailureProvider>
  )
}
