import { execFileSync, spawn } from 'node:child_process'
import { once } from 'node:events'
import { access, copyFile, mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import sharp from 'sharp'
import { createSparseH264Mp4 } from '../tests/e2e/create-sparse-mp4.mjs'

const scriptDir = path.dirname(fileURLToPath(import.meta.url))
const webDir = path.resolve(scriptDir, '..')
const repoRoot = path.resolve(webDir, '../..')
const runtimeState = path.join(webDir, 'tests', 'e2e', '.runtime.json')
const imageFixture = '<svg xmlns="http://www.w3.org/2000/svg" width="240" height="160" viewBox="0 0 240 160"><rect width="240" height="160" fill="#286d63"/><circle cx="120" cy="80" r="50" fill="#dfa34f"/></svg>'
let fixtureRoot
let extraRoot
let localAppData
let recentDirectory
let app
let cleanupPromise
let heicFixtureAvailable = false

const cleanup = () => {
  if (cleanupPromise) return cleanupPromise
  cleanupPromise = (async () => {
    if (app && app.exitCode === null) {
      const exited = once(app, 'exit')
      app.kill()
      await exited
    }
    const cleanupErrors = []
    for (const target of [fixtureRoot, extraRoot, localAppData, runtimeState]) {
      if (!target) continue
      try {
        await rm(target, { recursive: target !== runtimeState, force: true, maxRetries: 8, retryDelay: 250 })
      } catch (error) {
        cleanupErrors.push(error)
      }
    }
    if (cleanupErrors.length > 0) throw new AggregateError(cleanupErrors, 'E2E fixture cleanup failed.')
  })()
  return cleanupPromise
}

try {
  fixtureRoot = await mkdtemp(path.join(os.tmpdir(), 'windows-mobile-explorer-e2e-'))
  extraRoot = await mkdtemp(path.join(os.tmpdir(), 'windows-mobile-explorer-extra-'))
  const largeFixture = path.join(fixtureRoot, '50.000 Einträge')
  const cleanFixture = path.join(fixtureRoot, 'clean-fixture')
  localAppData = await mkdtemp(path.join(os.tmpdir(), 'windows-mobile-explorer-profile-'))
  const configDir = path.join(localAppData, 'WindowsMobileExplorer')
  recentDirectory = path.join(localAppData, 'WindowsRecent')
  await mkdir(largeFixture, { recursive: true })
  await mkdir(cleanFixture, { recursive: true })
  await mkdir(recentDirectory, { recursive: true })
  const optionalHeicFixture = process.env.WINDOWS_MOBILE_EXPLORER_HEIC_FIXTURE
  if (optionalHeicFixture) {
    if (path.extname(optionalHeicFixture).toLowerCase() !== '.heic') {
      throw new Error('WINDOWS_MOBILE_EXPLORER_HEIC_FIXTURE must point to a .heic file.')
    }
    await access(optionalHeicFixture)
    await copyFile(optionalHeicFixture, path.join(fixtureRoot, 'heic-thumbnail-fixture.heic'))
    heicFixtureAvailable = true
  }
  await writeFile(path.join(fixtureRoot, 'touchprobe.svg'), imageFixture)
  await sharp(Buffer.from(imageFixture)).png().toFile(path.join(fixtureRoot, 'thumbnail-fixture.png'))
  await writeFile(path.join(fixtureRoot, 'iphone-preview.pdf'), createPdfFixture())
  await writeFile(path.join(fixtureRoot, 'large-preview.pdf'), createPdfFixture(true))
  await writeFile(path.join(fixtureRoot, 'corrupt-preview.pdf'), '%PDF-1.4\nthis is not a valid PDF')
  await copyFile(path.resolve(scriptDir, '../tests/e2e/fixtures/password-protected.pdf'), path.join(fixtureRoot, 'password-protected.pdf'))
  await copyFile(path.resolve(scriptDir, '../tests/e2e/fixtures/iphone-preview.mp4'), path.join(fixtureRoot, 'iphone-preview.mp4'))
  await copyFile(path.resolve(scriptDir, '../tests/e2e/fixtures/iphone-preview.mp3'), path.join(fixtureRoot, 'iphone-preview.mp3'))
  const longVideoDirectory = path.join(fixtureRoot, '5GB video')
  await mkdir(longVideoDirectory, { recursive: true })
  createSparseH264Mp4(
    path.resolve(scriptDir, '../tests/e2e/fixtures/iphone-preview.mp4'),
    path.join(longVideoDirectory, '5gb-playback-probe.mp4'),
  )
  await writeFile(path.join(extraRoot, 'zusatz.txt'), 'Zusätzliche Freigabe')
  const shortcutTarget = path.join(fixtureRoot, 'Zieltext.txt')
  await writeFile(shortcutTarget, 'Vorschau aus dem Ziel der Windows-Verknüpfung.')
  execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', path.join(scriptDir, 'create-shortcut.ps1'),
    path.join(fixtureRoot, 'Zielverknüpfung.lnk'), shortcutTarget], { windowsHide: true, stdio: 'pipe' })
  execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', path.join(scriptDir, 'create-shortcut.ps1'),
    path.join(recentDirectory, 'touchprobe zuletzt.lnk'), path.join(fixtureRoot, 'touchprobe.svg')], { windowsHide: true, stdio: 'pipe' })
  for (let start = 0; start < 50_000; start += 400) {
    await Promise.all(Array.from({ length: Math.min(400, 50_000 - start) }, (_, offset) =>
      writeFile(path.join(largeFixture, `datei-${String(start + offset).padStart(5, '0')}.txt`), 'x')))
  }
  await mkdir(configDir, { recursive: true })
  await writeFile(path.join(configDir, 'settings.json'), JSON.stringify({
    port: 5197,
    tailscaleHttpsPort: 8443,
    allowedLogins: [],
    shareAllDrives: false,
    roots: [{ path: fixtureRoot, readOnly: false, label: 'Testordner' }],
    readOnlyMode: false,
    protectSystemFolders: true,
    showHiddenByDefault: false,
  }, null, 2))

  const dotnet = path.join(repoRoot, '.tools', 'dotnet', 'dotnet.exe')
  const dotnetExe = await import('node:fs/promises').then(({ access }) => access(dotnet).then(() => dotnet).catch(() => 'dotnet'))
  const npmCli = path.join(path.dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js')
  const configuredHostDll = process.env.WINDOWS_MOBILE_EXPLORER_E2E_HOST_DLL
  const hostDll = configuredHostDll
    ? path.resolve(configuredHostDll)
    : path.join(repoRoot, 'src', 'Explorer.Host', 'bin', 'Debug', 'net10.0-windows', 'Explorer.Host.dll')

  await run(process.execPath, [npmCli, 'run', 'build'], { cwd: webDir })
  if (configuredHostDll) await access(hostDll)
  else await run(dotnetExe, ['build', path.join(repoRoot, 'src', 'Explorer.Host', 'Explorer.Host.csproj'), '--no-restore'], { cwd: repoRoot })
  app = spawn(dotnetExe, [hostDll, '--isolated-e2e'], {
    cwd: path.dirname(hostDll),
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe'],
    env: {
      ...process.env,
      LOCALAPPDATA: localAppData,
      TEMP: cleanFixture,
      TMP: cleanFixture,
      WINDOWS_MOBILE_EXPLORER_DATA_DIR: configDir,
      ASPNETCORE_ENVIRONMENT: 'Development',
      DOTNET_ENVIRONMENT: 'Development',
      WINDOWS_MOBILE_EXPLORER_E2E: '1',
      WINDOWS_MOBILE_EXPLORER_RECENT_DIR: recentDirectory,
      DOTNET_NOLOGO: '1',
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    },
  })
  app.stdout.pipe(process.stdout)
  app.stderr.pipe(process.stderr)
  if (!app.pid) throw new Error('Explorer.Host test process did not start.')
  await writeFile(runtimeState, JSON.stringify({ fixtureRoot, extraRoot, localAppData, heicFixtureAvailable, hostPid: app.pid }))
  await waitForServer(app)
  await once(app, 'exit')
} finally {
  await cleanup()
}

function run(command, args, options) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { ...options, windowsHide: true, stdio: 'inherit' })
    child.once('error', reject)
    child.once('exit', (code) => code === 0 ? resolve() : reject(new Error(`${command} exited with code ${code}`)))
  })
}

async function waitForServer(child) {
  const deadline = Date.now() + 60_000
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`Explorer.Host exited with code ${child.exitCode}`)
    try {
      const response = await fetch('http://127.0.0.1:5197/api/v1/health')
      if (response.ok) return
    } catch { /* Host is still starting. */ }
    await new Promise((resolve) => setTimeout(resolve, 200))
  }
  throw new Error('Explorer.Host did not become ready within 60 seconds.')
}

function createPdfFixture(large = false) {
  const stream = (text) => `<< /Length ${Buffer.byteLength(text, 'ascii')} >>\nstream\n${text}\nendstream`
  const page = (content) => `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F1 9 0 R >> >> /Contents ${content} 0 R >>`
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R 5 0 R 7 0 R] /Count 3 >>',
    page(4), stream(`${large ? `%${'x'.repeat(400_000)}\n` : ''}0 0 0 rg 20 230 260 120 re f BT /F1 18 Tf 30 190 Td (PDF test page 1) Tj ET`),
    page(6), stream('0 0 0 rg 20 200 260 150 re f BT /F1 18 Tf 30 160 Td (PDF test page 2) Tj ET'),
    page(8), stream('0 0 0 rg 20 170 260 180 re f BT /F1 18 Tf 30 130 Td (PDF test page 3) Tj ET'),
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ]
  let pdf = '%PDF-1.4\n'
  const offsets = [0]
  for (const [index, object] of objects.entries()) {
    offsets.push(Buffer.byteLength(pdf, 'ascii'))
    pdf += `${index + 1} 0 obj\n${object}\nendobj\n`
  }
  const xrefOffset = Buffer.byteLength(pdf, 'ascii')
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`
  for (const offset of offsets.slice(1)) pdf += `${String(offset).padStart(10, '0')} 00000 n \n`
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xrefOffset}\n%%EOF\n`
  return Buffer.from(pdf, 'ascii')
}
