import axios from 'axios'

const client = axios.create({
  baseURL: '/api/v1',
  headers: { 'Content-Type': 'application/json' },
})

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
    const status: number | undefined = err.response?.status
    if (apiMessage && status) {
      return Promise.reject(new ApiError(apiMessage, status, apiCode))
    }
    return Promise.reject(err)
  },
)

export default client
