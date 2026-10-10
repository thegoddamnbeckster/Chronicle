import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import type { AxiosRequestConfig } from 'axios'
import client, { BACKGROUND_REQUEST } from './client'

const TOKEN_KEY = 'chronicle_token'

/** Replaces the transport so no network is touched; records the config each request was sent with. */
function stubTransport(status: number, body: unknown = { success: true, data: [] }) {
  const seen: AxiosRequestConfig[] = []
  client.defaults.adapter = async (config) => {
    seen.push(config)
    const response = { data: body, status, statusText: '', headers: {}, config, request: {} }
    if (status >= 400) {
      throw Object.assign(new Error(`status ${status}`), { isAxiosError: true, response, config })
    }
    return response
  }
  return seen
}

describe('api client session handling', () => {
  const realLocation = window.location
  let navigations: string[]

  beforeEach(() => {
    localStorage.clear()
    navigations = []
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...realLocation, set href(v: string) { navigations.push(v) }, get href() { return 'http://localhost/' } },
    })
  })

  afterEach(() => {
    Object.defineProperty(window, 'location', { configurable: true, value: realLocation })
  })

  it('sends the stored session key as a Bearer token', async () => {
    localStorage.setItem(TOKEN_KEY, 'chr_sess_abc')
    const seen = stubTransport(200)

    await client.get('/users/me')

    expect(seen[0].headers?.Authorization).toBe('Bearer chr_sess_abc')
  })

  it('sends no Authorization header when signed out', async () => {
    const seen = stubTransport(200)

    await client.get('/themes')

    expect(seen[0].headers?.Authorization).toBeUndefined()
  })

  it('clears the key and goes to /login when the server no longer recognises it', async () => {
    localStorage.setItem(TOKEN_KEY, 'chr_sess_stale')
    stubTransport(401)

    await expect(client.get('/users/me')).rejects.toBeTruthy()

    expect(localStorage.getItem(TOKEN_KEY)).toBeNull()
    expect(navigations).toContain('/login')
  })

  it('does NOT redirect when the sign-in call itself returns 401 (wrong password)', async () => {
    stubTransport(401, { success: false, error: { code: 'INVALID_CREDENTIALS', message: 'Invalid username or password.' } })

    await expect(client.post('/auth/login', { username: 'a', password: 'b' }))
      .rejects.toMatchObject({ message: 'Invalid username or password.' })

    expect(navigations).toHaveLength(0)
  })

  it('does NOT redirect when registration returns 401 either', async () => {
    stubTransport(401)

    await expect(client.post('/auth/register', {})).rejects.toBeTruthy()

    expect(navigations).toHaveLength(0)
  })

  it('marks polling requests as background so they do not keep a session alive', async () => {
    const seen = stubTransport(200)

    await client.get('/scrobble/active', BACKGROUND_REQUEST)
    await client.get('/users/me')

    expect(seen[0].headers?.['X-Chronicle-Background']).toBe('1')
    expect(seen[1].headers?.['X-Chronicle-Background']).toBeUndefined()
  })
})
