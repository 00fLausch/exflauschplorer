import { expect, test } from '@playwright/test'
import { readFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

test.use({ serviceWorkers: 'block' })
const testDir = path.dirname(fileURLToPath(import.meta.url))

test.beforeEach(async ({ page }) => {
  const { fixtureRoot } = JSON.parse(await readFile(path.join(testDir, '.runtime.json'), 'utf8')) as { fixtureRoot: string }
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'System' })).toBeVisible()
  await page.getByRole('button', { name: 'Mehr', exact: true }).click()
  await page.getByRole('button', { name: /Pfad öffnen/ }).click()
  await page.getByRole('textbox', { name: 'Ordnerpfad' }).fill(fixtureRoot)
  await page.getByRole('button', { name: 'Ordner öffnen', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Datei iphone-preview.pdf' })).toBeVisible()
})

test('rendert mehrere PDF-Seiten und bietet Seitennavigation und Zoom', async ({ page, baseURL }) => {
  const { version } = JSON.parse(await readFile(path.resolve(testDir, '../../node_modules/pdfjs-dist/package.json'), 'utf8')) as { version: string }
  for (const resource of ['cmaps/78-EUC-H.bcmap', 'standard_fonts/FoxitDingbats.pfb', 'iccs/CGATS001Compat-v2-micro.icc', 'wasm/jbig2.wasm']) {
    const response = await page.request.get(`${baseURL}/pdfjs/${version}/${resource}`)
    expect(response.status(), resource).toBe(200)
    expect(response.headers()['content-type']).toContain(resource.endsWith('.wasm') ? 'application/wasm' : 'application/octet-stream')
    expect((await response.body()).length).toBeGreaterThan(0)
  }
  const requests: string[] = []
  page.on('request', (request) => { if (request.url().includes('/api/v1/stream?')) requests.push(request.url()) })
  await page.getByRole('button', { name: 'Datei iphone-preview.pdf' }).click()
  const dialog = page.getByRole('dialog', { name: 'Vorschau für iphone-preview.pdf' })
  const canvas = dialog.locator('canvas.pdf-page')
  await expect(canvas).toHaveAttribute('data-rendered-page', '1', { timeout: 30_000 })
  await expect(dialog.getByText('von 3')).toBeVisible()
  const darkPixels = await canvas.evaluate((element: HTMLCanvasElement) => {
    const context = element.getContext('2d')!
    const image = context.getImageData(0, 0, element.width, element.height).data
    let count = 0
    for (let i = 0; i < image.length; i += 160) if (image[i] < 100 && image[i + 1] < 100 && image[i + 2] < 100) count++
    return count
  })
  expect(darkPixels).toBeGreaterThan(100)
  const textPixels = await canvas.evaluate((element: HTMLCanvasElement) => {
    const context = element.getContext('2d')!
    const top = Math.floor(element.height * 0.49)
    const image = context.getImageData(0, top, element.width, Math.floor(element.height * 0.15)).data
    let count = 0
    for (let i = 0; i < image.length; i += 4) if (image[i] < 100 && image[i + 1] < 100 && image[i + 2] < 100) count++
    return count
  })
  expect(textPixels).toBeGreaterThan(20)
  await dialog.getByRole('button', { name: 'Nächste PDF-Seite' }).click()
  await expect(canvas).toHaveAttribute('data-rendered-page', '2')
  const pageInput = dialog.getByRole('spinbutton', { name: 'PDF-Seite' })
  await pageInput.fill('3')
  await pageInput.press('Enter')
  await expect(canvas).toHaveAttribute('data-rendered-page', '3')
  await expect(dialog.getByRole('button', { name: 'Nächste PDF-Seite' })).toBeDisabled()
  const widthBefore = await canvas.evaluate((element: HTMLCanvasElement) => element.clientWidth)
  await dialog.getByRole('button', { name: 'PDF vergrößern' }).click()
  await expect.poll(() => canvas.evaluate((element: HTMLCanvasElement) => element.clientWidth)).toBeGreaterThan(widthBefore)
  await page.setViewportSize({ width: 320, height: 700 })
  const toolbarFits = await dialog.locator('.pdf-toolbar').evaluate((element) => element.scrollWidth <= element.clientWidth)
  expect(toolbarFits).toBe(true)
  await expect(dialog.getByRole('button', { name: 'PDF vergrößern' })).toBeVisible()
  await expect(canvas).toHaveAttribute('data-rendered-page', '3')
  await expect(dialog.getByText('Seite wird dargestellt …')).toHaveCount(0)
  await page.screenshot({ path: path.resolve(testDir, '../../../../docs/verification/pdf-iphone.png'), animations: 'disabled' })
  expect(requests.length).toBeGreaterThan(0)
  await dialog.getByRole('button', { name: 'Schließen' }).click()
  await expect(dialog).toHaveCount(0)
})

test('lädt große PDF-Dateien über Teilanfragen', async ({ page }) => {
  const ranges: string[] = []
  page.on('request', (request) => {
    if (request.url().includes('/api/v1/stream?') && request.headers().range) ranges.push(request.headers().range)
  })
  await page.getByRole('button', { name: 'Datei large-preview.pdf' }).click()
  const dialog = page.getByRole('dialog', { name: 'Vorschau für large-preview.pdf' })
  await expect(dialog.locator('canvas.pdf-page')).toHaveAttribute('data-rendered-page', '1', { timeout: 30_000 })
  expect(ranges.some((value) => /^bytes=\d+-\d+$/.test(value))).toBe(true)
})

test('zeigt beschädigte PDFs mit verständlicher Fehlermeldung und Download an', async ({ page }) => {
  await page.getByRole('button', { name: 'Datei corrupt-preview.pdf' }).click()
  const dialog = page.getByRole('dialog', { name: 'Vorschau für corrupt-preview.pdf' })
  await expect(dialog.getByRole('alert')).toContainText('PDF konnte nicht geöffnet werden', { timeout: 30_000 })
  await expect(dialog.getByRole('button', { name: 'Laden', exact: true })).toBeVisible()
  await expect(dialog.getByRole('link', { name: 'Im Browser öffnen' })).toHaveAttribute('href', /\/api\/v1\/stream\?path=/)
})

test('öffnet passwortgeschützte PDFs nach korrekter Eingabe', async ({ page }) => {
  await page.getByRole('button', { name: 'Datei password-protected.pdf' }).click()
  const dialog = page.getByRole('dialog', { name: 'Vorschau für password-protected.pdf' })
  const password = dialog.getByLabel('PDF-Passwort')
  await expect(password).toBeVisible({ timeout: 30_000 })
  await password.fill('falsch')
  await dialog.getByRole('button', { name: 'PDF öffnen' }).click()
  await expect(dialog.getByText('Das Passwort war nicht richtig. Bitte erneut versuchen.')).toBeVisible()
  await password.fill('demo123')
  await dialog.getByRole('button', { name: 'PDF öffnen' }).click()
  await expect(dialog.locator('canvas.pdf-page')).toHaveAttribute('data-rendered-page', '1', { timeout: 30_000 })
})
