import { defineConfig, devices } from '@playwright/test'

export default defineConfig({
  testDir: './tests/e2e',
  globalTeardown: './scripts/e2e-teardown.mjs',
  fullyParallel: false,
  workers: 1,
  timeout: 45_000,
  expect: { timeout: 8_000 },
  reporter: 'list',
  use: {
    baseURL: 'http://127.0.0.1:5197',
    ...devices['iPhone 13'],
    serviceWorkers: 'allow',
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
  projects: [
    {
      name: 'webkit-iphone',
      use: { ...devices['iPhone 13'] },
      testIgnore: [/media-player\.spec\.ts/, /a-media-preview\.spec\.ts/],
    },
    {
      name: 'chrome-iphone',
      use: { ...devices['iPhone 13'], browserName: 'chromium', channel: 'chrome' },
      testMatch: [/media-player\.spec\.ts/, /a-media-preview\.spec\.ts/],
    },
  ],
  webServer: {
    command: 'node scripts/e2e-server.mjs',
    url: 'http://127.0.0.1:5197/api/v1/health',
    reuseExistingServer: false,
    timeout: 180_000,
    stdout: 'pipe',
    stderr: 'pipe',
  },
})
