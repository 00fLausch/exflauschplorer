// Screenshots use the original frontend with a synthetic API: no PC files are read.
import { spawn, spawnSync } from 'node:child_process'
import { mkdir, readFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium, devices } from '@playwright/test'

const web = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const root = path.resolve(web, '../..')
const output = path.join(root, 'docs', 'screenshots')
await mkdir(output, { recursive: true })
// A silent synthetic hour-long MP3 gives the player a realistic duration.
const audioResult = spawnSync('ffmpeg', ['-loglevel', 'error', '-f', 'lavfi', '-i', 'anullsrc=r=8000:cl=mono', '-t', '3600', '-b:a', '8k', '-f', 'mp3', 'pipe:1'], { maxBuffer: 16 * 1024 * 1024, windowsHide: true })
const audio = audioResult.status === 0 ? audioResult.stdout : await readFile(path.join(web, 'tests/e2e/fixtures/iphone-preview.mp3'))
const server = spawn(process.execPath, [path.join(web, 'node_modules/vite/bin/vite.js'), '--host', '127.0.0.1', '--port', '5198', '--strictPort'], { cwd: web, windowsHide: true, stdio: 'ignore' })
const browser = await chromium.launch({ channel: 'chrome' })
try {
  for (let i = 0; ; i++) {
    try { if ((await fetch('http://127.0.0.1:5198/')).ok) break } catch { /* wait for Vite */ }
    if (i > 80 || server.exitCode !== null) throw new Error('Demo server did not start')
    await new Promise((resolve) => setTimeout(resolve, 250))
  }
  const context = await browser.newContext({ ...devices['iPhone 13'], serviceWorkers: 'block', locale: 'de-DE' })
  const page = await context.newPage()
  const date = '2026-10-15T10:30:00Z'
  const item = (name, parent, directory = true, size = null) => ({ name, path: `${parent}\\${name}`, isDirectory: directory, size, modified: date, created: date, extension: directory ? '' : name.split('.').at(-1), contentType: directory ? '' : 'audio/mpeg', isHidden: false, isReadOnly: false, isLink: false })
  const quick = [item('Projekte', 'D:'), item('DJ-Sets', 'D:\\Musik'), item('Fotos', 'D:'), item('Ideen', 'D:\\Projekte')]
  const known = ['Desktop', 'Dokumente', 'Downloads', 'Bilder', 'Videos', 'Musik'].map((name, i) => ({ id: ['desktop','documents','downloads','pictures','videos','music'][i], name, path: `C:\\Users\\Alex\\${name}`, available: true }))
  const tracks = ['Afterglow – Live Set.mp3', 'Coastal Drive.mp3', 'Golden Hour Mix.mp3', 'Midnight Sessions.mp3', 'Sunday Morning.mp3'].map((name, i) => item(name, 'D:\\Musik\\DJ-Sets', false, (80 + i * 35) * 1024 * 1024))
  const folders = [item('DJ-Sets', 'D:\\Musik'), item('Alben', 'D:\\Musik'), item('Favoriten', 'D:\\Musik'), item('Playlists', 'D:\\Musik'), item('Samples', 'D:\\Musik')]
  await page.route('**/api/v1/**', async (route) => {
    const url = new URL(route.request().url())
    const endpoint = url.pathname.replace('/api/v1/', '')
    if (endpoint === 'stream') return route.fulfill({ status: 200, contentType: 'audio/mpeg', body: audio })
    if (endpoint === 'watch' || endpoint === 'jobs/events') return route.fulfill({ status: 204 })
    const data = {
      health: { ok: true, version: '0.1.0', user: 'demo@example.com', readOnly: false, shareAllDrives: true },
      drives: [{ name: 'C:\\', displayName: 'System', driveType: 'Fixed', format: 'NTFS', totalBytes: 1024 ** 4, freeBytes: 430 * 1024 ** 3, ready: true, readOnly: false }, { name: 'D:\\', displayName: 'Medien', driveType: 'Fixed', format: 'NTFS', totalBytes: 2 * 1024 ** 4, freeBytes: 1.1 * 1024 ** 4, ready: true, readOnly: false }],
      'known-folders': known, 'quick-access': quick, favorites: [], 'windows-recent': [], jobs: [], uploads: [],
      'security/pin/status': { isConfigured: false, isUnlocked: true, retryAfterSeconds: 0 },
      'settings/shares': { shareAllDrives: true, readOnlyMode: false, protectSystemFolders: true, roots: [] },
      waveform: { duration: 3600, peaks: Array.from({ length: 170 }, (_, i) => 0.15 + 0.75 * Math.abs(Math.sin(i * 0.73) * Math.cos(i * 0.13))) },
    }
    if (endpoint === 'entries') {
      const folder = url.searchParams.get('path')
      const entries = folder?.includes('DJ-Sets') ? tracks : folders
      return route.fulfill({ json: { path: folder, entries, skip: 0, pageSize: 160, totalCount: entries.length, hasMore: false } })
    }
    return route.fulfill({ json: data[endpoint] ?? [] })
  })
  await page.goto('http://127.0.0.1:5198/', { waitUntil: 'domcontentloaded' })
  await page.getByText('Fast Access').waitFor()
  await page.screenshot({ path: path.join(output, '01-system.png') })
  await page.getByRole('button', { name: /DJ-Sets/ }).first().click()
  await page.getByText('Afterglow – Live Set.mp3').waitFor()
  await page.screenshot({ path: path.join(output, '02-dateien.png') })
  await page.getByText('Afterglow – Live Set.mp3').click()
  await page.getByRole('dialog').waitFor()
  await page.getByText(/Stream wird geladen/).waitFor({ state: 'hidden', timeout: 10000 }).catch(() => {})
  if (await page.getByText(/Dieses Medienformat/).count()) throw new Error('Demo audio playback failed')
  await page.screenshot({ path: path.join(output, '03-player.png') })
  console.log('Drei Screenshots mit ausschließlich synthetischen Daten erstellt.')
} finally {
  await browser.close()
  server.kill()
}
