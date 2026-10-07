import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, waitFor, within, act } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import DatabasePage from './DatabasePage'
import * as api from '@/api/database'

vi.mock('@/api/database', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/database')>()
  return {
    ...actual,
    getDatabaseStatus: vi.fn(),
    listBackups: vi.fn(),
    createBackup: vi.fn(),
    deleteBackup: vi.fn(),
    downloadBackup: vi.fn(),
    restoreBackup: vi.fn(),
    runMaintenance: vi.fn(),
    saveDatabaseSettings: vi.fn(),
    uploadBackup: vi.fn(),
  }
})

const MB = 1024 * 1024

const STATUS: api.DatabaseStatus = {
  supported: true, provider: 'Microsoft.EntityFrameworkCore.Sqlite', note: null,
  databaseFile: 'D:\\data\\chronicle.db', databaseBytes: 250 * MB, walBytes: 4 * MB,
  pageSize: 4096, pageCount: 64000, freePages: 2560, reclaimableBytes: 10 * MB, freeDiskBytes: 50 * 1024 * MB,
  latestMigration: '20261007194413_AddApiTokenScope', backupDirectory: 'D:\\data\\backups', backupCount: 2, retainCount: 10,
  lastBackupAtUtc: '2026-10-07T02:00:00Z', warnSizeBytes: 5120 * MB, overWarnSize: false,
  largestTables: [{ name: 'media_items', bytes: 90 * MB, rows: null }],
}

const BACKUPS: api.BackupInfo[] = [
  { fileName: 'chronicle-20261007-020000-scheduled.zip', kind: 'scheduled', sizeBytes: 40 * MB, createdAtUtc: '2026-10-07T02:00:00Z',
    latestMigration: 'x', databaseBytes: 250 * MB, rowCounts: { users: 9, media_items: 12345 } },
  { fileName: 'pre-restore-20261006-101010.zip', kind: 'pre-restore', sizeBytes: 38 * MB, createdAtUtc: '2026-10-06T10:10:10Z',
    latestMigration: 'x', databaseBytes: 240 * MB, rowCounts: null },
]

beforeEach(() => {
  vi.mocked(api.getDatabaseStatus).mockResolvedValue(STATUS)
  vi.mocked(api.listBackups).mockResolvedValue(BACKUPS)
})

afterEach(() => {
  vi.restoreAllMocks()
  vi.useRealTimers()
})

describe('DatabasePage', () => {
  it('shows the status and the backups with what each one is', async () => {
    render(<DatabasePage />)

    expect(await screen.findByText('D:\\data\\chronicle.db')).toBeInTheDocument()
    expect(screen.getByText(/250 MB/)).toBeInTheDocument()
    expect(screen.getByText(/10 MB could be reclaimed/)).toBeInTheDocument()
    expect(screen.getByText('chronicle-20261007-020000-scheduled.zip')).toBeInTheDocument()
    expect(screen.getByText(/Nightly/)).toBeInTheDocument()
    expect(screen.getByText(/Before a restore/)).toBeInTheDocument()
    expect(screen.getByText(/9 users, 12345 items/)).toBeInTheDocument()
    expect(screen.getByText(/media_items - 90 MB/)).toBeInTheDocument()
  })

  it('says plainly when the database cannot be backed up from the app', async () => {
    vi.mocked(api.getDatabaseStatus).mockResolvedValue({ ...STATUS, supported: false, note: 'Use pg_dump on the database server.' })
    vi.mocked(api.listBackups).mockResolvedValue([])
    render(<DatabasePage />)

    expect(await screen.findByText('Use pg_dump on the database server.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Back up now/ })).not.toBeInTheDocument()
  })

  it('raises an alert when the database is over its warning size', async () => {
    vi.mocked(api.getDatabaseStatus).mockResolvedValue({ ...STATUS, overWarnSize: true })
    render(<DatabasePage />)

    expect(await screen.findByRole('alert')).toHaveTextContent(/over the 5\.0 GB warning size|over the 5 GB warning size/)
  })

  it('backs up on request and says where it went', async () => {
    vi.mocked(api.createBackup).mockResolvedValue({ ...BACKUPS[0], fileName: 'chronicle-new-manual.zip', sizeBytes: 41 * MB })
    render(<DatabasePage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Back up now' }))

    expect(await screen.findByText(/Backed up to chronicle-new-manual\.zip/)).toBeInTheDocument()
    expect(api.getDatabaseStatus).toHaveBeenCalledTimes(2)   // reloaded afterwards
  })

  it('shows the server\'s reason when something fails', async () => {
    vi.mocked(api.createBackup).mockRejectedValue(new Error('Another database operation is already running.'))
    render(<DatabasePage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Back up now' }))

    expect(await screen.findByText('Another database operation is already running.')).toBeInTheDocument()
  })

  it('saves the retention and warning settings as numbers', async () => {
    vi.mocked(api.saveDatabaseSettings).mockResolvedValue(STATUS)
    render(<DatabasePage />)
    const keep = await screen.findByLabelText('Backups to keep')

    await userEvent.clear(keep)
    await userEvent.type(keep, '4')
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(api.saveDatabaseSettings).toHaveBeenCalledWith({ backupsToKeep: 4, warnSizeMb: 5120 }))
  })

  it('deletes only after confirmation', async () => {
    vi.mocked(api.deleteBackup).mockResolvedValue()
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<DatabasePage />)
    const row = (await screen.findByText('pre-restore-20261006-101010.zip')).closest('div')!.parentElement!

    await userEvent.click(within(row).getByRole('button', { name: 'Delete' }))
    expect(api.deleteBackup).not.toHaveBeenCalled()

    confirm.mockReturnValue(true)
    await userEvent.click(within(row).getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(api.deleteBackup).toHaveBeenCalledWith('pre-restore-20261006-101010.zip'))
  })

  it('downloads through the API client', async () => {
    vi.mocked(api.downloadBackup).mockResolvedValue()
    render(<DatabasePage />)
    const row = (await screen.findByText('chronicle-20261007-020000-scheduled.zip')).closest('div')!.parentElement!

    await userEvent.click(within(row).getByRole('button', { name: 'Download' }))

    await waitFor(() => expect(api.downloadBackup).toHaveBeenCalledWith('chronicle-20261007-020000-scheduled.zip'))
  })

  it('uploads a chosen file and reports it ready', async () => {
    vi.mocked(api.uploadBackup).mockResolvedValue({ ...BACKUPS[0], fileName: 'uploaded-1.zip', kind: 'uploaded' })
    render(<DatabasePage />)
    await screen.findByText('D:\\data\\chronicle.db')
    const file = new File([new Uint8Array(10)], 'mine.zip', { type: 'application/zip' })

    await userEvent.upload(screen.getByTestId('backup-file'), file)

    expect(await screen.findByText(/Uploaded and checked uploaded-1\.zip/)).toBeInTheDocument()
    expect(api.uploadBackup).toHaveBeenCalledWith(file, expect.any(Function))
  })

  it('shows why an uploaded file was refused', async () => {
    vi.mocked(api.uploadBackup).mockRejectedValue(new Error('This zip is not a Chronicle backup (unexpected contents).'))
    render(<DatabasePage />)
    await screen.findByText('D:\\data\\chronicle.db')

    await userEvent.upload(screen.getByTestId('backup-file'), new File([new Uint8Array(3)], 'x.zip'))

    expect(await screen.findByText(/not a Chronicle backup/)).toBeInTheDocument()
  })

  it('runs quick maintenance, and full rebuild only after confirmation', async () => {
    vi.mocked(api.runMaintenance).mockResolvedValue({ summary: 'Done.', elapsed: '00:00:01', bytesBefore: 1, bytesAfter: 1 })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<DatabasePage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Quick maintenance' }))
    await waitFor(() => expect(api.runMaintenance).toHaveBeenCalledWith('quick'))

    await userEvent.click(screen.getByRole('button', { name: 'Full rebuild' }))
    expect(api.runMaintenance).not.toHaveBeenCalledWith('full')

    confirm.mockReturnValue(true)
    await userEvent.click(screen.getByRole('button', { name: 'Full rebuild' }))
    await waitFor(() => expect(api.runMaintenance).toHaveBeenCalledWith('full'))
  })

  describe('restore', () => {
    async function openRestore() {
      render(<DatabasePage />)
      const row = (await screen.findByText('chronicle-20261007-020000-scheduled.zip')).closest('div')!.parentElement!
      await userEvent.click(within(row).getByRole('button', { name: 'Restore…' }))
      return screen.getByRole('dialog', { name: 'Confirm restore' })
    }

    it('warns what will be lost and will not proceed until the exact word is typed', async () => {
      const dialog = await openRestore()
      const go = within(dialog).getByRole('button', { name: 'Restore and restart' })
      const box = within(dialog).getByLabelText(/Type RESTORE/)

      expect(dialog).toHaveTextContent(/Anything added or changed since then is lost/)
      expect(go).toBeDisabled()
      for (const wrong of ['restore', 'RESTOR', 'RESTORE ']) {
        await userEvent.clear(box)
        await userEvent.type(box, wrong)
        expect(go).toBeDisabled()
      }

      await userEvent.clear(box)
      await userEvent.type(box, 'RESTORE')
      expect(go).toBeEnabled()
    })

    it('can be cancelled without doing anything', async () => {
      const dialog = await openRestore()

      await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      expect(api.restoreBackup).not.toHaveBeenCalled()
    })

    it('sends the confirmation, then waits for Chronicle to come back and goes to sign-in', async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true, toFake: ['setTimeout'] })
      vi.mocked(api.restoreBackup).mockResolvedValue()
      const fetchMock = vi.fn().mockResolvedValue({ ok: true })
      vi.stubGlobal('fetch', fetchMock)
      const real = window.location
      const navigations: string[] = []
      Object.defineProperty(window, 'location', {
        configurable: true,
        value: { ...real, set href(v: string) { navigations.push(v) }, get href() { return 'http://localhost/' } },
      })
      localStorage.setItem('chronicle_token', 'chr_sess_old')

      try {
        const dialog = await openRestore()
        await userEvent.type(within(dialog).getByLabelText(/Type RESTORE/), 'RESTORE')
        await userEvent.click(within(dialog).getByRole('button', { name: 'Restore and restart' }))

        expect(api.restoreBackup).toHaveBeenCalledWith('chronicle-20261007-020000-scheduled.zip', 'RESTORE')
        expect(await screen.findByText('Restoring…')).toBeInTheDocument()

        await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
        await waitFor(() => expect(navigations).toContain('/login'))
        expect(fetchMock).toHaveBeenCalledWith('/api/health')
        expect(localStorage.getItem('chronicle_token')).toBeNull()
      } finally {
        Object.defineProperty(window, 'location', { configurable: true, value: real })
        vi.unstubAllGlobals()
      }
    })

    it('stays on the page and shows the reason if the server refuses', async () => {
      vi.mocked(api.restoreBackup).mockRejectedValue(new Error('The backup has been altered or damaged (checksum does not match its manifest).'))
      const dialog = await openRestore()
      await userEvent.type(within(dialog).getByLabelText(/Type RESTORE/), 'RESTORE')

      await userEvent.click(within(dialog).getByRole('button', { name: 'Restore and restart' }))

      expect(await screen.findByText(/checksum does not match/)).toBeInTheDocument()
      expect(screen.queryByText('Restoring…')).not.toBeInTheDocument()
    })
  })
})

describe('formatBytes', () => {
  it.each([
    [0, '0 B'], [1023, '1023 B'], [1024, '1.0 KB'], [1536, '1.5 KB'], [250 * MB, '250 MB'], [5 * 1024 * MB, '5.0 GB'],
  ])('%d -> %s', (bytes, expected) => {
    expect(api.formatBytes(bytes)).toBe(expected)
  })

  it('does not print nonsense for bad input', () => {
    expect(api.formatBytes(-1)).toBe('-')
    expect(api.formatBytes(Number.NaN)).toBe('-')
  })
})
