import { expect, test } from '@playwright/test'
import { readFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

test('PDF-Vorschau funktioniert mit PWA und PIN ohne Dateicache', async ({ page }) => {
  const testDir = path.dirname(fileURLToPath(import.meta.url))
  const { fixtureRoot } = JSON.parse(await readFile(path.join(testDir, '.runtime.json'), 'utf8')) as { fixtureRoot: string }
  await page.goto('/')
  await page.evaluate(async () => { await navigator.serviceWorker.ready })
  await page.reload()
  await expect(page.getByRole('heading', { name: 'System' })).toBeVisible()
  const pinEnabled = await page.evaluate(async () => (await fetch('/api/v1/security/pin/configure', {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Explorer-Request': '1' }, body: JSON.stringify({ pin: '849201' }),
  })).ok)
  expect(pinEnabled).toBe(true)
  try {
    await page.getByRole('button', { name: 'Mehr', exact: true }).click()
    await page.getByRole('button', { name: /Pfad öffnen/ }).click()
    await page.getByRole('textbox', { name: 'Ordnerpfad' }).fill(fixtureRoot)
    await page.getByRole('button', { name: 'Ordner öffnen', exact: true }).click()
    await page.getByRole('button', { name: 'Datei iphone-preview.pdf' }).click()
    const dialog = page.getByRole('dialog', { name: 'Vorschau für iphone-preview.pdf' })
    await expect(dialog.locator('canvas.pdf-page')).toHaveAttribute('data-rendered-page', '1', { timeout: 30_000 })
    const cachedDocumentUrls = await page.evaluate(async () => {
      const names = await caches.keys()
      const requests = await Promise.all(names.map(async (name) => (await caches.open(name)).keys()))
      return requests.flat().map((request) => request.url).filter((url) => url.includes('/api/v1/stream'))
    })
    expect(cachedDocumentUrls).toEqual([])
  } finally {
    await page.evaluate(async () => { await fetch('/api/v1/security/pin/disable', { method: 'POST', headers: { 'X-Explorer-Request': '1' } }) })
  }
})
