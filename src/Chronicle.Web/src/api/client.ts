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

/**
 * Marks a request as automatic polling rather than something the user did. The server still
 * authenticates it, but it does not restart the session's idle timer -- otherwise a tab left
 * open and abandoned would keep its session alive forever just by polling.
 */
export const BACKGROUND_REQUEST = { headers: { 'X-Chronicle-Background': '1' } } as const

// Attach the session key from localStorage on every request
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
    // 401 = the server no longer recognises this session key (logged out, expired, ended by
    // an admin, or the API restarted). Not for the sign-in calls themselves: a wrong password
    // is also a 401 and must stay on the form showing its error, not reload the page.
    const requestUrl: string = err.config?.url ?? ''
    const isSignInCall = requestUrl.endsWith('/auth/login') || requestUrl.endsWith('/auth/register')
    if (err.response?.status === 401 && !isSignInCall) {
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

    // No structured envelope on this error — either the request never reached Chronicle
    // at all (network error, no `status`) or it hit something in front of it that doesn't
    // speak Chronicle's response shape (e.g. Vite's dev proxy returning its own bare 500/502
    // when the API is down mid-restart). Axios's own message for these ("Network Error",
    // "Request failed with status code 500") is accurate but reads like the app itself is
    // broken. Since a real Chronicle-side failure always carries the envelope and is handled
    // above, anything reaching here is a connectivity problem — say so plainly instead.
    return Promise.reject(new ApiError(
      'Unable to reach the Chronicle server. It may be restarting — please try again in a moment.',
      status ?? 0,
    ))
  },
)

export default client
