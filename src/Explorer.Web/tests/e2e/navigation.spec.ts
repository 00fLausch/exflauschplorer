import { expect, test as base } from '@playwright/test'
import { execFile } from 'node:child_process'
import { mkdir, mkdtemp, readFile, realpath, rm, stat, utimes, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import type { Drive, FileEntry, JobSnapshot, KnownFolder, TextDocument } from '../../src/types'
import { formatDate } from '../../src/api'

const statePath = path.join(path.dirname(fileURLToPath(import.meta.url)), '.runtime.json')
const execute = promisify(execFile)

interface NavigationFolders {
  root: string
  apps: string
  desktop: string
  downloads: string
  favorite: string
  readOnly: string
}

const test = base.extend<{ folders: NavigationFolders }>({
  folders: async ({ page, request, baseURL }, runFixture) => {
    const state = JSON.parse(await readFile(statePath, 'utf8')) as { fixtureRoot: string }
    const fixtureRoot = await realpath(state.fixtureRoot)
    const root = await mkdtemp(path.join(fixtureRoot, 'navigation-'))
    const folders: NavigationFolders = {
      root,
      apps: path.join(root, 'Apps'),
      desktop: path.join(root, 'Desktop'),
      downloads: path.join(root, 'Downloads'),
      favorite: path.join(root, 'Mein Favorit'),
      readOnly: path.join(root, 'Apps', 'Attributgeschützter Ordner'),
    }
    for (const folder of [folders.desktop, folders.downloads, folders.favorite, folders.readOnly])
      await mkdir(folder, { recursive: true })
    await writeFile(path.join(folders.readOnly, 'inhalt.txt'), 'Dieser Ordner bleibt trotz Windows-ReadOnly-Attribut zugänglich.')

    // Substitute Windows shell metadata only; directory listing and navigation use the real host.
    const knownFolders: KnownFolder[] = [
      { id: 'desktop', name: 'Desktop', path: folders.desktop, available: true },
      { id: 'downloads', name: 'Downloads', path: folders.downloads, available: true },
    ]
    const quickAccess: FileEntry[] = [{
      name: 'Apps', path: folders.apps, isDirectory: true, size: null,
      modified: '2026-10-27T10:00:00Z', created: '2026-10-27T10:00:00Z',
      extension: '', contentType: 'inode/directory', isHidden: false, isReadOnly: false, isLink: false,
    }]
    const drives: Drive[] = [
      { name: root, displayName: 'Testlaufwerk', driveType: 'Fixed', format: 'NTFS', totalBytes: 100_000, freeBytes: 40_000, ready: true, readOnly: false },
      { name: 'Z:\\', displayName: 'Nicht verbunden', driveType: 'Removable', format: '', totalBytes: null, freeBytes: null, ready: false, readOnly: false },
    ]
    await page.route('**/api/v1/known-folders', (route) => route.fulfill({ json: knownFolders }))
    await page.route('**/api/v1/quick-access', (route) => route.fulfill({ json: quickAccess }))
    await page.route('**/api/v1/drives', (route) => route.fulfill({ json: drives }))

    const headers = { 'X-Explorer-Request': '1', Origin: baseURL! }
    const favoriteResponse = await request.get('/api/v1/favorites')
    expect(favoriteResponse.ok()).toBeTruthy()
    const originalFavorites = await favoriteResponse.json() as string[]
    const saved = await request.put('/api/v1/favorites', {
      headers,
      data: { paths: [...originalFavorites, folders.favorite] },
    })
    expect(saved.ok()).toBeTruthy()

    try {
      await runFixture(folders)
    } finally {
      await execute('attrib.exe', ['-R', folders.readOnly], { windowsHide: true })
      const restored = await request.put('/api/v1/favorites', { headers, data: { paths: originalFavorites } })
      expect(restored.ok()).toBeTruthy()
      const actualRoot = await realpath(root)
      expect(path.dirname(actualRoot).toLowerCase(), 'Navigation cleanup must stay directly inside the isolated fixture root.').toBe(fixtureRoot.toLowerCase())
      expect(path.basename(actualRoot), 'Only navigation fixtures may be removed.').toMatch(/^navigation-/)
      await rm(actualRoot, { recursive: true, force: true })
    }
  },
})

test.describe('Explorer desktop navigation', () => {
  test.use({ viewport: { width: 1280, height: 900 }, isMobile: false, hasTouch: false })

  test('keeps drives, Windows shortcuts and favorites reachable inside folders', async ({ page, folders }) => {
    await page.goto('/')
    const navigation = page.getByRole('navigation', { name: 'Ordnernavigation', exact: true })
    await expect(navigation).toBeVisible()
    await expect(page.getByRole('button', { name: 'Navigation öffnen', exact: true })).toBeHidden()
    await expect(page.getByRole('heading', { name: 'System', exact: true })).toBeVisible()
    await expect(navigation.getByRole('button', { name: 'Dieser PC', exact: true })).toHaveAttribute('aria-current', 'location')

    const home = page.getByRole('main')
    const drive = home.getByRole('button', { name: /Testlaufwerk/ })
    await expect(drive).toBeVisible()
    await expect(home.getByRole('button', { name: /Nicht verbunden/ })).toBeDisabled()
    const drivesHeading = home.getByRole('heading', { name: 'Laufwerke', exact: true })
    const quickAccessHeading = home.getByRole('heading', { name: 'Fast Access', exact: true })
    await expect(drivesHeading).toBeVisible()
    await expect(quickAccessHeading).toBeVisible()
    expect((await drivesHeading.boundingBox())!.y).toBeLessThan((await quickAccessHeading.boundingBox())!.y)

    for (const folder of [folders.apps, folders.desktop, folders.downloads, folders.favorite, folders.root]) {
      const target = navigation.getByTitle(folder, { exact: true })
      await expect(target).toBeEnabled()
      await target.click()
      await expect(page.getByRole('heading', { name: path.basename(folder), exact: true })).toBeVisible()
      await expect(target).toHaveAttribute('aria-current', 'location')
      await expect(navigation).toBeVisible()
      for (const shortcut of [folders.apps, folders.desktop, folders.downloads, folders.favorite, folders.root])
        await expect(navigation.getByTitle(shortcut, { exact: true })).toBeVisible()
    }

    await navigation.getByRole('button', { name: 'Dieser PC', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'System', exact: true })).toBeVisible()
    await expect(drive).toBeVisible()
    await drive.click()
    await expect(page.getByRole('heading', { name: path.basename(folders.root), exact: true })).toBeVisible()
  })

  test('opens and writes inside a directory carrying the Windows read-only attribute', async ({ page, request, baseURL, folders }) => {
    await execute('attrib.exe', ['+R', folders.readOnly], { windowsHide: true })
    const listing = await request.get(`/api/v1/entries?${new URLSearchParams({ path: folders.apps, refresh: 'true' })}`)
    expect(listing.ok()).toBeTruthy()
    const entries = (await listing.json() as { entries: FileEntry[] }).entries
    expect(entries.find((entry) => entry.path === folders.readOnly)?.isReadOnly).toBe(false)

    const headers = { 'X-Explorer-Request': '1', Origin: baseURL! }
    const created = await request.post('/api/v1/jobs', {
      headers,
      data: { kind: 'new-file', sources: [], destination: folders.readOnly, newName: 'neu.txt', template: 'text' },
    })
    expect(created.status()).toBe(202)
    const job = await created.json() as JobSnapshot
    await expect.poll(async () => {
      const response = await request.get('/api/v1/jobs')
      expect(response.ok()).toBeTruthy()
      return (await response.json() as JobSnapshot[]).find((item) => item.id === job.id)?.status
    }).toBe('completed')
    const createdPath = path.join(folders.readOnly, 'neu.txt')
    const documentResponse = await request.get(`/api/v1/text?${new URLSearchParams({ path: createdPath })}`)
    expect(documentResponse.ok()).toBeTruthy()
    const document = await documentResponse.json() as TextDocument
    const content = 'Im Ordner mit Windows-Attribut bearbeitet.'
    const saved = await request.put('/api/v1/text', {
      headers,
      data: { path: document.path, eTag: document.eTag, content },
    })
    expect(saved.ok()).toBeTruthy()
    expect(await readFile(createdPath, 'utf8')).toBe(content)

    await page.goto('/')
    await page.getByRole('navigation', { name: 'Ordnernavigation', exact: true }).getByTitle(folders.apps, { exact: true }).click()
    const protectedFolder = page.getByRole('button', { name: /^Ordner Attributgeschützter Ordner/ })
    await expect(protectedFolder).toBeEnabled()
    await protectedFolder.click()
    await expect(page.getByRole('heading', { name: 'Attributgeschützter Ordner', exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Datei inhalt.txt', exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Datei neu.txt', exact: true })).toBeVisible()
    await expect(page.getByRole('navigation', { name: 'Ordnernavigation', exact: true }).getByTitle(folders.desktop, { exact: true })).toBeVisible()
  })
})

test('slash-separated breadcrumb keeps parent folders clickable', async ({ page, folders }) => {
  await page.goto('/')
  await page.getByRole('main').getByRole('button', { name: 'Apps', exact: true }).click()
  const breadcrumb = page.getByRole('navigation', { name: 'Pfadnavigation' })
  await expect(breadcrumb).toHaveText(folders.apps.replaceAll('\\', '/'))
  await breadcrumb.getByRole('button', { name: `Zu ${path.basename(folders.root)} navigieren` }).click()
  await expect(page.getByRole('heading', { name: path.basename(folders.root), exact: true })).toBeAttached()
  await expect(breadcrumb).toHaveText(folders.root.replaceAll('\\', '/'))
})

test('compact controls keep navigation and folder tools reachable', async ({ page, folders }) => {
  await page.goto('/')
  for (const width of [320, 390, 768, 1024, 1440]) {
    await page.setViewportSize({ width, height: 844 })
    await expect(page.getByRole('heading', { name: 'System', exact: true })).toBeVisible()
    const home = page.getByRole('main')
    const shortcuts = home.locator('.quick-grid').first()
    await expect(shortcuts.getByRole('button', { name: 'Apps', exact: true })).toBeVisible()
    const knownRows = home.locator('.quick-grid').last().locator('.quick-card')
    const firstRow = (await knownRows.nth(0).boundingBox())!
    const secondRow = (await knownRows.nth(1).boundingBox())!
    expect(firstRow.x).toBe(secondRow.x)
    expect(secondRow.y).toBeGreaterThan(firstRow.y)
    await shortcuts.getByRole('button', { name: 'Apps', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Apps', exact: true })).toBeAttached()
    await expect(page.getByRole('button', { name: `Pfad eingeben: ${folders.apps}`, exact: true })).toBeVisible()
    await expect(page.getByRole('navigation', { name: 'Pfadnavigation' })).toBeVisible()
    await expect(page.getByRole('navigation', { name: 'Pfadnavigation' })).toHaveText(folders.apps.replaceAll('\\', '/'))
    await expect(page.getByRole('textbox', { name: 'In diesem Ordner suchen' })).toHaveCount(0)
    await page.getByRole('button', { name: 'In diesem Ordner suchen', exact: true }).click()
    const search = page.getByRole('textbox', { name: 'In diesem Ordner suchen' })
    await expect(search).toBeFocused()
    await search.fill('Attribut')
    await expect(page.getByRole('button', { name: /Ordner Attributgeschützter Ordner/ })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Auch Unterordner durchsuchen' })).toBeEnabled()
    await page.getByRole('button', { name: 'Ansicht', exact: true }).click()
    await expect(search).toBeHidden()
    await page.getByRole('button', { name: 'Kacheln', exact: true }).click()
    await expect(page.locator('.entry-virtual-list')).toHaveClass(/mode-grid/)
    await page.getByRole('button', { name: 'Ansicht', exact: true }).click()
    await page.getByRole('button', { name: 'Liste', exact: true }).click()
    await page.getByRole('button', { name: 'Sortierung und Filter', exact: true }).click()
    await expect(page.getByRole('group', { name: 'Dateityp filtern' })).toBeVisible()
    await page.getByRole('button', { name: 'Sortierung und Filter', exact: true }).click()
    const bottom = page.getByRole('navigation', { name: 'Hauptnavigation' })
    const back = bottom.getByRole('button', { name: 'Zurück', exact: true })
    const forward = bottom.getByRole('button', { name: 'Vor', exact: true })
    const up = bottom.getByRole('button', { name: 'Nach oben', exact: true })
    const filesBox = (await bottom.getByRole('button', { name: 'Dateien', exact: true }).boundingBox())!
    const homeBox = (await bottom.getByRole('button', { name: 'Dieser PC', exact: true }).boundingBox())!
    const moreBox = (await bottom.getByRole('button', { name: 'Mehr', exact: true }).boundingBox())!
    const backBox = (await back.boundingBox())!
    expect(filesBox.x).toBeLessThan(homeBox.x)
    expect(homeBox.x).toBeLessThan(backBox.x)
    expect(backBox.x).toBeLessThan((await forward.boundingBox())!.x)
    expect((await forward.boundingBox())!.x).toBeLessThan((await up.boundingBox())!.x)
    expect((await up.boundingBox())!.x).toBeLessThan(moreBox.x)
    expect(width - (moreBox.x + moreBox.width)).toBeLessThanOrEqual(12)
    await expect(bottom.getByRole('button', { name: 'Zuletzt', exact: true })).toHaveCount(0)
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    await back.click()
    await expect(page.getByRole('heading', { name: 'System', exact: true })).toBeVisible()
    if (width === 390) {
      await forward.click()
      await expect(page.getByRole('heading', { name: 'Apps', exact: true })).toBeAttached()
      await bottom.getByRole('button', { name: 'Dieser PC', exact: true }).click()
      await expect(page.getByRole('heading', { name: 'System', exact: true })).toBeVisible()
    }
  }
  await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('button', { name: 'Mehr' }).click()
  await expect(page.getByRole('button', { name: /Zuletzt/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /Übertragungen/ })).toBeVisible()
})

base('the same top-left control toggles mobile navigation without moving', async ({ page }) => {
  await page.goto('/')
  for (const width of [320, 390, 768]) {
    await page.setViewportSize({ width, height: 844 })
    for (const safeTop of [0, 37]) {
      await page.evaluate((top) => document.documentElement.style.setProperty('--safe-top', `${top}px`), safeTop)
      const toggle = page.locator('#folder-navigation-toggle')
      const original = await toggle.elementHandle()
      const before = (await toggle.boundingBox())!
      const point = { x: before.x + before.width / 2, y: before.y + before.height / 2 }
      await page.mouse.click(point.x, point.y)
      await expect(toggle).toHaveAccessibleName('Navigation schließen')
      await expect(toggle).toHaveAttribute('aria-expanded', 'true')
      await expect(page.getByRole('dialog', { name: 'Ordnernavigation', exact: true })).toBeVisible()
      expect(await toggle.evaluate((element, previous) => element === previous, original)).toBe(true)
      await expect.poll(() => toggle.boundingBox()).toEqual(before)
      expect(await page.evaluate(({ x, y }) => document.elementFromPoint(x, y)?.closest('button')?.id, point)).toBe('folder-navigation-toggle')
      // The close control participates in the drawer's keyboard focus loop.
      await page.keyboard.press('Shift+Tab')
      await expect(toggle).not.toBeFocused()
      await page.keyboard.press('Tab')
      await expect(toggle).toBeFocused()
      await page.mouse.click(point.x, point.y)
      await expect(page.getByRole('dialog', { name: 'Ordnernavigation', exact: true })).toBeHidden()
      await expect(toggle).toHaveAccessibleName('Navigation öffnen')
      await expect(toggle).toHaveAttribute('aria-expanded', 'false')
      await expect(toggle).toBeFocused()
      await original?.dispose()
    }
  }
})

base.describe('large folder refresh', () => {
base.use({ serviceWorkers: 'block' })
base('large folder stays visible and keeps its scroll position during a real folder change', async ({ page }) => {
  base.setTimeout(90_000)
  const { fixtureRoot } = JSON.parse(await readFile(statePath, 'utf8')) as { fixtureRoot: string }
  const folder = path.join(fixtureRoot, '50.000 Einträge')
  await page.addInitScript(() => {
    const NativeEventSource = window.EventSource
    window.EventSource = class extends NativeEventSource {
      constructor(url: string | URL, options?: EventSourceInit) {
        super(url, options)
        this.addEventListener('ready', () => { (window as Window & { __watchReady?: string }).__watchReady = String(url) })
      }
    }
  })
  await page.goto('/')
  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /Pfad öffnen/ }).click()
  await page.getByRole('textbox', { name: 'Ordnerpfad' }).fill(fixtureRoot)
  await page.getByRole('button', { name: 'Ordner öffnen', exact: true }).click()
  await page.getByRole('button', { name: /Ordner 50\.000 Einträge/ }).click()
  await expect(page.getByText('50.000 Elemente')).toBeVisible({ timeout: 20_000 })
  await expect.poll(() => page.evaluate(() => (window as Window & { __watchReady?: string }).__watchReady ?? ''), { timeout: 15_000 }).toContain('50.000')
  const scroll = page.locator('.workspace.folder-scroll')
  await scroll.evaluate((element) => { element.scrollTop = 8000 })
  await expect(page.locator('.folder-footer')).toContainText('320 geladen', { timeout: 15_000 })
  await scroll.evaluate((element) => { element.scrollTop = 9000 })
  await expect.poll(() => scroll.evaluate((element) => element.scrollTop)).toBeGreaterThan(8000)
  const before = await scroll.evaluate((element) => element.scrollTop)
  const visibleEntry = await page.locator('.entry-card').first().getAttribute('aria-label')
  const visibleFile = visibleEntry?.match(/^Datei (datei-\d+\.txt)$/)?.[1]
  expect(visibleFile).toBeTruthy()

  let releaseRefresh!: () => void
  const waiting = new Promise<void>((resolve) => { releaseRefresh = resolve })
  let refreshStarted!: () => void
  const started = new Promise<void>((resolve) => { refreshStarted = resolve })
  await page.route('**/api/v1/entries?*', async (route) => {
    if (new URL(route.request().url()).searchParams.get('refresh') === 'true') {
      refreshStarted()
      await waiting
    }
    await route.continue()
  })
  const refreshResponse = page.waitForResponse((response) => response.url().includes('/api/v1/entries?') && new URL(response.url()).searchParams.get('refresh') === 'true', { timeout: 30_000 })
  await writeFile(path.join(folder, visibleFile!), 'x'.repeat(99))
  try {
    await Promise.race([started, new Promise((_, reject) => setTimeout(() => reject(new Error('Folder change did not trigger a refresh')), 15_000))])
    await expect(page.locator('.entry-card').first()).toHaveAttribute('aria-label', visibleEntry!)
    expect(await scroll.evaluate((element) => element.scrollTop)).toBe(before)
    await expect(page.locator('.folder-loading')).toBeHidden()
  } finally { releaseRefresh() }
  expect((await refreshResponse).ok()).toBeTruthy()
  await expect(page.getByRole('button', { name: visibleEntry!, exact: true })).toContainText('99 Byte', { timeout: 20_000 })
  await expect.poll(() => scroll.evaluate((element) => element.scrollTop), { timeout: 20_000 }).toBe(before)
  await expect(page.locator('.folder-footer')).toContainText('320 geladen', { timeout: 20_000 })
  await expect(page.locator('.entry-card').first()).toHaveAttribute('aria-label', visibleEntry!)

  const firstEntry = page.locator('.entry-card').first()
  await firstEntry.dispatchEvent('pointerdown', { pointerType: 'touch', pointerId: 99, isPrimary: true, clientX: 80, clientY: 200, buttons: 1 })
  await firstEntry.dispatchEvent('pointermove', { pointerType: 'touch', pointerId: 99, isPrimary: true, clientX: 80, clientY: 150, buttons: 1 })
  await page.waitForTimeout(650)
  await expect(page.getByRole('dialog', { name: /^Aktionen für/ })).toBeHidden()
  await firstEntry.dispatchEvent('pointerup', { pointerType: 'touch', pointerId: 99, isPrimary: true, clientX: 80, clientY: 150 })

  const bottom = page.getByRole('navigation', { name: 'Hauptnavigation' })
  const back = bottom.getByRole('button', { name: 'Zurück', exact: true })
  const forward = bottom.getByRole('button', { name: 'Vor', exact: true })
  await expect(back).toBeVisible()
  await expect(forward).toBeVisible()
  expect((await back.boundingBox())!.y).toBeGreaterThan(page.viewportSize()!.height - 100)
  await back.tap()
  await expect(page.getByRole('heading', { name: path.basename(fixtureRoot), exact: true })).toBeVisible()
  await forward.tap()
  await expect(page.getByRole('heading', { name: '50.000 Einträge', exact: true })).toBeVisible()
})
})

test('mobile navigation closes after choosing a folder and returns keyboard focus on dismiss', async ({ page, folders }) => {
  await page.goto('/')
  const open = page.getByRole('button', { name: 'Navigation öffnen', exact: true })
  const navigation = page.getByRole('navigation', { name: 'Ordnernavigation', exact: true })
  await expect(open).toBeVisible()
  await expect(navigation).toBeHidden()

  await open.click()
  await expect(navigation).toBeVisible()
  await navigation.getByTitle(folders.apps, { exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Apps', exact: true })).toBeVisible()
  await expect(navigation).toBeHidden()

  await open.click()
  await expect(navigation.getByTitle(folders.apps, { exact: true })).toHaveAttribute('aria-current', 'location')
  await expect(navigation.getByTitle(folders.desktop, { exact: true })).toBeVisible()
  await expect(navigation.getByTitle(folders.downloads, { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Navigation schließen', exact: true }).click()
  await expect(navigation).toBeHidden()
  await expect(open).toBeFocused()

  await open.click()
  await expect(navigation).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(navigation).toBeHidden()
  await expect(open).toBeFocused()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})

test('compact file rows keep sizes but omit repeated types', async ({ page, folders }) => {
  await writeFile(path.join(folders.apps, 'video.mp4'), Buffer.alloc(2048))
  await page.goto('/')
  await page.getByRole('main').getByRole('button', { name: 'Apps', exact: true }).click()
  const folder = page.getByRole('button', { name: 'Ordner Attributgeschützter Ordner' })
  const file = page.getByRole('button', { name: 'Datei video.mp4' })
  await expect(folder).toBeVisible()
  await expect(folder.locator('.entry-subtitle')).toHaveCount(0)
  await expect(file.locator('.entry-subtitle')).toHaveText('2 KB')
  expect((await file.locator('.entry-name').boundingBox())!.y).toBeCloseTo((await file.locator('.entry-subtitle').boundingBox())!.y, -1)
  expect((await file.textContent())!.toLowerCase().match(/mp4/g)).toHaveLength(1)
  await page.getByRole('button', { name: 'Ansicht', exact: true }).click()
  await page.getByRole('button', { name: 'Details', exact: true }).click()
  await expect(file.locator('.entry-subtitle')).toHaveCount(0)
  await expect(file.locator('.entry-size')).toHaveText('2 KB')
  await page.getByRole('button', { name: 'Ansicht', exact: true }).click()
  await page.getByRole('button', { name: 'Kacheln', exact: true }).click()
  await expect(file.locator('.grid-entry-meta')).toHaveText('2 KB')
  await expect(folder.locator('.grid-entry-meta')).toHaveCount(0)
})

test('folder sizes sort correctly, persist, and important actions lead the menu', async ({ page, folders }) => {
  await mkdir(path.join(folders.apps, 'A-klein'))
  await mkdir(path.join(folders.apps, 'Z-gross', 'Unterordner'), { recursive: true })
  await writeFile(path.join(folders.apps, 'A-klein', 'klein.bin'), Buffer.alloc(1024))
  await writeFile(path.join(folders.apps, 'Z-gross', 'Unterordner', 'gross.bin'), Buffer.alloc(8192))
  await page.goto('/')
  await page.getByRole('main').getByRole('button', { name: 'Apps', exact: true }).click()
  await page.getByRole('button', { name: 'Sortierung und Filter', exact: true }).click()
  await page.getByLabel('Sortieren nach').selectOption('folder-size')
  await page.getByRole('button', { name: 'Aufsteigend sortiert' }).click()
  await page.getByRole('button', { name: 'Sortierung und Filter', exact: true }).click()
  const first = page.locator('.entry-card').first()
  await expect(first).toHaveAttribute('aria-label', 'Ordner Z-gross', { timeout: 20_000 })
  await expect(first).toContainText('8 KB')
  await page.reload()
  await expect(page.getByRole('button', { name: 'Ordner Z-gross' })).toContainText('8 KB', { timeout: 20_000 })
  await expect(page.locator('.entry-card').first()).toHaveAttribute('aria-label', 'Ordner Z-gross')
  await page.getByRole('button', { name: 'Sortierung und Filter', exact: true }).click()
  await expect(page.getByLabel('Sortieren nach')).toHaveValue('folder-size')
  await expect(page.getByRole('button', { name: 'Absteigend sortiert' })).toBeVisible()
  await page.getByRole('button', { name: 'Sortierung und Filter', exact: true }).click()
  await page.getByRole('button', { name: 'Weitere Aktionen für Z-gross' }).click()
  const actions = page.getByRole('dialog', { name: 'Aktionen für Z-gross' }).locator('.action-button')
  await expect(actions.nth(0)).toHaveText('Kopieren')
  await expect(actions.nth(1)).toHaveText('Ausschneiden')
  await expect(actions.nth(2)).toHaveText('Verschieben')
  await expect(actions.nth(3)).toHaveText('Löschen')
  await expect(actions.nth(4)).toHaveText('Eigenschaften')
  for (let index = 0; index < 5; index++) await expect(actions.nth(index)).toHaveClass(/primary-action/)
  await expect(actions.nth(5)).not.toHaveClass(/primary-action/)
  const colors = await actions.evaluateAll((elements) => elements.slice(0, 6).map(element => getComputedStyle(element).backgroundColor))
  expect(colors[0]).not.toBe(colors[5])
})

test.describe('clean preview', () => {
test.use({ serviceWorkers: 'block' })
base('clean mode deletes only the chosen isolated test file', async ({ page }) => {
  const { fixtureRoot } = JSON.parse(await readFile(statePath, 'utf8')) as { fixtureRoot: string }
  const root = await realpath(path.join(fixtureRoot, 'clean-fixture'))
  expect(path.dirname(root).toLowerCase()).toBe((await realpath(fixtureRoot)).toLowerCase())
  const selected = path.join(root, 'selected-clean-test.tmp')
  const untouched = path.join(root, 'untouched-clean-test.tmp')
  const fresh = path.join(root, 'fresh-clean-test.tmp')
  for (const target of [selected, untouched, fresh]) await writeFile(target, Buffer.alloc(1024))
  const old = new Date(Date.now() - 4 * 24 * 60 * 60 * 1000)
  await utimes(selected, old, old)
  await utimes(untouched, old, old)
  await page.goto('/')
  await page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /Special Clean/ }).click()
  await page.getByRole('button', { name: 'Jetzt prüfen' }).click()
  const category = page.locator('.clean-category').filter({ has: page.getByRole('heading', { name: 'Temporäre Dateien' }) })
  await expect(category).toBeVisible()
  await category.getByText('Dateien und Speicherorte anzeigen').click()
  await expect(category.getByText('fresh-clean-test.tmp', { exact: true })).toHaveCount(0)
  await category.locator('.clean-item').filter({ hasText: 'selected-clean-test.tmp' }).getByRole('checkbox').check()
  await page.getByRole('button', { name: 'Auswahl löschen' }).click()
  await page.getByRole('dialog', { name: 'Dateien endgültig löschen?' }).getByRole('button', { name: 'Endgültig löschen' }).click()
  await expect(page.getByRole('status')).toContainText('1 Dateien gelöscht')
  await expect.poll(async () => stat(selected).then(() => true, () => false)).toBe(false)
  expect((await stat(untouched)).size).toBe(1024)
  expect((await stat(fresh)).size).toBe(1024)
})
test('clean preview requires a specific selection and confirmation', async ({ page, folders }) => {
  expect(formatDate('2023-11-21T12:00:00Z')).toBe('21.11.23')
  const filePath = path.join(folders.apps, 'old-cache.tmp')
  let deleteCalls = 0
  let submitted: unknown = null
  await page.route('**/api/v1/clean/scan', route => route.fulfill({ json: { id: 'scan-1', createdUtc: '2026-11-20T10:00:00Z', categories: [{ id: 'temp', title: 'Temporäre Dateien', description: 'Ältere temporäre Dateien', exists: true, truncated: false, items: [{ id: 'item-1', category: 'temp', path: filePath, bytes: 4096, lastWriteUtc: '2026-11-10T10:00:00Z' }] }] } }))
  await page.route('**/api/v1/clean/delete', route => { deleteCalls++; submitted = route.request().postDataJSON(); return route.fulfill({ json: { deleted: 1, skipped: 0, bytesFreed: 4096 } }) })
  await page.goto('/')
  const home = page.getByRole('main')
  const quick = home.getByRole('heading', { name: 'Fast Access', exact: true })
  const important = home.getByRole('heading', { name: 'Important folders', exact: true })
  await expect(quick).toBeVisible()
  await expect(important).toBeVisible()
  expect((await quick.boundingBox())!.x).toBeLessThan((await important.boundingBox())!.x)
  await expect(page.getByRole('button', { name: 'Mehr Optionen' })).toHaveCount(0)
  const more = page.getByRole('navigation', { name: 'Hauptnavigation' }).getByRole('button', { name: 'Mehr', exact: true })
  await more.click()
  await expect(page.getByRole('button', { name: /Neuer Ordner/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /Papierkorb/ })).toHaveClass(/priority-row/)
  await more.click()
  await expect(page.getByRole('heading', { name: 'System', exact: true })).toBeVisible()
  await more.click()
  await page.getByRole('button', { name: /Special Clean/ }).click()
  await page.getByRole('button', { name: 'Jetzt prüfen' }).click()
  await expect(page.getByRole('heading', { name: 'Temporäre Dateien' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Auswahl löschen' })).toBeDisabled()
  await page.getByText('Dateien und Speicherorte anzeigen').click()
  const choice = page.getByRole('checkbox')
  await expect(choice).not.toBeChecked()
  await expect(page.getByText(filePath, { exact: true })).toBeVisible()
  await choice.check()
  await page.getByRole('button', { name: 'Auswahl löschen' }).click()
  const confirmation = page.getByRole('dialog', { name: 'Dateien endgültig löschen?' })
  await expect(confirmation).toBeVisible()
  await confirmation.getByRole('button', { name: 'Abbrechen' }).click()
  expect(deleteCalls).toBe(0)
  await page.getByRole('button', { name: 'Auswahl löschen' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Endgültig löschen' }).click()
  await expect(page.getByRole('status')).toContainText('1 Dateien gelöscht')
  expect(deleteCalls).toBe(1)
  expect(submitted).toEqual({ scanId: 'scan-1', itemIds: ['item-1'] })
})
})
