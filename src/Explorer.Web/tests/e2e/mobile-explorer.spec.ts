import { expect, test } from '@playwright/test'
import { mkdir, readFile, realpath, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { de } from '../../src/locales/de'

const statePath = path.join(path.dirname(fileURLToPath(import.meta.url)), '.runtime.json')

test.afterEach(async ({ page, baseURL }) => {
  await page.request.post('/api/v1/security/pin/disable', { headers: { 'X-Explorer-Request': '1', Origin: baseURL! } }).catch(() => {})
})

test('iPhone PWA navigates a shared folder and edits a text file', async ({ page, request, baseURL }) => {
  test.setTimeout(90_000)
  const { fixtureRoot, extraRoot } = JSON.parse(await readFile(statePath, 'utf8')) as { fixtureRoot: string; extraRoot: string }
  const canonicalExtraRoot = await realpath(extraRoot)
  const canonicalFixtureRoot = await realpath(fixtureRoot)
  const canonicalRecentTarget = await realpath(path.join(fixtureRoot, 'touchprobe.svg'))
  await writeFile(path.join(fixtureRoot, 'undo-fast-check.txt'), 'Undo must remain available for a fast operation.')
  const imageBytes = (await readFile(path.join(fixtureRoot, 'touchprobe.svg'))).byteLength
  const blockedRemoteRequests: string[] = []
  await page.route('https://example.invalid/**', async (route) => {
    blockedRemoteRequests.push(route.request().url())
    await route.abort()
  })
  await page.addInitScript(() => {
    const target = window as typeof window & { __sharedFiles?: string[]; __shareAttempts?: number }
    Object.defineProperty(navigator, 'canShare', { configurable: true, value: () => true })
    Object.defineProperty(navigator, 'share', {
      configurable: true,
      value: async (data: ShareData) => {
        target.__shareAttempts = (target.__shareAttempts ?? 0) + 1
        if (target.__shareAttempts === 1) throw new DOMException('activation expired', 'NotAllowedError')
        target.__sharedFiles = (data.files ?? []).map((file) => `${file.name}:${file.size}`)
      },
    })
  })
  await page.addInitScript((legacyFavoritePath: string) => {
    localStorage.setItem('windows-mobile-explorer:favorites', JSON.stringify([legacyFavoritePath]))
  }, fixtureRoot)
  await page.addInitScript(() => {
    const testWindow = window as Window & {
      __dynamicTypeBaseSize: string
      __setDynamicTypeBaseSize: (pixels: number) => void
    }
    testWindow.__dynamicTypeBaseSize = '20px'
    testWindow.__setDynamicTypeBaseSize = (pixels) => { testWindow.__dynamicTypeBaseSize = `${pixels}px` }

    const nativeSupports = CSS.supports.bind(CSS)
    Object.defineProperty(CSS, 'supports', {
      configurable: true,
      value: (property: string, value?: string) => property === 'font' && value === '-apple-system-body'
        ? true
        : nativeSupports(property, value),
    })

    const nativeGetComputedStyle = window.getComputedStyle.bind(window)
    Object.defineProperty(window, 'getComputedStyle', {
      configurable: true,
      value: (element: Element, pseudoElement?: string | null) => {
        const computed = nativeGetComputedStyle(element, pseudoElement)
        if (!(element instanceof HTMLElement) || element.dataset.dynamicTypeProbe !== 'true') return computed
        return new Proxy(computed, {
          get(target, property) {
            if (property === 'fontSize') return testWindow.__dynamicTypeBaseSize
            const result = Reflect.get(target, property, target)
            return typeof result === 'function' ? result.bind(target) : result
          },
        }) as CSSStyleDeclaration
      },
    })
  })
  await page.goto('/')

  await expect(page.getByRole('heading', { name: 'System' })).toBeVisible()
  await expect(page.locator('.favorite-chip').filter({ hasText: path.basename(fixtureRoot) })).toBeVisible()
  await expect(page.locator('.brand-name')).toHaveText(de.appName)
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  const manifest = await request.get(`${baseURL}/manifest.webmanifest`)
  expect(manifest.ok()).toBeTruthy()
  expect((await manifest.json()).display).toBe('standalone')
  const quickAccess = await request.get(`${baseURL}/api/v1/quick-access`)
  expect(quickAccess.ok()).toBeTruthy()
  for (const entry of await quickAccess.json() as Array<{ path: string }>) {
    const relative = path.win32.relative(canonicalFixtureRoot, entry.path)
    expect(relative === '..' || relative.startsWith(`..${path.win32.sep}`) || path.win32.isAbsolute(relative)).toBe(false)
  }

  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /^Einstellungen/ }).click()
  const settingsDialog = page.getByRole('dialog', { name: 'Einstellungen' })
  await expect(settingsDialog).toBeVisible()
  await expectMobileControls(page)
  const textSize = settingsDialog.getByRole('combobox', { name: 'Textgröße' })
  await expect(textSize).toHaveValue('100')
  const appBaseFontSize = await page.locator('html').evaluate((element) => Number.parseFloat(getComputedStyle(element).fontSize))
  await textSize.selectOption('200')
  await expect.poll(() => page.locator('html').evaluate((element) => getComputedStyle(element).fontSize)).toBe(`${appBaseFontSize * 2}px`)
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await expectMobileControls(page)
  await page.evaluate(() => {
    const testWindow = window as Window & { __setDynamicTypeBaseSize: (pixels: number) => void }
    testWindow.__setDynamicTypeBaseSize(24)
    document.dispatchEvent(new Event('visibilitychange'))
  })
  await expect.poll(() => page.locator('html').evaluate((element) => getComputedStyle(element).fontSize)).toBe('48px')
  await page.evaluate(() => {
    const testWindow = window as Window & { __setDynamicTypeBaseSize: (pixels: number) => void }
    testWindow.__setDynamicTypeBaseSize(20)
    window.dispatchEvent(new Event('pageshow'))
  })
  await textSize.selectOption('100')
  await expect.poll(() => page.locator('html').evaluate((element) => getComputedStyle(element).fontSize)).toBe(`${appBaseFontSize}px`)
  const getShareSettings = async () => await (await request.get(`${baseURL}/api/v1/settings/shares`)).json()
  const systemProtection = settingsDialog.getByRole('switch', { name: 'Systemordner schreibgeschützt halten' })
  await expect(systemProtection).toHaveAttribute('aria-checked', 'true')
  await systemProtection.click()
  await settingsDialog.getByRole('button', { name: 'Speichern', exact: true }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Freigabeeinstellungen gespeichert.' })).toBeVisible()
  await expect.poll(async () => (await getShareSettings()).protectSystemFolders).toBe(false)
  await settingsDialog.getByRole('switch', { name: 'Systemordner schreibgeschützt halten' }).click()
  await settingsDialog.getByRole('button', { name: 'Speichern', exact: true }).click()
  await expect.poll(async () => (await getShareSettings()).protectSystemFolders).toBe(true)

  await settingsDialog.getByLabel('Ordnerpfad').fill(extraRoot)
  await settingsDialog.getByRole('checkbox', { name: 'Diesen Ordner schreibgeschützt freigeben' }).check()
  await settingsDialog.getByRole('button', { name: 'Ordner hinzufügen' }).click()
  await expect(settingsDialog.getByRole('checkbox', { name: `${extraRoot} nur lesbar freigeben` })).toBeChecked()
  await settingsDialog.getByRole('button', { name: 'Speichern', exact: true }).click()
  await expect.poll(async () => (await getShareSettings()).roots.some((root: { path: string; readOnly: boolean }) => root.path === canonicalExtraRoot && root.readOnly)).toBe(true)
  const extraEntries = await request.get(`${baseURL}/api/v1/entries?${new URLSearchParams({ path: extraRoot, pageSize: '10' })}`)
  expect(extraEntries.ok()).toBeTruthy()
  expect((await extraEntries.json()).entries.map((entry: { name: string }) => entry.name)).toContain('zusatz.txt')

  const rejectedWrite = await request.post(`${baseURL}/api/v1/jobs`, {
    headers: { 'X-Explorer-Request': '1', Origin: baseURL! },
    data: { kind: 'new-file', sources: [], destination: extraRoot, newName: 'darf-nicht-entstehen.txt', template: 'text' },
  })
  expect(rejectedWrite.status()).toBe(202)
  const queuedWrite = await rejectedWrite.json()
  await expect.poll(async () => {
    const jobsResponse = await request.get(`${baseURL}/api/v1/jobs`)
    const jobs = await jobsResponse.json()
    return jobs.find((job: { id: string }) => job.id === queuedWrite.id)?.status
  }).toBe('failed')

  await settingsDialog.getByRole('button', { name: `Freigabe für ${canonicalExtraRoot} entfernen` }).click()
  await settingsDialog.getByRole('button', { name: 'Speichern', exact: true }).click()
  await expect.poll(async () => (await getShareSettings()).roots.some((root: { path: string }) => root.path === canonicalExtraRoot)).toBe(false)
  const removedEntries = await request.get(`${baseURL}/api/v1/entries?${new URLSearchParams({ path: extraRoot, pageSize: '1' })}`)
  expect(removedEntries.status()).toBe(403)
  await settingsDialog.getByRole('button', { name: 'Schließen' }).click()
  await page.getByRole('button', { name: 'Mehr', exact: true }).click()

  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /^Zuletzt/ }).click()
  await expect(page.getByRole('heading', { name: 'Zuletzt am PC verwendet' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Datei touchprobe.svg' })).toBeVisible()
  const windowsRecent = await request.get(`${baseURL}/api/v1/windows-recent`)
  expect(windowsRecent.ok()).toBeTruthy()
  expect((await windowsRecent.json()).map((entry: { path: string }) => entry.path)).toContain(canonicalRecentTarget)
  await page.getByRole('button', { name: 'Dateien', exact: true }).click()

  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /Pfad öffnen/ }).click()
  await page.getByRole('textbox', { name: 'Ordnerpfad' }).fill(fixtureRoot)
  await page.getByRole('button', { name: 'Ordner öffnen', exact: true }).click()
  await expect(page.getByRole('heading', { name: path.basename(fixtureRoot) })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Favorit entfernen' })).toBeVisible()
  const favoritePaths = await request.get(`${baseURL}/api/v1/favorites`)
  expect(favoritePaths.ok()).toBeTruthy()
  expect(await favoritePaths.json()).toContain(canonicalFixtureRoot)
  await page.getByRole('button', { name: 'Zurück' }).click()
  const favoriteChip = page.locator('.favorite-chip').filter({ hasText: path.basename(fixtureRoot) })
  await expect(favoriteChip).toBeVisible()
  await page.reload()
  await expect(page.locator('.favorite-chip').filter({ hasText: path.basename(fixtureRoot) })).toBeVisible()
  await page.locator('.favorite-chip').filter({ hasText: path.basename(fixtureRoot) }).click()
  await expect(page.getByRole('heading', { name: path.basename(fixtureRoot) })).toBeVisible()

  const largeFolderHeading = page.getByRole('heading', { name: '50.000 Einträge', exact: true })
  await page.getByRole('button', { name: /Ordner 50\.000 Einträge/ }).click()
  await expect(largeFolderHeading).toBeVisible({ timeout: 15_000 })
  await page.reload()
  await expect(largeFolderHeading).toBeVisible({ timeout: 15_000 })
  await page.evaluate(() => window.history.back())
  await expect(page.getByRole('heading', { name: path.basename(fixtureRoot) })).toBeVisible()
  await page.evaluate(() => window.history.forward())
  await expect(largeFolderHeading).toBeVisible()
  await page.getByRole('button', { name: 'Zurück' }).click()
  await expect(page.getByRole('heading', { name: path.basename(fixtureRoot) })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('heading', { name: path.basename(fixtureRoot) })).toBeVisible({ timeout: 15_000 })
  await page.getByRole('button', { name: 'Vor', exact: true }).click()
  await expect(largeFolderHeading).toBeVisible({ timeout: 15_000 })
  await page.getByRole('button', { name: 'Zurück' }).click()
  await expect(page.getByRole('heading', { name: path.basename(fixtureRoot) })).toBeVisible()

  await page.getByRole('button', { name: 'Datei Zielverknüpfung.lnk' }).click()
  const shortcutTargetPreview = page.getByText('Vorschau aus dem Ziel der Windows-Verknüpfung.')
  await expect(shortcutTargetPreview).toBeVisible()
  await page.getByRole('button', { name: 'Schließen' }).click()
  await expect(shortcutTargetPreview).toHaveCount(0)

  await page.locator('.workspace').evaluate((element) => { element.scrollTop = 0 })
  await page.getByRole('button', { name: /Ordner 50\.000 Einträge/ }).click()
  await expect(page.getByText('50.000 Elemente')).toBeVisible({ timeout: 15_000 })
  let pullRefreshRequests = 0
  page.on('request', (requestEvent) => {
    if (new URL(requestEvent.url()).searchParams.get('refresh') === 'true') pullRefreshRequests++
  })
  await page.locator('.workspace.folder-scroll').evaluate((element) => {
    element.scrollTop = 0
    const dispatchTouch = (type: 'touchstart' | 'touchend', clientY: number) => {
      const event = new Event(type, { bubbles: true, cancelable: true })
      const touch = { clientY }
      Object.defineProperty(event, 'touches', { value: type === 'touchstart' ? [touch] : [] })
      Object.defineProperty(event, 'changedTouches', { value: [touch] })
      element.dispatchEvent(event)
    }
    dispatchTouch('touchstart', 80)
    dispatchTouch('touchend', 170)
  })
  await expect(page.locator('.pull-indicator')).toBeVisible()
  await expect.poll(() => pullRefreshRequests).toBe(1)
  await page.setViewportSize({ width: 320, height: 700 })
  await expect.poll(() => page.locator('.entry-card').count()).toBeLessThan(80)
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  const smallLabelSizes = await page.locator('.entry-name, .entry-subtitle, .entry-meta, .entry-size, .grid-entry-name, .grid-entry-meta, .nav-item').evaluateAll((elements) => elements
    .filter((element) => element.getBoundingClientRect().width > 0)
    .map((element) => Number.parseFloat(getComputedStyle(element).fontSize)))
  expect(smallLabelSizes.length).toBeGreaterThan(0)
  expect(Math.min(...smallLabelSizes)).toBeGreaterThanOrEqual(12)
  await expectMobileControls(page)
  await page.getByRole('button', { name: 'Auswählen', exact: true }).click()
  const selectionBar = page.getByRole('toolbar', { name: 'Mehrfachauswahl' })
  await expect(selectionBar).toBeVisible()
  await expect(selectionBar.locator('strong')).toHaveText('0 ausgewählt')
  const firstEntry = page.locator('.entry-card').first()
  await firstEntry.click()
  await expect(selectionBar.locator('strong')).toHaveText('1 ausgewählt')
  await firstEntry.click()
  await expect(selectionBar.locator('strong')).toHaveText('0 ausgewählt')
  await selectionBar.getByRole('button', { name: 'Alle auswählen' }).click()
  await expect(selectionBar.locator('strong')).toHaveText('50.000 ausgewählt', { timeout: 30_000 })
  const selectionBarBounds = await selectionBar.boundingBox()
  const bottomNavBounds = await page.locator('.bottom-nav').boundingBox()
  expect(selectionBarBounds).not.toBeNull()
  expect(bottomNavBounds).not.toBeNull()
  expect(selectionBarBounds!.y).toBeGreaterThan(bottomNavBounds!.y - 90)
  expect(selectionBarBounds!.y + selectionBarBounds!.height).toBeLessThanOrEqual(bottomNavBounds!.y + 1)
  await expectMobileControls(page)
  await selectionBar.getByRole('button', { name: 'Auswahl aufheben' }).click()
  await expect(selectionBar).toHaveCount(0)
  const moreButton = page.getByRole('button', { name: /^Weitere Aktionen für / }).first()
  await expect(moreButton).toBeVisible()
  const moreBounds = await moreButton.boundingBox()
  expect(moreBounds?.width ?? 0).toBeGreaterThanOrEqual(44)
  expect(moreBounds?.height ?? 0).toBeGreaterThanOrEqual(44)
  await moreButton.click()
  const actionDialog = page.getByRole('dialog', { name: /^Aktionen für / })
  await expect(actionDialog).toBeVisible()
  await expectMobileControls(page)
  await expect(actionDialog.getByRole('button', { name: 'Öffnen', exact: true })).toBeVisible()
  await expect(actionDialog.getByRole('button', { name: 'Umbenennen' })).toBeVisible()
  await expect(actionDialog.getByRole('button', { name: 'Löschen', exact: true })).toBeVisible()
  const closeActions = actionDialog.getByRole('button', { name: 'Schließen' })
  await expect(closeActions).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(actionDialog).toHaveCount(0)
  await expect(moreButton).toBeFocused()
  await page.getByRole('button', { name: 'Sortierung und Filter', exact: true }).click()
  await page.getByLabel('Gruppieren nach').selectOption('type')
  await expect(page.getByText('TXT-Dateien', { exact: true })).toBeVisible()
  await page.getByLabel('Gruppieren nach').selectOption('none')
  await page.getByRole('button', { name: 'Sortierung und Filter', exact: true }).click()
  await page.getByRole('button', { name: 'Zurück' }).click()
  await expect(page.getByRole('heading', { name: path.basename(fixtureRoot) })).toBeVisible()

  const undoFile = page.getByRole('button', { name: /Datei undo-fast-check\.txt/ })
  await expect(undoFile).toBeVisible()
  await page.getByRole('button', { name: 'Weitere Aktionen für undo-fast-check.txt' }).click()
  const undoFileActions = page.getByRole('dialog', { name: 'Aktionen für undo-fast-check.txt' })
  await undoFileActions.getByRole('button', { name: 'Löschen', exact: true }).click()
  const undoToast = page.getByRole('status').filter({ has: page.getByRole('button', { name: 'Rückgängig' }) })
  await expect(undoToast.getByRole('button', { name: 'Rückgängig' })).toBeVisible()
  await undoToast.getByRole('button', { name: 'Rückgängig' }).click()
  await expect(undoFile).toBeVisible({ timeout: 15_000 })

  const imageEntry = page.getByRole('button', { name: /Datei touchprobe\.svg/ })
  const imageBounds = await imageEntry.boundingBox()
  expect(imageBounds).not.toBeNull()
  const longPressX = imageBounds!.x + imageBounds!.width / 2
  const longPressY = imageBounds!.y + imageBounds!.height / 2
  const gestureActions = page.getByRole('dialog', { name: 'Aktionen für touchprobe.svg' })
  await imageEntry.dispatchEvent('pointerdown', { pointerType: 'touch', pointerId: 71, isPrimary: true, button: 0, buttons: 1, clientX: longPressX, clientY: longPressY })
  await expect(gestureActions).toBeVisible()
  await imageEntry.dispatchEvent('pointerup', { pointerType: 'touch', pointerId: 71, isPrimary: true, button: 0, buttons: 0, clientX: longPressX, clientY: longPressY })
  await expect(gestureActions).toBeVisible()
  await expect(gestureActions.getByRole('button', { name: 'Teilen', exact: true })).toBeVisible()
  await expect(gestureActions.getByRole('button', { name: 'Umbenennen', exact: true })).toBeVisible()
  await expect(gestureActions.getByRole('button', { name: 'Löschen', exact: true })).toBeVisible()
  await gestureActions.getByRole('button', { name: 'Teilen', exact: true }).click()
  const shareRetryToast = page.getByRole('status').filter({ hasText: 'Die Datei ist bereit.' })
  await expect(shareRetryToast.getByRole('button', { name: 'Teilen' })).toBeVisible()
  await shareRetryToast.getByRole('button', { name: 'Teilen' }).click()
  await expect.poll(() => page.evaluate(() => (window as typeof window & { __sharedFiles?: string[] }).__sharedFiles)).toEqual([`touchprobe.svg:${imageBytes}`])
  await expect.poll(() => page.evaluate(() => (window as typeof window & { __shareAttempts?: number }).__shareAttempts)).toBe(2)
  await expect(gestureActions).toHaveCount(0)

  const swipeStartX = imageBounds!.x + imageBounds!.width * 0.78
  const swipeEndX = swipeStartX - 90
  await imageEntry.dispatchEvent('pointerdown', { pointerType: 'touch', pointerId: 72, isPrimary: true, button: 0, buttons: 1, clientX: swipeStartX, clientY: longPressY })
  await imageEntry.dispatchEvent('pointermove', { pointerType: 'touch', pointerId: 72, isPrimary: true, button: 0, buttons: 1, clientX: swipeEndX, clientY: longPressY })
  await imageEntry.dispatchEvent('pointerup', { pointerType: 'touch', pointerId: 72, isPrimary: true, button: 0, buttons: 0, clientX: swipeEndX, clientY: longPressY })
  await expect(gestureActions).toBeVisible()
  await expect(gestureActions.getByRole('button', { name: 'Löschen', exact: true })).toBeVisible()
  await gestureActions.getByRole('button', { name: 'Schließen' }).click()

  await imageEntry.click()
  const previewImage = page.locator('.preview-stage > img')
  await expect(previewImage).toBeVisible()
  await expect(page.getByText('100%', { exact: true })).toBeVisible()
  await expectMobileControls(page)
  await previewImage.evaluate((image: HTMLImageElement) => {
    image.setPointerCapture = () => {}
    const pointer = (type: string, pointerId: number, clientX: number) => new PointerEvent(type, { bubbles: true, pointerId, pointerType: 'touch', clientX, clientY: 120 })
    image.dispatchEvent(pointer('pointerdown', 1, 100))
    image.dispatchEvent(pointer('pointerdown', 2, 200))
    image.dispatchEvent(pointer('pointermove', 2, 250))
    image.dispatchEvent(pointer('pointerup', 2, 250))
    image.dispatchEvent(pointer('pointerup', 1, 100))
  })
  await expect(page.getByText('150%', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Schließen' }).click()

  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /Neue Textdatei/ }).click()
  const newFileDialog = page.getByRole('dialog', { name: 'Neue Datei erstellen' })
  await expect(newFileDialog).toBeVisible()
  await expect(page.getByLabel('Dateiname')).toBeFocused()
  await expectMobileControls(page)
  await page.getByLabel('Vorlage').selectOption('markdown')
  await page.getByLabel('Dateiname').fill('iphone-e2e.md')
  await page.getByRole('button', { name: 'Erstellen', exact: true }).click()
  await page.getByRole('button', { name: 'Dateien', exact: true }).click()
  const fileButton = page.getByRole('button', { name: /Datei iphone-e2e\.md/ })
  await expect(fileButton).toBeVisible({ timeout: 15_000 })
  await fileButton.click()
  const previewDialog = page.getByRole('dialog', { name: 'Vorschau für iphone-e2e.md' })
  await expect(previewDialog).toBeVisible()
  await expect(previewDialog.getByRole('button', { name: 'Schließen' })).toBeFocused()
  const detailsButton = previewDialog.getByRole('button', { name: 'Details' })
  await detailsButton.click()
  const propertiesDialog = page.getByRole('dialog', { name: 'Eigenschaften' })
  await expect(propertiesDialog).toBeVisible()
  await expect(propertiesDialog.getByRole('button', { name: 'Schließen' })).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(propertiesDialog).toHaveCount(0)
  await expect(previewDialog).toBeVisible()
  await expect(detailsButton).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(previewDialog).toHaveCount(0)
  await expect(fileButton).toBeFocused()
  await fileButton.click()
  await expect(page.getByRole('button', { name: 'Bearbeiten' })).toBeVisible()
  await page.getByRole('button', { name: 'Bearbeiten' }).click()
  const editor = page.getByRole('textbox', { name: 'Inhalt von iphone-e2e.md' })
  await expect(editor).toBeVisible()
  await expectMobileControls(page)
  await editor.fill('# Vom iPhone\n\nDer mobile Editor funktioniert.\n\n- Erster Punkt\n\n![Lokales Bild](touchprobe.svg)\n\n![Externes Bild](https://example.invalid/track.png)\n\n<img src="https://example.invalid/raw.png" srcset="https://example.invalid/raw@2x.png 2x" alt="Raw externes Bild" onerror="window.__markdownRawPwned = true">\n\n<script>window.__markdownPwned = true</script>')
  await page.keyboard.press('Escape')
  await expect(editor).toBeVisible()
  await page.getByRole('button', { name: /Speichern/ }).click()
  await expect(page.locator('.toast')).toContainText('Datei gespeichert')

  const saved = await request.get(`${baseURL}/api/v1/text?${new URLSearchParams({ path: path.join(fixtureRoot, 'iphone-e2e.md') })}`)
  expect(saved.ok()).toBeTruthy()
  expect((await saved.json()).content).toBe('# Vom iPhone\n\nDer mobile Editor funktioniert.\n\n- Erster Punkt\n\n![Lokales Bild](touchprobe.svg)\n\n![Externes Bild](https://example.invalid/track.png)\n\n<img src="https://example.invalid/raw.png" srcset="https://example.invalid/raw@2x.png 2x" alt="Raw externes Bild" onerror="window.__markdownRawPwned = true">\n\n<script>window.__markdownPwned = true</script>')

  await page.getByRole('button', { name: 'Editor schließen' }).click()
  await fileButton.click()
  const markdownPreview = page.locator('.markdown-preview')
  await expect(markdownPreview.getByRole('heading', { name: 'Vom iPhone' })).toBeVisible()
  await expectMobileControls(page)
  await expect(markdownPreview.getByText('Erster Punkt')).toBeVisible()
  const localMarkdownImage = markdownPreview.locator('img[alt="Lokales Bild"]')
  await expect(localMarkdownImage).toHaveAttribute('src', /\/api\/v1\/thumbnail\?path=/)
  await expect.poll(() => localMarkdownImage.evaluate((image: HTMLImageElement) => image.complete && image.naturalWidth > 0)).toBe(true)
  await expect(markdownPreview.locator('img')).toHaveCount(1)
  expect(await page.evaluate(() => (window as typeof window & { __markdownPwned?: boolean }).__markdownPwned)).toBeUndefined()
  expect(await page.evaluate(() => (window as typeof window & { __markdownRawPwned?: boolean }).__markdownRawPwned)).toBeUndefined()
  await expect(markdownPreview.locator('[srcset]')).toHaveCount(0)
  await expect(markdownPreview.getByText('Raw externes Bild')).toBeVisible()
  expect(blockedRemoteRequests).toEqual([])
  await page.getByRole('button', { name: 'Schließen' }).click()

  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /Neue Textdatei/ }).click()
  await page.getByLabel('Vorlage').selectOption('javascript')
  await page.getByLabel('Dateiname').fill('iphone-e2e.js')
  await page.getByRole('button', { name: 'Erstellen', exact: true }).click()
  await page.getByRole('button', { name: 'Dateien', exact: true }).click()
  const codeFileButton = page.getByRole('button', { name: /Datei iphone-e2e\.js/ })
  await expect(codeFileButton).toBeVisible({ timeout: 15_000 })
  await codeFileButton.click()
  await page.getByRole('button', { name: 'Bearbeiten' }).click()
  await page.getByRole('textbox', { name: 'Inhalt von iphone-e2e.js' }).fill('const answer = 42;\nconsole.log(answer);')
  await page.getByRole('button', { name: /Speichern/ }).click()
  await expect(page.locator('.toast')).toContainText('Datei gespeichert')
  await page.getByRole('button', { name: 'Editor schließen' }).click()
  await codeFileButton.click()
  await expect(page.locator('.code-preview code.hljs .hljs-keyword')).toContainText('const')
  await expect(page.locator('.code-preview code.hljs .hljs-number')).toContainText('42')
  await page.getByRole('button', { name: 'Schließen' }).click()

  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /^Einstellungen/ }).click()
  const systemSwitch = page.getByRole('switch', { name: 'Systemdateien' })
  const extensionSwitch = page.getByRole('switch', { name: 'Dateiendungen' })
  await expect(systemSwitch).toBeVisible()
  await expectMobileControls(page)
  await systemSwitch.click()
  await expect.poll(() => page.evaluate(() => localStorage.getItem('windows-mobile-explorer:show-system'))).toBe('true')
  await systemSwitch.click()
  await extensionSwitch.click()
  await expect.poll(() => page.evaluate(() => localStorage.getItem('windows-mobile-explorer:show-extensions'))).toBe('false')
  await extensionSwitch.click()
  await page.getByRole('button', { name: 'Schließen' }).click()
  await page.getByRole('button', { name: 'Mehr', exact: true }).click()

  await page.getByRole('button', { name: 'Sortierung und Filter', exact: true }).click()
  const extensionFilter = page.getByLabel('Endung')
  await extensionFilter.fill('md')
  await expect(page.getByRole('button', { name: /Datei iphone-e2e\.md/ })).toBeVisible()
  await extensionFilter.fill('pdf')
  await expect(page.getByRole('button', { name: /Datei iphone-e2e\.md/ })).toHaveCount(0)

  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /PIN einrichten/ }).click()
  const pinSettings = page.getByRole('dialog', { name: 'PIN einrichten' })
  await expectMobileControls(page)
  await pinSettings.getByRole('textbox', { name: 'PIN eingeben' }).fill('849201')
  await pinSettings.getByRole('textbox', { name: 'PIN wiederholen' }).fill('849201')
  await pinSettings.getByRole('button', { name: 'PIN einrichten' }).click()
  await expect(pinSettings).toHaveCount(0)

  await page.getByRole('button', { name: 'App jetzt sperren' }).click()
  await expect(page.getByRole('heading', { name: 'Dateiablage gesperrt' })).toBeVisible()
  await expectMobileControls(page)
  const protectedResponse = await page.evaluate(async () => {
    const response = await fetch('/api/v1/drives')
    return { status: response.status, body: await response.json() }
  })
  expect(protectedResponse.status).toBe(423)
  expect(protectedResponse.body.code).toBe('pin_locked')

  const pinInput = page.getByRole('textbox', { name: 'PIN eingeben' })
  await pinInput.fill('111111')
  await page.getByRole('button', { name: 'Dateiablage entsperren' }).click()
  await expect(page.getByRole('alert')).toContainText('PIN ist nicht korrekt')
  await pinInput.fill('849201')
  await page.getByRole('button', { name: 'Dateiablage entsperren' }).click()
  await expect(page.getByRole('heading', { name: 'Mehr', exact: true })).toBeVisible()

  await page.reload()
  const lockedAfterReload = page.getByRole('heading', { name: 'Dateiablage gesperrt' })
  if (await lockedAfterReload.isVisible().catch(() => false)) {
    await page.getByRole('textbox', { name: 'PIN eingeben' }).fill('849201')
    await page.getByRole('button', { name: 'Dateiablage entsperren' }).click()
  }
  const restoredFolder = page.getByRole('heading', { name: path.basename(fixtureRoot), exact: true })
  if (!(await restoredFolder.isVisible().catch(() => false))) {
    await page.getByRole('button', { name: path.basename(fixtureRoot), exact: true }).click()
  }
  await expect(restoredFolder).toBeVisible()
  await expect.poll(() => page.evaluate(() => localStorage.getItem('windows-mobile-explorer:last-path'))).toContain(path.basename(fixtureRoot))
  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: 'App jetzt sperren' }).click()
  await expect(page.getByRole('heading', { name: 'Dateiablage gesperrt' })).toBeVisible()
  await page.getByRole('textbox', { name: 'PIN eingeben' }).fill('849201')
  await page.getByRole('button', { name: 'Dateiablage entsperren' }).click()
  await expect(page.getByRole('heading', { name: 'Mehr', exact: true })).toBeVisible()
})

test.describe('change stream connectivity', () => {
test.use({ serviceWorkers: 'block' })

test('a change-stream reconnect does not show offline while the API remains reachable', async ({ page }) => {
  const { fixtureRoot } = JSON.parse(await readFile(statePath, 'utf8')) as { fixtureRoot: string }
  await page.addInitScript(() => {
    const target = window as Window & { __watchErrorSettled?: boolean }
    const NativeEventSource = window.EventSource
    class TrackingEventSource extends NativeEventSource {
      constructor(url: string | URL, init?: EventSourceInit) {
        super(url, init)
        this.addEventListener('error', () => { window.setTimeout(() => { target.__watchErrorSettled = true }, 0) })
      }
    }
    Object.defineProperty(window, 'EventSource', { configurable: true, value: TrackingEventSource })
  })

  let blockedWatchRequests = 0
  await page.route(/\/api\/v1\/watch(?:\?|$)/, async (route) => {
    blockedWatchRequests++
    await route.abort()
  })
  await page.goto('/')
  const pinGate = page.getByRole('heading', { name: 'Dateiablage gesperrt' })
  const pcHeading = page.getByRole('heading', { name: 'System' })
  await expect(pinGate.or(pcHeading).first()).toBeVisible()
  if (await pinGate.isVisible().catch(() => false)) {
    await page.getByRole('textbox', { name: 'PIN eingeben' }).fill('849201')
    await page.getByRole('button', { name: 'Dateiablage entsperren' }).click()
  }
  await expect(pcHeading).toBeVisible()
  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /Pfad öffnen/ }).click()
  await page.getByRole('textbox', { name: 'Ordnerpfad' }).fill(fixtureRoot)
  await page.getByRole('button', { name: 'Ordner öffnen', exact: true }).click()
  await expect(page.getByRole('heading', { name: path.basename(fixtureRoot) })).toBeVisible()
  await expect.poll(() => blockedWatchRequests).toBeGreaterThan(0)
  await expect.poll(() => page.evaluate(() => Boolean((window as Window & { __watchErrorSettled?: boolean }).__watchErrorSettled))).toBe(true)
  await expect(page.locator('.connection-pill')).toHaveText('Verbunden', { timeout: 7_000 })
})
})

test.describe('resumable upload network recovery', () => {
test.use({ serviceWorkers: 'block' })

test('iPhone upload resumes automatically when a committed chunk reply is lost', async ({ page, request, baseURL }) => {
  const { fixtureRoot } = JSON.parse(await readFile(statePath, 'utf8')) as { fixtureRoot: string }
  const fileName = 'Netzabbruch-Wiederaufnahme.bin'
  const filePath = path.join(fixtureRoot, fileName)
  const destination = path.join(fixtureRoot, 'upload-destination')
  const payload = Buffer.alloc(8 * 1024 * 1024, 0x5a)
  await mkdir(destination, { recursive: true })
  await writeFile(filePath, payload)
  let lostChunkReply = false
  let reconciledSession = false
  await page.route(/\/api\/v1\/uploads\/[^/]+\/chunk(?:\?|$)/, async (route) => {
    if (!lostChunkReply) {
      lostChunkReply = true
      const cookie = (await page.context().cookies(baseURL!)).map((item) => `${item.name}=${item.value}`).join('; ')
      const response = await request.put(route.request().url(), {
        data: payload,
        headers: { Origin: baseURL!, Cookie: cookie, 'X-Explorer-Request': '1', 'Content-Type': 'application/octet-stream' },
      })
      expect(response.ok(), await response.text()).toBeTruthy()
      await route.abort('connectionreset')
      return
    }
    await route.continue()
  })
  page.on('request', (requestEvent) => {
    if (requestEvent.method() !== 'POST' || !requestEvent.url().endsWith('/api/v1/uploads')) return
    try { reconciledSession ||= Boolean((requestEvent.postDataJSON() as { resumeId?: string }).resumeId) } catch { /* Ignore unrelated malformed or bodyless requests. */ }
  })
  await page.addInitScript(() => {
    const target = window as typeof window & { __reconnectSeen?: boolean }
    const observer = new MutationObserver(() => {
      if (document.body?.textContent?.includes('Verbindung wird wiederhergestellt …')) target.__reconnectSeen = true
    })
    observer.observe(document, { subtree: true, childList: true, characterData: true })
  })

  await page.goto('/')
  const pinGate = page.getByRole('heading', { name: 'Dateiablage gesperrt' })
  const pcHeading = page.getByRole('heading', { name: 'System' })
  await expect(pinGate.or(pcHeading).first()).toBeVisible()
  if (await pinGate.isVisible().catch(() => false)) {
    await page.getByRole('textbox', { name: 'PIN eingeben' }).fill('849201')
    await page.getByRole('button', { name: 'Dateiablage entsperren' }).click()
  }
  await expect(pcHeading).toBeVisible()
  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /Pfad öffnen/ }).click()
  await page.getByRole('textbox', { name: 'Ordnerpfad' }).fill(fixtureRoot)
  await page.getByRole('button', { name: 'Ordner öffnen', exact: true }).click()
  await expect(page.getByRole('heading', { name: path.basename(fixtureRoot) })).toBeVisible()
  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: 'Übertragungen', exact: true }).click()
  await page.locator('.upload-toolbar button.primary-button').click()
  await page.locator('input[type="file"]').setInputFiles(filePath)
  const picker = page.getByRole('dialog', { name: 'Dateien hochladen nach' })
  await expect(picker).toBeVisible()
  await picker.locator('.picker-row').filter({ hasText: 'upload-destination' }).click()
  await picker.getByRole('button', { name: 'Diesen Ordner verwenden' }).click()

  await expect(page.locator('.toast').filter({ hasText: `${fileName} hochgeladen` })).toBeVisible({ timeout: 30_000 })
  expect(lostChunkReply).toBe(true)
  expect(reconciledSession).toBe(true)
  expect(await page.evaluate(() => (window as typeof window & { __reconnectSeen?: boolean }).__reconnectSeen)).toBe(true)
  const uploaded = await readFile(path.join(destination, fileName))
  expect(uploaded.equals(payload)).toBe(true)
})
})

async function expectMobileControls(page: import('@playwright/test').Page) {
  const modal = page.locator('[role="dialog"][aria-modal="true"]:visible').last()
  const scope = await modal.count() > 0 ? modal : page
  const audit = await scope.locator('button:visible, select:visible, [role="button"]:visible').evaluateAll((elements) => elements.map((element) => {
    const htmlElement = element as HTMLElement
    const bounds = htmlElement.getBoundingClientRect()
    const labelledBy = htmlElement.getAttribute('aria-labelledby')?.split(/\s+/).map((id) => document.getElementById(id)?.textContent ?? '').join(' ')
    const formLabels = 'labels' in htmlElement ? Array.from((htmlElement as HTMLInputElement).labels ?? []).map((label) => label.textContent ?? '').join(' ') : ''
    const name = htmlElement.getAttribute('aria-label') || labelledBy || htmlElement.getAttribute('title') || formLabels || htmlElement.innerText.trim() || htmlElement.querySelector('svg title')?.textContent?.trim() || ''
    return { name: name.trim(), tag: htmlElement.tagName, width: bounds.width, height: bounds.height }
  }))
  expect(audit.filter((control) => !control.name || control.width < 43.99 || control.height < 43.99)).toEqual([])
}
