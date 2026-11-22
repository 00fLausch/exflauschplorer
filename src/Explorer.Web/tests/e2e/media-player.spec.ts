import { expect, test as base, type Locator, type Page } from '@playwright/test'
import { copyFile, mkdir, mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const statePath = path.join(path.dirname(fileURLToPath(import.meta.url)), '.runtime.json')
const verificationDir = path.resolve(path.dirname(statePath), '../../../../docs/verification')

interface MediaFixture {
  root: string
  sourceRoot: string
}

const test = base.extend<{ mediaFixture: MediaFixture }>({
  mediaFixture: async ({ page }, runFixture) => {
    const state = JSON.parse(await readFile(statePath, 'utf8')) as { fixtureRoot: string }
    const sourceRoot = await realpath(state.fixtureRoot)
    const root = await mkdtemp(path.join(sourceRoot, 'media-player-'))
    try {
      await runFixture({ root, sourceRoot })
    } finally {
      await page.close()
      const actualRoot = await realpath(root)
      expect(path.dirname(actualRoot).toLowerCase(), 'Media fixtures must stay inside the isolated test root.').toBe(sourceRoot.toLowerCase())
      expect(path.basename(actualRoot), 'Only media-player fixtures may be removed.').toMatch(/^media-player-/)
      await rm(actualRoot, { recursive: true, force: true, maxRetries: 8, retryDelay: 250 })
    }
  },
})

test.use({ serviceWorkers: 'block' })

async function openFolder(page: Page, folder: string) {
  await page.goto('/')
  const locked = page.getByRole('heading', { name: 'Dateiablage gesperrt', exact: true })
  const home = page.getByRole('heading', { name: 'System', exact: true })
  await expect(locked.or(home)).toBeVisible()
  if (await locked.isVisible()) {
    await page.getByRole('textbox', { name: 'PIN eingeben' }).fill('849201')
    await page.getByRole('button', { name: 'Dateiablage entsperren' }).click()
  }
  await expect(home).toBeVisible()
  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /Pfad öffnen/ }).click()
  await page.getByRole('textbox', { name: 'Ordnerpfad', exact: true }).fill(folder)
  await page.getByRole('button', { name: 'Ordner öffnen', exact: true }).click()
  await expect(page.getByRole('heading', { name: path.basename(folder), exact: true })).toBeVisible()
}

async function pauseIfPlaying(player: Locator) {
  const pause = player.getByRole('button', { name: 'Pause', exact: true })
  if (await pause.isVisible()) await pause.click()
}

async function expectNoHorizontalOverflow(page: Page) {
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
}

test('music keeps its playback element and position while browsing and extending the queue', async ({ page, mediaFixture }) => {
  const firstName = '01 - Erster Titel.mp3'
  const secondName = '02 - Zweiter Titel.mp3'
  const otherFolder = path.join(mediaFixture.root, 'Weiterstöbern')
  await mkdir(otherFolder)
  await copyFile(path.join(mediaFixture.sourceRoot, 'iphone-preview.mp3'), path.join(mediaFixture.root, firstName))
  await copyFile(path.join(mediaFixture.sourceRoot, 'iphone-preview.mp3'), path.join(otherFolder, secondName))
  await openFolder(page, mediaFixture.root)
  await page.getByRole('button', { name: `Datei ${firstName}`, exact: true }).click()

  const player = page.getByRole('dialog', { name: `Player für ${firstName}`, exact: true })
  const audio = page.locator('audio')
  await expect(player).toBeVisible()
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => Number.isFinite(element.duration) && element.duration >= 2.9 && element.readyState >= 2)).toBe(true)
  await pauseIfPlaying(player)
  const waveform = player.getByRole('slider', { name: 'Wellenform des gesamten Titels', exact: true })
  await waveform.focus()
  await waveform.press('Home')
  await player.getByRole('button', { name: 'Abspielen', exact: true }).click()
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => !element.paused && element.currentTime > 0.1)).toBe(true)
  await player.getByRole('button', { name: 'Pause', exact: true }).click()

  const originalElement = await audio.elementHandle()
  const pausedTime = await audio.evaluate((element: HTMLAudioElement) => element.currentTime)
  const originalSource = await audio.evaluate((element: HTMLAudioElement) => element.currentSrc)
  await player.getByRole('button', { name: 'Player minimieren', exact: true }).click()
  const mini = page.getByRole('region', { name: 'Medienplayer', exact: true })
  await expect(mini).toBeVisible()
  await expect(player).toBeHidden()
  await page.getByRole('button', { name: 'Ordner Weiterstöbern', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Weiterstöbern', exact: true })).toBeVisible()
  await expect.poll(() => audio.evaluate((element, original) => element === original, originalElement)).toBe(true)
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement, position: number) => element.paused && Math.abs(element.currentTime - position) < 0.15, pausedTime)).toBe(true)

  await page.getByRole('button', { name: `Weitere Aktionen für ${secondName}`, exact: true }).click()
  await page.getByRole('button', { name: 'Zur Warteschlange hinzufügen', exact: true }).click()
  await expect.poll(() => audio.evaluate((element, original) => element === original, originalElement)).toBe(true)
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => element.currentSrc)).toBe(originalSource)
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement, position: number) => element.paused && Math.abs(element.currentTime - position) < 0.15, pausedTime)).toBe(true)

  await mini.getByRole('button', { name: 'Abspielen', exact: true }).click()
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement, position: number) => !element.paused && element.currentTime > position + 0.1, pausedTime)).toBe(true)
  await mini.getByRole('button', { name: 'Pause', exact: true }).click()
  await mini.getByRole('button', { name: 'Player öffnen', exact: true }).click()
  await expect(player).toBeVisible()
  await expect.poll(() => audio.evaluate((element, original) => element === original, originalElement)).toBe(true)
  await player.getByRole('button', { name: 'Nächster Titel', exact: true }).click()
  const nextPlayer = page.getByRole('dialog', { name: `Player für ${secondName}`, exact: true })
  await expect(nextPlayer).toBeVisible()
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => {
    const source = element.currentSrc || element.src
    return source ? new URL(source).searchParams.get('path') : null
  })).toContain(secondName)
  await nextPlayer.getByRole('button', { name: 'Vorheriger Titel', exact: true }).click()
  await expect(player).toBeVisible()
  await player.getByRole('button', { name: 'Wiedergabe beenden', exact: true }).click()
  await expect(audio).toHaveCount(0)
  await expect(mini).toHaveCount(0)
})

test('the floating player leaves folder additions and selection controls touchable', async ({ page, mediaFixture }) => {
  const firstName = 'Aktueller Titel.mp3'
  const folder = path.join(mediaFixture.root, 'Weitere Titel')
  await mkdir(folder)
  await copyFile(path.join(mediaFixture.sourceRoot, 'iphone-preview.mp3'), path.join(mediaFixture.root, firstName))
  for (const name of ['Titel A.mp3', 'Titel B.mp3'])
    await copyFile(path.join(mediaFixture.sourceRoot, 'iphone-preview.mp3'), path.join(folder, name))
  await writeFile(path.join(folder, 'Notiz.txt'), 'Kein Titel')
  await page.setViewportSize({ width: 320, height: 740 })
  await openFolder(page, mediaFixture.root)
  await page.getByRole('button', { name: `Datei ${firstName}`, exact: true }).click()
  const player = page.getByRole('dialog', { name: `Player für ${firstName}`, exact: true })
  const audio = page.locator('audio')
  await expect(player).toBeVisible()
  // This regression checks layout and queue additions independently of installed decoders.
  // Real media decoding is covered by the playback tests above and below.
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => element.currentSrc)).not.toBe('')
  await audio.evaluate((element: HTMLAudioElement) => element.pause())
  const originalElement = await audio.elementHandle()
  const originalSource = await audio.evaluate((element: HTMLAudioElement) => element.currentSrc)
  const originalPosition = await audio.evaluate((element: HTMLAudioElement) => element.currentTime)
  await player.getByRole('button', { name: 'Player minimieren', exact: true }).click()
  await page.getByRole('button', { name: 'Ordner Weitere Titel', exact: true }).click()
  const addAll = page.getByRole('button', { name: 'Ordner zur Warteschlange hinzufügen', exact: true })
  const mini = page.getByRole('region', { name: 'Medienplayer', exact: true })
  const expectTouchable = async (control: Locator) => {
    await expect.poll(() => control.evaluate((button) => {
      const playerBounds = document.querySelector('.player-mini')!.getBoundingClientRect()
      const bounds = button.getBoundingClientRect()
      const hit = document.elementFromPoint(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2)
      return playerBounds.bottom + 8 <= bounds.top && !!hit && button.contains(hit)
    })).toBe(true)
  }
  await expectTouchable(addAll)
  await page.setViewportSize({ width: 390, height: 844 })
  await page.evaluate(() => {
    document.documentElement.style.setProperty('--safe-bottom', '34px')
    document.documentElement.style.setProperty('--app-font-size', '24px')
  })
  await expectTouchable(addAll)
  await page.getByRole('button', { name: 'Auswählen', exact: true }).click()
  await expectTouchable(addAll)
  await expectTouchable(page.getByRole('button', { name: 'Alle auswählen', exact: true }))
  await addAll.click()
  await expect(mini.getByText('Titel 1 von 3', { exact: true })).toBeVisible()
  await expect.poll(() => audio.evaluate((element, original) => element === original, originalElement)).toBe(true)
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => element.currentSrc)).toBe(originalSource)
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement, position: number) => element.paused && Math.abs(element.currentTime - position) < .15, originalPosition)).toBe(true)
  await expectTouchable(addAll)
  await expectNoHorizontalOverflow(page)
  await mini.getByRole('button', { name: 'Player öffnen', exact: true }).click()
  await player.getByRole('button', { name: 'Wiedergabe beenden', exact: true }).click()
})

test('a long recording has a complete waveform and supports late touch seeking at 320px', async ({ page, mediaFixture }) => {
  test.setTimeout(90_000)
  const fileName = '32 Minuten DJ Set.wav'
  const filePath = path.join(mediaFixture.root, fileName)
  await writeFile(filePath, createLongWaveFixture())
  await page.setViewportSize({ width: 320, height: 700 })
  await openFolder(page, mediaFixture.root)
  await page.getByRole('button', { name: `Datei ${fileName}`, exact: true }).click()
  const player = page.getByRole('dialog', { name: `Player für ${fileName}`, exact: true })
  const audio = page.locator('audio')
  await expect(player).toBeVisible()
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => Number.isFinite(element.duration) && element.duration > 1_900 && element.readyState >= 2), { timeout: 20_000 }).toBe(true)
  await pauseIfPlaying(player)

  let waveformData: { duration: number; peaks: number[] } | undefined
  await expect.poll(async () => {
    const response = await page.context().request.get(`/api/v1/waveform?${new URLSearchParams({ path: filePath })}`)
    if (response.status() === 200) waveformData = await response.json() as { duration: number; peaks: number[] }
    return response.status()
  }, { timeout: 45_000, intervals: [2_000] }).toBe(200)
  expect(waveformData!.duration).toBeGreaterThan(1_900)
  expect(waveformData!.peaks).toHaveLength(1_200)
  expect(waveformData!.peaks.every((peak) => Number.isFinite(peak) && peak >= 0 && peak <= 1)).toBe(true)
  const average = (values: number[]) => values.reduce((sum, value) => sum + value, 0) / values.length
  expect(average(waveformData!.peaks.slice(-100))).toBeGreaterThan(average(waveformData!.peaks.slice(0, 100)) * 4)

  const waveform = player.getByRole('slider', { name: 'Wellenform des gesamten Titels', exact: true })
  await expect(waveform.locator('svg').first()).toBeVisible()
  const bounds = await waveform.boundingBox()
  expect(bounds).not.toBeNull()
  await page.touchscreen.tap(bounds!.x + bounds!.width * 0.85, bounds!.y + bounds!.height / 2)
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => !element.seeking && element.currentTime > element.duration * 0.8 && element.currentTime < element.duration * 0.9)).toBe(true)
  await expectNoHorizontalOverflow(page)
  await mkdir(verificationDir, { recursive: true })
  await page.screenshot({ path: path.join(verificationDir, 'player-320-waveform.png'), animations: 'disabled' })
  await player.getByRole('button', { name: 'Player minimieren', exact: true }).click()
  const mini = page.getByRole('region', { name: 'Medienplayer', exact: true })
  await expect(mini).toBeVisible()
  await expectNoHorizontalOverflow(page)
  const miniBounds = await mini.boundingBox()
  const navBounds = await page.getByRole('navigation', { name: 'Hauptnavigation', exact: true }).boundingBox()
  expect(miniBounds).not.toBeNull()
  expect(navBounds).not.toBeNull()
  expect(miniBounds!.y + miniBounds!.height).toBeLessThanOrEqual(navBounds!.y + 1)
  await page.screenshot({ path: path.join(verificationDir, 'player-320-mini.png'), animations: 'disabled' })
  await mini.getByRole('button', { name: 'Player öffnen', exact: true }).click()
  await player.getByRole('button', { name: 'Wiedergabe beenden', exact: true }).click()
})

test('video decodes inline with range streaming and reports invalid media clearly', async ({ page, mediaFixture }) => {
  const fileName = 'Film.mp4'
  const brokenName = 'Defekter Film.mp4'
  const videoPath = path.join(mediaFixture.root, fileName)
  await copyFile(path.join(mediaFixture.sourceRoot, 'iphone-preview.mp4'), videoPath)
  await writeFile(path.join(mediaFixture.root, brokenName), 'Diese Datei enthält absichtlich keine Videodaten.')
  await openFolder(page, mediaFixture.root)
  await page.getByRole('button', { name: `Datei ${fileName}`, exact: true }).click()
  const player = page.getByRole('dialog', { name: `Player für ${fileName}`, exact: true })
  const video = player.locator('video')
  await expect(video).toBeVisible()
  await expect(video).toHaveAttribute('playsinline', '')
  await expect.poll(() => video.evaluate((element: HTMLVideoElement) => Number.isFinite(element.duration) && element.duration >= 2.9 && element.videoWidth > 0 && element.videoHeight > 0 && element.readyState >= 2), { timeout: 20_000 }).toBe(true)
  const range = await page.context().request.get(`/api/v1/stream?${new URLSearchParams({ path: videoPath })}`, { headers: { Range: 'bytes=0-15' } })
  expect(range.status()).toBe(206)
  expect(range.headers()['content-range']).toMatch(/^bytes 0-15\//)
  expect(range.headers()['content-type']).toContain('video/mp4')
  expect((await range.body()).subarray(4, 8).toString('ascii')).toBe('ftyp')
  await player.getByRole('button', { name: 'Wiedergabe beenden', exact: true }).click()

  await page.getByRole('button', { name: `Datei ${brokenName}`, exact: true }).click()
  const brokenPlayer = page.getByRole('dialog', { name: `Player für ${brokenName}`, exact: true })
  await expect(brokenPlayer.getByRole('alert')).toContainText(/nicht|Format|abspiel/i)
  await expect(brokenPlayer.getByRole('button', { name: 'Erneut versuchen', exact: true })).toBeVisible()
  await brokenPlayer.getByRole('button', { name: 'Wiedergabe beenden', exact: true }).click()
  await expect(page.getByRole('heading', { name: path.basename(mediaFixture.root), exact: true })).toBeVisible()
})

function createLongWaveFixture() {
  const sampleRate = 8_000
  const durationSeconds = 32 * 60
  const sampleCount = sampleRate * durationSeconds
  const bytes = Buffer.alloc(44 + sampleCount)
  bytes.write('RIFF', 0)
  bytes.writeUInt32LE(36 + sampleCount, 4)
  bytes.write('WAVEfmt ', 8)
  bytes.writeUInt32LE(16, 16)
  bytes.writeUInt16LE(1, 20)
  bytes.writeUInt16LE(1, 22)
  bytes.writeUInt32LE(sampleRate, 24)
  bytes.writeUInt32LE(sampleRate, 28)
  bytes.writeUInt16LE(1, 32)
  bytes.writeUInt16LE(8, 34)
  bytes.write('data', 36)
  bytes.writeUInt32LE(sampleCount, 40)
  for (let index = 0; index < sampleCount; index++) {
    const amplitude = index < sampleCount / 2 ? 8 : 96
    bytes[44 + index] = 128 + Math.round(Math.sin(2 * Math.PI * 100 * index / sampleRate) * amplitude)
  }
  return bytes
}
