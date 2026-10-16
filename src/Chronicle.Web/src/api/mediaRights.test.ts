import { describe, it, expect, vi, beforeEach } from 'vitest'
import client from './client'
import { getMediaRights, setMediaRights } from './mediaRights'

vi.mock('./client', () => ({ default: { get: vi.fn(), put: vi.fn() } }))

beforeEach(() => { vi.mocked(client.get).mockReset(); vi.mocked(client.put).mockReset() })

describe('media rights api', () => {
  it('reads the setting', async () => {
    vi.mocked(client.get).mockResolvedValue({ data: { success: true, data: { deleteAdminOnly: true } } })
    expect(await getMediaRights()).toEqual({ deleteAdminOnly: true })
    expect(client.get).toHaveBeenCalledWith('/media-rights')
  })

  it('treats an unreadable setting as open (the server still enforces the rule)', async () => {
    vi.mocked(client.get).mockImplementation(() => Promise.reject(new Error('down')))
    expect(await getMediaRights()).toEqual({ deleteAdminOnly: false })
  })

  it('writes the setting and returns what the server stored', async () => {
    vi.mocked(client.put).mockResolvedValue({ data: { success: true, data: { deleteAdminOnly: true } } })
    expect(await setMediaRights(true)).toEqual({ deleteAdminOnly: true })
    expect(client.put).toHaveBeenCalledWith('/media-rights', { deleteAdminOnly: true })
  })
})
