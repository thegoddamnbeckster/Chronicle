import { Routes, Route, Navigate } from 'react-router-dom'
import Layout from '@/components/layout/Layout'
import LoginPage from '@/pages/auth/LoginPage'
import RegisterPage from '@/pages/auth/RegisterPage'
import DashboardPage from '@/pages/dashboard/DashboardPage'
import LibraryPage from '@/pages/library/LibraryPage'
import AddMediaPage from '@/pages/media/AddMediaPage'
import HistoryPage from '@/pages/media/HistoryPage'
import ServiceSettingsPage from '@/pages/settings/ServiceSettingsPage'
import ApiKeysPage from '@/pages/settings/ApiKeysPage'

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
        <Route path="settings/service" element={<ServiceSettingsPage />} />
        <Route path="settings/api-keys" element={<ApiKeysPage />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
