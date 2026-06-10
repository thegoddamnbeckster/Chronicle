import axios from 'axios'

const client = axios.create({
  baseURL: '/api/v1',
  headers: { 'Content-Type': 'application/json' },
})

// ── Plugin auth failure event bus ─────────────────────────────────────────────
// The API client can't directly call React context, so we publish auth failures
// here and the AuthFailureContext subscribes via subscribeToAuthFailures().
type AuthFailureListener = (pluginId: string, pluginName: string) => void
const authFailureListeners = new Set<AuthFailureListener>()

export function subscribeToAuthFailures(fn: AuthFailureListener): () => void {
  authFailureListeners.add(fn)
  return () => authFailureListeners.delete(fn)
}

function emitAuthFailure(pluginId: string, pluginName: string) {
  authFailureListeners.forEach(fn => fn(pluginId, pluginName))
}

// Attach JWT token from localStorage on every request
client.interceptors.request.use((config) => {
  const token = localStorage.getItem('chronicle_token')
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

/** Structured error from the Chronicle API envelope ({ success: false, error: { code, message } }) */
export class ApiError extends Error {
  constructor(
    message: string,
    public readonly statusCode: number,
    public readonly errorCode?: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

// Extract API error message from response envelope; redirect to login on 401
client.interceptors.response.use(
  (res) => res,
  (err) => {
    if (err.response?.status === 401) {
      localStorage.removeItem('chronicle_token')
      window.location.href = '/login'
    }
    const apiMessage: string | undefined = err.response?.data?.error?.message
    const apiCode: string | undefined = err.response?.data?.error?.code
    const apiPluginId: string | undefined = err.response?.data?.error?.pluginId
    const status: number | undefined = err.response?.status

    // Notify auth-failure subscribers so the global banner can be shown
    if (apiCode === 'PLUGIN_AUTH_FAILED' && apiPluginId) {
      emitAuthFailure(apiPluginId, apiPluginId) // name resolved by context from plugin list
    }

    if (apiMessage && status) {
      return Promise.reject(new ApiError(apiMessage, status, apiCode))
    }
    return Promise.reject(err)
  },
)

export default client
