import { access, realpath, readFile, rm } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptDir = path.dirname(fileURLToPath(import.meta.url))
const webDir = path.resolve(scriptDir, '..')
const runtimeState = path.join(webDir, 'tests', 'e2e', '.runtime.json')

export default async function globalTeardown() {
  let state
  try {
    state = JSON.parse(await readFile(runtimeState, 'utf8'))
  } catch (error) {
    if (error?.code === 'ENOENT') return
    throw error
  }

  if (Number.isInteger(state.hostPid) && state.hostPid > 0) {
    try { process.kill(state.hostPid, 'SIGTERM') }
    catch (error) { if (error?.code !== 'ESRCH') throw error }
    if (!await waitUntil(async () => !(await processIsAlive(state.hostPid)), 30_000))
      throw new Error('Explorer.Host test server did not stop.')
  }

  if (!await waitUntil(async () => !(await exists(runtimeState)), 30_000) && await exists(runtimeState)) {
    const fixtureRoot = await exists(state.fixtureRoot) ? await validateTempChild(state.fixtureRoot, 'fixture') : null
    const extraRoot = await exists(state.extraRoot) ? await validateTempChild(state.extraRoot, 'extra') : null
    const localAppData = await exists(state.localAppData) ? await validateTempChild(state.localAppData, 'profile') : null
    for (const target of [fixtureRoot, extraRoot, localAppData]) {
      if (target) await rm(target, { recursive: true, force: true, maxRetries: 8, retryDelay: 250 })
    }
    await rm(runtimeState, { force: true, maxRetries: 8, retryDelay: 250 })
  }
}

async function waitUntil(predicate, timeoutMs, message) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (await predicate()) return true
    await new Promise((resolve) => setTimeout(resolve, 100))
  }
  if (message) throw new Error(message)
  return false
}

async function processIsAlive(pid) {
  try { process.kill(pid, 0); return true }
  catch (error) { if (error?.code === 'ESRCH') return false; throw error }
}

async function exists(file) {
  try { await access(file); return true }
  catch { return false }
}

// This fallback is intentionally limited to unique, verified children of os.tmpdir().
async function validateTempChild(target, kind) {
  if (typeof target !== 'string' || target.length === 0) throw new Error(`Missing ${kind} fixture path.`)
  const resolved = path.resolve(target)
  const actualParent = await realpath(path.dirname(resolved))
  const actualTemp = await realpath(os.tmpdir())
  if (actualParent.toLowerCase() !== actualTemp.toLowerCase()) throw new Error(`Refusing to clean ${kind} outside the temp root.`)
  const patterns = {
    fixture: /^windows-mobile-explorer-e2e-[A-Za-z0-9]+$/,
    extra: /^windows-mobile-explorer-extra-[A-Za-z0-9]+$/,
    profile: /^windows-mobile-explorer-profile-[A-Za-z0-9]+$/,
  }
  const pattern = patterns[kind]
  if (!pattern.test(path.basename(resolved))) throw new Error(`Refusing to clean an unexpected ${kind} directory.`)
  return resolved
}

/** @typedef {{ fixtureRoot: string, extraRoot?: string, localAppData: string, hostPid?: number }} RuntimeState */
