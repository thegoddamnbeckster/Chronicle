import { Routes, Route, Navigate } from 'react-router-dom'
import Layout from '@/components/layout/Layout'
import LoginPage from '@/pages/auth/LoginPage'
import RegisterPage from '@/pages/auth/RegisterPage'
import DashboardPage from '@/pages/dashboard/DashboardPage'
import LibraryPage from '@/pages/library/LibraryPage'
import AddMediaPage from '@/pages/media/AddMediaPage'
import MediaDetailPage from '@/pages/media/MediaDetailPage'
import HistoryPage from '@/pages/media/HistoryPage'
import ImportPage from '@/pages/import/ImportPage'
import ReportsPage from '@/pages/reports/ReportsPage'
import ServiceSettingsPage from '@/pages/settings/ServiceSettingsPage'
import ApiKeysPage from '@/pages/settings/ApiKeysPage'
import PluginsPage from '@/pages/plugins/PluginsPage'
import ListsPage from '@/pages/lists/ListsPage'
import ListDetailPage from '@/pages/lists/ListDetailPage'
import DeviceAuthPage from '@/pages/device-auth/DeviceAuthPage'
import ScanPage from '@/pages/scan/ScanPage'

function isLoggedIn() {
  return !!localStorage.getItem('chronicle_token')
}

function RequireAuth({ children }: { children: React.ReactNode }) {
  return isLoggedIn() ? <>{children}</> : <Navigate to="/login" replace />
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      {/* Device-auth approval page — accessible without being logged in (page handles auth check) */}
      <Route path="/device-auth/:code" element={<DeviceAuthPage />} />
      <Route
        path="/"
        element={
          <RequireAuth>
            <Layout />
          </RequireAuth>
        }
      >
        <Route index element={<DashboardPage />} />
        <Route path="library" element={<LibraryPage />} />
        <Route path="history" element={<HistoryPage />} />
        <Route path="media/add" element={<AddMediaPage />} />
        <Route path="media/:id" element={<MediaDetailPage />} />
        <Route path="import" element={<ImportPage />} />
        <Route path="reports" element={<ReportsPage />} />
        <Route path="settings/service" element={<ServiceSettingsPage />} />
        <Route path="settings/api-keys" element={<ApiKeysPage />} />
        <Route path="plugins" element={<PluginsPage />} />
        <Route path="lists" element={<ListsPage />} />
        <Route path="lists/:id" element={<ListDetailPage />} />
        <Route path="scan" element={<ScanPage />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
