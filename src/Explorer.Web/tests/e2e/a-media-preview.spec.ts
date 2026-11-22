import { expect, test } from '@playwright/test'
import { mkdir, readFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

test.use({ serviceWorkers: 'block' })

const testDir = path.dirname(fileURLToPath(import.meta.url))
const webDir = path.resolve(testDir, '../..')
const screenshotDir = path.resolve(webDir, '../../docs/verification/iphone-viewport')
const statePath = path.join(testDir, '.runtime.json')

test('iPhone previews and seeks audio/video, shows a shell thumbnail and streams PDFs', async ({ page, baseURL }) => {
  test.setTimeout(90_000)
  const { fixtureRoot, heicFixtureAvailable } = JSON.parse(await readFile(statePath, 'utf8')) as { fixtureRoot: string; heicFixtureAvailable: boolean }
  await mkdir(screenshotDir, { recursive: true })
  await page.goto('/')
  const pinGate = page.getByRole('heading', { name: 'Dateiablage gesperrt' })
  if (await pinGate.isVisible().catch(() => false)) {
    await page.getByRole('textbox', { name: 'PIN eingeben' }).fill('849201')
    await page.getByRole('button', { name: 'Dateiablage entsperren' }).click()
  }
  await expect(page.getByRole('heading', { name: 'System' })).toBeVisible()
  await expect(page.getByText('Verbunden', { exact: true })).toBeVisible({ timeout: 15_000 })
  await expect(page.getByText(/Laufwerke werden gesucht/)).toHaveCount(0, { timeout: 30_000 })
  const sharedRootCard = page.locator('.shared-root-card').first()
  const sharedRootLayout = await sharedRootCard.evaluate((card) => {
    const pathText = card.querySelector('small')!
    const cardRect = card.getBoundingClientRect()
    const pathStyle = getComputedStyle(pathText)
    return {
      viewportWidth: document.documentElement.clientWidth,
      cardRight: cardRect.right,
      pathClientWidth: pathText.clientWidth,
      pathScrollWidth: pathText.scrollWidth,
      pathOverflow: pathStyle.overflowX,
      pathTextOverflow: pathStyle.textOverflow,
    }
  })
  expect(sharedRootLayout.cardRight).toBeLessThanOrEqual(sharedRootLayout.viewportWidth)
  expect(sharedRootLayout.pathTextOverflow).toBe('ellipsis')
  expect(sharedRootLayout.pathScrollWidth).toBeGreaterThan(sharedRootLayout.pathClientWidth)
  await page.screenshot({ path: path.join(screenshotDir, '01-dieser-pc.png'), animations: 'disabled' })
  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /Pfad öffnen/ }).click()
  await page.getByRole('textbox', { name: 'Ordnerpfad' }).fill(fixtureRoot)
  await page.getByRole('button', { name: 'Ordner öffnen', exact: true }).click()
  await expect(page.getByRole('heading', { name: path.basename(fixtureRoot) })).toBeVisible()
  const breadcrumb = page.getByRole('navigation', { name: 'Pfadnavigation' })
  const breadcrumbItems = breadcrumb.getByRole('button')
  expect(await breadcrumbItems.count()).toBeGreaterThan(5)
  await expect(breadcrumbItems.last()).toHaveAttribute('aria-current', 'location')
  await expect.poll(() => breadcrumb.evaluate((element: HTMLElement) => element.scrollLeft + element.clientWidth >= element.scrollWidth - 1)).toBe(true)
  const breadcrumbTargets = await breadcrumbItems.evaluateAll((elements) => elements.map((element) => {
    const bounds = element.getBoundingClientRect()
    return { name: element.getAttribute('aria-label'), width: bounds.width, height: bounds.height }
  }))
  expect(breadcrumbTargets.every((target) => target.name && target.width > 0 && target.height >= 44)).toBe(true)
  const pathEditButton = page.getByRole('button', { name: /^Pfad eingeben:/ })
  await expect(pathEditButton).toBeVisible()
  expect((await pathEditButton.getAttribute('aria-label'))?.toLowerCase()).toContain(path.basename(fixtureRoot).toLowerCase())
  await page.screenshot({ path: path.join(screenshotDir, '02-dateiliste.png'), animations: 'disabled' })
  await page.getByRole('button', { name: 'Ansicht', exact: true }).click()
  await page.getByRole('button', { name: 'Kacheln', exact: true }).click()

  const thumbnailFixture = path.join(fixtureRoot, 'thumbnail-fixture.png')
  const thumbnailResponse = await page.context().request.get(`${baseURL}/api/v1/thumbnail?${new URLSearchParams({ path: thumbnailFixture, size: '160' })}`)
  expect(thumbnailResponse.ok(), await thumbnailResponse.text()).toBeTruthy()
  expect(thumbnailResponse.headers()['content-type']).toContain('image/png')
  expect((await thumbnailResponse.body()).subarray(0, 8)).toEqual(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))
  const imageEntry = page.getByRole('button', { name: 'Datei thumbnail-fixture.png' })
  await expect(imageEntry.locator('img.entry-thumbnail')).toBeVisible()
  await expect.poll(() => imageEntry.locator('img.entry-thumbnail').evaluate((image: HTMLImageElement) => image.complete && image.naturalWidth > 0)).toBe(true)

  for (const mediaName of ['iphone-preview.mp4', 'iphone-preview.pdf']) {
    const mediaPath = path.join(fixtureRoot, mediaName)
    const mediaThumbnail = await page.context().request.get(`${baseURL}/api/v1/thumbnail?${new URLSearchParams({ path: mediaPath, size: '160' })}`)
    expect(mediaThumbnail.ok(), `Windows-Shell-Miniatur für ${mediaName}: ${await mediaThumbnail.text()}`).toBeTruthy()
    expect(mediaThumbnail.headers()['content-type']).toContain('image/png')
    expect((await mediaThumbnail.body()).subarray(0, 8)).toEqual(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))
    const mediaEntry = page.getByRole('button', { name: `Datei ${mediaName}` })
    await expect(mediaEntry.locator('img.entry-thumbnail')).toBeVisible()
    await expect.poll(() => mediaEntry.locator('img.entry-thumbnail').evaluate((image: HTMLImageElement) => image.complete && image.naturalWidth > 0)).toBe(true)
  }

  if (heicFixtureAvailable) {
    const heicPath = path.join(fixtureRoot, 'heic-thumbnail-fixture.heic')
    const heicResponse = await page.context().request.get(`${baseURL}/api/v1/thumbnail?${new URLSearchParams({ path: heicPath, size: '160' })}`)
    expect(heicResponse.ok(), await heicResponse.text()).toBeTruthy()
    expect(heicResponse.headers()['content-type']).toContain('image/png')
    expect((await heicResponse.body()).subarray(0, 8)).toEqual(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))
    const heicEntry = page.getByRole('button', { name: 'Datei heic-thumbnail-fixture.heic' })
    await expect(heicEntry.locator('img.entry-thumbnail')).toBeVisible()
    await expect.poll(() => heicEntry.locator('img.entry-thumbnail').evaluate((image: HTMLImageElement) => image.complete && image.naturalWidth > 0)).toBe(true)
  }

  const videoPath = path.join(fixtureRoot, 'iphone-preview.mp4')
  await page.getByRole('button', { name: 'Datei iphone-preview.mp4' }).click()
  const videoPreview = page.getByRole('dialog', { name: 'Player für iphone-preview.mp4' })
  const video = videoPreview.locator('video')
  await expect(video).toBeVisible()
  await expect.poll(() => video.evaluate((element: HTMLVideoElement) => Number.isFinite(element.duration) && element.videoWidth > 0), { timeout: 20_000 }).toBe(true)
  const videoMetadata = await video.evaluate((element: HTMLVideoElement) => ({ duration: element.duration, width: element.videoWidth, height: element.videoHeight }))
  expect(videoMetadata.duration).toBeGreaterThanOrEqual(2.9)
  expect(videoMetadata.width).toBeGreaterThan(0)
  expect(videoMetadata.height).toBeGreaterThan(0)
  await video.evaluate((element: HTMLVideoElement) => { element.currentTime = element.duration / 2 })
  await expect.poll(() => video.evaluate((element: HTMLVideoElement) => element.currentTime)).toBeGreaterThan(1)
  const videoRange = await page.context().request.get(`${baseURL}/api/v1/stream?${new URLSearchParams({ path: videoPath })}`, { headers: { Range: 'bytes=0-15' } })
  expect(videoRange.status()).toBe(206)
  expect(videoRange.headers()['content-range']).toMatch(/^bytes 0-15\//)
  expect(videoRange.headers()['content-type']).toContain('video/mp4')
  expect((await videoRange.body()).subarray(4, 8).toString('ascii')).toBe('ftyp')
  await videoPreview.getByRole('button', { name: 'Wiedergabe beenden' }).click()

  const audioPath = path.join(fixtureRoot, 'iphone-preview.mp3')
  await page.getByRole('button', { name: 'Datei iphone-preview.mp3' }).click()
  const audioPreview = page.getByRole('dialog', { name: 'Player für iphone-preview.mp3' })
  const audio = audioPreview.locator('audio')
  await expect(audio).toBeAttached()
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => Number.isFinite(element.duration) && element.duration >= 2.9), { timeout: 20_000 }).toBe(true)
  const audioSeek = audioPreview.getByRole('slider', { name: 'Wiedergabeposition', exact: true })
  const audioBounds = await audioSeek.boundingBox()
  expect(audioBounds).not.toBeNull()
  await page.touchscreen.tap(audioBounds!.x + audioBounds!.width * 0.6, audioBounds!.y + audioBounds!.height / 2)
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => !element.seeking && element.currentTime > 1 && element.readyState >= 3)).toBe(true)
  await page.screenshot({ path: path.join(screenshotDir, '03-audio-vorschau.png'), animations: 'disabled' })
  const audioRange = await page.context().request.get(`${baseURL}/api/v1/stream?${new URLSearchParams({ path: audioPath })}`, { headers: { Range: 'bytes=0-15' } })
  expect(audioRange.status()).toBe(206)
  expect(audioRange.headers()['content-range']).toMatch(/^bytes 0-15\//)
  expect(audioRange.headers()['content-type']).toContain('audio/mpeg')
  await audioPreview.getByRole('button', { name: 'Wiedergabe beenden' }).click()

  const pdfFile = page.getByRole('button', { name: 'Datei iphone-preview.pdf' })
  await pdfFile.click()
  const pdfPreview = page.getByRole('dialog', { name: 'Vorschau für iphone-preview.pdf' })
  await expect(pdfPreview).toBeVisible()
  await expect(pdfPreview.locator('iframe')).toHaveAttribute('src', /\/api\/v1\/stream\?path=/)
  const pdfRange = await page.context().request.get(`${baseURL}/api/v1/stream?${new URLSearchParams({ path: path.join(fixtureRoot, 'iphone-preview.pdf') })}`, {
    headers: { Range: 'bytes=0-7' },
  })
  expect(pdfRange.status()).toBe(206)
  expect(pdfRange.headers()['content-range']).toMatch(/^bytes 0-7\//)
  expect(pdfRange.headers()['content-type']).toContain('application/pdf')
  await page.getByRole('button', { name: 'Schließen' }).click()

  await page.getByRole('button', { name: 'Ansicht', exact: true }).click()
  await page.getByRole('button', { name: 'Liste', exact: true }).click()
  await page.getByRole('button', { name: 'Ordner 5GB video' }).click()
  await expect(page.getByRole('heading', { name: '5GB video', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Datei 5gb-playback-probe.mp4' }).click()
  const longVideoPreview = page.getByRole('dialog', { name: 'Player für 5gb-playback-probe.mp4' })
  const longVideo = longVideoPreview.locator('video')
  await expect.poll(() => longVideo.evaluate((element: HTMLVideoElement) => Number.isFinite(element.duration) && element.videoWidth > 0 && element.readyState >= 2), { timeout: 20_000 }).toBe(true)
  const longVideoMetadata = await longVideo.evaluate((element: HTMLVideoElement) => ({ duration: element.duration, width: element.videoWidth, height: element.videoHeight }))
  expect(longVideoMetadata.duration).toBeGreaterThan(7_000)
  expect(longVideoMetadata.width).toBeGreaterThan(0)
  expect(longVideoMetadata.height).toBeGreaterThan(0)
  await longVideo.evaluate((element: HTMLVideoElement) => {
    element.dataset.seekEvents = ''
    for (const eventName of ['seeking', 'seeked', 'timeupdate', 'error', 'stalled', 'suspend']) {
      element.addEventListener(eventName, () => { element.dataset.seekEvents += `${eventName}:${element.currentTime},` })
    }
  })
  for (const seekSeconds of [10, 30, 75, 5_400]) {
    await longVideo.evaluate((element: HTMLVideoElement, seconds: number) => { element.currentTime = seconds }, seekSeconds)
    try {
      await expect.poll(() => longVideo.evaluate((element: HTMLVideoElement, seconds: number) => !element.seeking && element.currentTime >= seconds && element.readyState >= 2, seekSeconds), { timeout: 5_000 }).toBe(true)
    } catch (error) {
      const mediaState = await longVideo.evaluate((element: HTMLVideoElement) => ({
        currentTime: element.currentTime,
        duration: element.duration,
        seeking: element.seeking,
        readyState: element.readyState,
        networkState: element.networkState,
        error: element.error && { code: element.error.code, message: element.error.message },
        buffered: Array.from({ length: element.buffered.length }, (_, index) => [element.buffered.start(index), element.buffered.end(index)]),
        seekable: Array.from({ length: element.seekable.length }, (_, index) => [element.seekable.start(index), element.seekable.end(index)]),
        seekEvents: element.dataset.seekEvents,
      }))
      throw new Error(`5GB video seek to ${seekSeconds}s failed: ${JSON.stringify(mediaState)}; ${String(error)}`)
    }
  }
  const longVideoRange = await page.context().request.get(`${baseURL}/api/v1/stream?${new URLSearchParams({ path: path.join(fixtureRoot, '5GB video', '5gb-playback-probe.mp4') })}`, {
    headers: { Range: 'bytes=3750000000-3750000063' },
  })
  expect(longVideoRange.status()).toBe(206)
  expect(longVideoRange.headers()['content-range']).toBe('bytes 3750000000-3750000063/5000000000')
  expect((await longVideoRange.body()).length).toBe(64)
})
