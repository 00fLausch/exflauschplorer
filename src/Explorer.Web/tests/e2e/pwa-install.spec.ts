import { expect, test } from '@playwright/test'

test('iPhone PWA exposes install metadata, Apple icon, safe areas and an active service worker', async ({ page, request, baseURL }) => {
  await page.goto('/')
  const pinGate = page.getByRole('heading', { name: 'Dateiablage gesperrt' })
  const home = page.getByRole('heading', { name: 'System' })
  await expect(pinGate.or(home).first()).toBeVisible()
  if (await pinGate.isVisible().catch(() => false)) {
    await page.getByRole('textbox', { name: 'PIN eingeben' }).fill('849201')
    await page.getByRole('button', { name: 'Dateiablage entsperren' }).click()
  }
  await expect(home).toBeVisible()

  const manifestResponse = await request.get(new URL('/manifest.webmanifest', baseURL).toString())
  expect(manifestResponse.ok()).toBeTruthy()
  const manifest = await manifestResponse.json() as {
    name: string
    short_name: string
    start_url: string
    scope: string
    display: string
    orientation: string
    icons: Array<{ src: string; sizes: string; type: string; purpose?: string }>
  }
  expect(manifest.name).toBe('Windows Datei-Explorer')
  expect(manifest.short_name).toBe('Dateiablage')
  expect(manifest.start_url).toBe('/')
  expect(manifest.scope).toBe('/')
  expect(manifest.display).toBe('standalone')
  expect(manifest.orientation).toBe('any')
  expect(manifest.icons).toEqual(expect.arrayContaining([
    expect.objectContaining({ src: '/pwa-192.png', sizes: '192x192', type: 'image/png' }),
    expect.objectContaining({ src: '/pwa-512.png', sizes: '512x512', type: 'image/png' }),
    expect.objectContaining({ src: '/pwa-512.png', purpose: 'any maskable' }),
  ]))

  for (const iconPath of ['/apple-touch-icon.png', '/pwa-192.png', '/pwa-512.png']) {
    const icon = await request.get(new URL(iconPath, baseURL).toString())
    expect(icon.ok(), `${iconPath} must be available for Home Screen installation`).toBeTruthy()
    expect(icon.headers()['content-type']).toContain('image/png')
  }

  const browserMetadata = await page.evaluate(async () => {
    const viewport = document.querySelector<HTMLMetaElement>('meta[name="viewport"]')?.content ?? ''
    const serviceWorker = 'serviceWorker' in navigator
      ? await navigator.serviceWorker.ready.then((registration) => Boolean(registration.active))
      : false
    return {
      lang: document.documentElement.lang,
      viewport,
      capable: document.querySelector<HTMLMetaElement>('meta[name="apple-mobile-web-app-capable"]')?.content,
      statusBar: document.querySelector<HTMLMetaElement>('meta[name="apple-mobile-web-app-status-bar-style"]')?.content,
      title: document.querySelector<HTMLMetaElement>('meta[name="apple-mobile-web-app-title"]')?.content,
      appleTouchIcon: document.querySelector<HTMLLinkElement>('link[rel="apple-touch-icon"]')?.href,
      safeTop: getComputedStyle(document.documentElement).getPropertyValue('--safe-top').trim(),
      safeBottom: getComputedStyle(document.documentElement).getPropertyValue('--safe-bottom').trim(),
      serviceWorker,
    }
  })
  expect(browserMetadata.lang).toBe('de')
  expect(browserMetadata.viewport).toContain('viewport-fit=cover')
  expect(browserMetadata.capable).toBe('yes')
  expect(browserMetadata.statusBar).toBe('black-translucent')
  expect(browserMetadata.title).toBe('Dateiablage')
  expect(browserMetadata.appleTouchIcon).toContain('/apple-touch-icon.png')
  expect(browserMetadata.safeTop).not.toBe('')
  expect(browserMetadata.safeBottom).not.toBe('')
  expect(browserMetadata.serviceWorker).toBe(true)
})
