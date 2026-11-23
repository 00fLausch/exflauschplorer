import type { ApiError, Drive, ExplorerShareSettings, FileEntry, FileProperties, FolderPage, Health, JobSnapshot, KnownFolder, OperationRequest, PinStatus, RecycleBinEntry, TextDocument, UploadCommitResult, UploadSession, WaveformResult } from './types'
import { de } from './locales/de'

const API = '/api/v1'

function streamPathUrl(path: string, download = false) {
  const params = new URLSearchParams({ path })
  if (download) params.set('download', 'true')
  return `${API}/stream?${params}`
}

export class ApiException extends Error {
  readonly code?: string
  readonly statusCode?: number
  constructor(message: string, code?: string, statusCode?: number) { super(message); this.code = code; this.statusCode = statusCode; this.name = 'ApiException' }
}

export async function apiRequest<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.method && !['GET', 'HEAD', 'OPTIONS'].includes(init.method.toUpperCase())) {
    headers.set('X-Explorer-Request', '1')
    if (init.body && !(init.body instanceof FormData) && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  }
  const response = await fetch(`${API}${path}`, { ...init, headers, credentials: 'same-origin' })
  if (!response.ok) {
    let error: ApiError = { message: de.apiRequestFailed(response.status) }
    try { error = await response.json() as ApiError } catch { /* keep the German fallback */ }
    if (error.code === 'pin_locked') window.dispatchEvent(new Event('explorer:pin-locked'))
    throw new ApiException(error.message, error.code, response.status)
  }
  if (response.status === 204) return undefined as T
  return await response.json() as T
}

export const explorerApi = {
  folderSizes: (path: string, signal?: AbortSignal, refresh = false) => apiRequest<{ status: 'processing' | 'ready' }>(`/folder-sizes?${new URLSearchParams({ path, ...(refresh ? { refresh: 'true' } : {}) })}`, { signal }),
  pinStatus: () => apiRequest<PinStatus>('/security/pin/status'),
  configurePin: (pin: string) => apiRequest<{ ok: true }>('/security/pin/configure', { method: 'POST', body: JSON.stringify({ pin }) }),
  unlockPin: (pin: string) => apiRequest<{ ok: true }>('/security/pin/unlock', { method: 'POST', body: JSON.stringify({ pin }) }),
  lockPin: () => apiRequest<void>('/security/pin/lock', { method: 'POST' }),
  disablePin: () => apiRequest<{ ok: true }>('/security/pin/disable', { method: 'POST' }),
  health: (signal?: AbortSignal) => apiRequest<Health>('/health', { signal }),
  shareSettings: () => apiRequest<ExplorerShareSettings>('/settings/shares'),
  updateShareSettings: (settings: ExplorerShareSettings) => apiRequest<ExplorerShareSettings>('/settings/shares', { method: 'PUT', body: JSON.stringify(settings) }),
  drives: () => apiRequest<Drive[]>('/drives'),
  knownFolders: () => apiRequest<KnownFolder[]>('/known-folders'),
  quickAccess: () => apiRequest<FileEntry[]>('/quick-access'),
  favorites: () => apiRequest<string[]>('/favorites'),
  updateFavorites: (paths: string[]) => apiRequest<string[]>('/favorites', { method: 'PUT', body: JSON.stringify({ paths }) }),
  windowsRecent: () => apiRequest<FileEntry[]>('/windows-recent'),
  resolveShortcut: (path: string) => apiRequest<FileEntry>(`/shortcuts/resolve?${new URLSearchParams({ path })}`),
  entries: (options: { path: string; skip?: number; pageSize?: number; sort?: string; descending?: boolean; query?: string; showHidden?: boolean; showSystem?: boolean; extension?: string; minSize?: number; maxSize?: number; modifiedAfter?: string; modifiedBefore?: string; refresh?: boolean; kind?: string; signal?: AbortSignal }) => {
    const params = new URLSearchParams({
      path: options.path,
      skip: String(options.skip ?? 0),
      pageSize: String(options.pageSize ?? 100),
      sort: options.sort ?? 'name',
      descending: String(options.descending ?? false),
      q: options.query ?? '',
      showHidden: String(options.showHidden ?? false),
      showSystem: String(options.showSystem ?? false),
    })
    if (options.kind && options.kind !== 'all') params.set('kind', options.kind)
    if (options.extension) params.set('extension', options.extension.replace(/^\./, ''))
    if (options.minSize !== undefined) params.set('minSize', String(options.minSize))
    if (options.maxSize !== undefined) params.set('maxSize', String(options.maxSize))
    if (options.modifiedAfter) params.set('modifiedAfter', options.modifiedAfter)
    if (options.modifiedBefore) params.set('modifiedBefore', options.modifiedBefore)
    if (options.refresh) params.set('refresh', 'true')
    return apiRequest<FolderPage>(`/entries?${params}`, { signal: options.signal })
  },
  watchUrl: (path: string) => `${API}/watch?${new URLSearchParams({ path })}`,
  thumbnailUrl: (path: string, size = 320) => `${API}/thumbnail?${new URLSearchParams({ path, size: String(size) })}`,
  streamPathUrl,
  waveform: (path: string, signal?: AbortSignal) => apiRequest<WaveformResult>(`/waveform?${new URLSearchParams({ path })}`, { signal }),
  streamUrl: (entry: FileEntry, download = false) => streamPathUrl(entry.path, download),
  archiveUrl: (path: string) => `${API}/archive?${new URLSearchParams({ path })}`,
  jobs: () => apiRequest<JobSnapshot[]>('/jobs'),
  jobsUrl: () => `${API}/jobs/events`,
  enqueue: (request: OperationRequest) => apiRequest<JobSnapshot>('/jobs', { method: 'POST', body: JSON.stringify(request) }),
  jobAction: (id: string, action: 'pause' | 'resume' | 'cancel') => apiRequest<JobSnapshot>(`/jobs/${encodeURIComponent(id)}/${action}`, { method: 'POST' }),
  resolveConflict: (id: string, choice: string, applyToAll: boolean) => apiRequest<JobSnapshot>(`/jobs/${encodeURIComponent(id)}/conflict`, { method: 'POST', body: JSON.stringify({ choice, applyToAll }) }),
  undo: () => apiRequest<JobSnapshot>('/undo', { method: 'POST' }),
  recycleBin: () => apiRequest<RecycleBinEntry[]>('/recycle-bin'),
  restoreRecycleBin: (id: string) => apiRequest<void>(`/recycle-bin/${encodeURIComponent(id)}/restore`, { method: 'POST' }),
  deleteRecycleBin: (id: string) => apiRequest<void>(`/recycle-bin/${encodeURIComponent(id)}/delete`, { method: 'POST' }),
  emptyRecycleBin: () => apiRequest<void>('/recycle-bin/empty', { method: 'POST' }),
  openOnPc: (path: string, reveal = false) => apiRequest<void>('/open', { method: 'POST', body: JSON.stringify({ path, reveal }) }),
  properties: (path: string, hashAlgorithm?: string) => {
    const params = new URLSearchParams({ path })
    if (hashAlgorithm) params.set('hashAlgorithm', hashAlgorithm)
    return apiRequest<FileProperties>(`/properties?${params}`)
  },
  setAttributes: (path: string, change: { readOnly?: boolean; hidden?: boolean; archive?: boolean }) =>
    apiRequest<void>('/properties/attributes', { method: 'POST', body: JSON.stringify({ path, readOnly: change.readOnly ?? null, hidden: change.hidden ?? null, archive: change.archive ?? null }) }),
  searchUrl: (options: { root: string; query: string; showHidden?: boolean; showSystem?: boolean; content?: boolean; extension?: string; minSize?: number; maxSize?: number; modifiedAfter?: string; modifiedBefore?: string }) => {
    const params = new URLSearchParams({ root: options.root, q: options.query, showHidden: String(options.showHidden ?? false), showSystem: String(options.showSystem ?? false), content: String(options.content ?? false) })
    if (options.extension) params.set('extension', options.extension.replace(/^\./, ''))
    if (options.minSize !== undefined) params.set('minSize', String(options.minSize))
    if (options.maxSize !== undefined) params.set('maxSize', String(options.maxSize))
    if (options.modifiedAfter) params.set('modifiedAfter', options.modifiedAfter)
    if (options.modifiedBefore) params.set('modifiedBefore', options.modifiedBefore)
    return `${API}/search?${params}`
  },
  readText: (path: string) => apiRequest<TextDocument>(`/text?${new URLSearchParams({ path })}`),
  saveText: (document: Pick<TextDocument, 'path' | 'content' | 'eTag'>) => apiRequest<TextDocument>('/text', { method: 'PUT', body: JSON.stringify(document) }),
  uploads: () => apiRequest<UploadSession[]>('/uploads'),
  createUpload: (request: { fileName: string; size: number; destination: string; resumeId?: string }, signal?: AbortSignal) => apiRequest<UploadSession>('/uploads', { method: 'POST', body: JSON.stringify(request), signal }),
  uploadChunk: async (id: string, offset: number, chunk: Blob, sha256: string, signal?: AbortSignal) => {
    const params = new URLSearchParams({ offset: String(offset), sha256 })
    const response = await fetch(`${API}/uploads/${encodeURIComponent(id)}/chunk?${params}`, {
      method: 'PUT', body: chunk, signal, credentials: 'same-origin',
      headers: { 'X-Explorer-Request': '1', 'Content-Type': 'application/octet-stream' },
    })
    if (!response.ok) {
      let error: ApiError = { message: de.uploadFailed(response.status) }
      try { error = await response.json() as ApiError } catch { /* use the German fallback */ }
      if (error.code === 'pin_locked') window.dispatchEvent(new Event('explorer:pin-locked'))
      throw new ApiException(error.message, error.code, response.status)
    }
    return await response.json() as { id: string; offset: number; complete: boolean }
  },
  commitUpload: (id: string, conflictMode?: string, expectedSha256?: string, signal?: AbortSignal) => apiRequest<UploadCommitResult>(`/uploads/${encodeURIComponent(id)}/commit`, { method: 'POST', body: JSON.stringify({ conflictMode, expectedSha256 }), signal }),
  cancelUpload: (id: string) => apiRequest<void>(`/uploads/${encodeURIComponent(id)}`, { method: 'DELETE' }),
}

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes)) return '—'
  if (bytes < 1024) return `${bytes} ${de.byteUnit}`
  const units = ['KB', 'MB', 'GB', 'TB', 'PB']
  let value = bytes
  let index = -1
  do { value /= 1024; index += 1 } while (value >= 1024 && index < units.length - 1)
  return `${new Intl.NumberFormat(de.locale, { maximumFractionDigits: value < 10 ? 1 : 0 }).format(value)} ${units[index]}`
}

export function formatDate(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '—'
  const now = new Date()
  const sameDay = date.toDateString() === now.toDateString()
  return new Intl.DateTimeFormat(de.locale, sameDay
    ? { hour: '2-digit', minute: '2-digit' }
    : { day: '2-digit', month: '2-digit', year: '2-digit' }).format(date)
}
