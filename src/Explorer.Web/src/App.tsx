import { Component, lazy, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useVirtualizer } from '@tanstack/react-virtual'
import {
  Archive, ArrowDown, ArrowLeft, ArrowRight, ArrowUp, AudioLines, Check, ChevronDown,
  ChevronRight, CircleAlert, Clock3, Copy, Download, File, FileArchive, FileCode2, FileImage,
  FileText, Film, Folder, FolderOpen, HardDrive, Heart, Info, List, LockKeyhole, LoaderCircle,
  MoreHorizontal, Music2, PanelTop, Pin, RefreshCw, Search, Settings2, Share2, SlidersHorizontal,
  Smartphone, SortAsc, TableProperties, X, Scissors, Trash2, PenLine, FolderPlus, FilePlus2, Sparkles,
  Undo2, Pause, Play, Square, ClipboardPaste, MoveRight, ChevronUp, Upload, Menu,
} from 'lucide-react'
import { ApiException, explorerApi, formatBytes, formatDate } from './api'
import { de } from './locales/de'
import type { Drive, ExplorerShareSettings, FileEntry, FileProperties, FolderPage, Health, JobSnapshot, KnownFolder, PinStatus, RecycleBinEntry, ShareRootSetting, TextDocument, UploadSession } from './types'
import { useModalDialogFocus } from './useModalDialogFocus'
import { setAppTextScale } from './textScale'
import { FolderNavigation, FolderNavigationDrawer } from './FolderNavigation'
import { MediaPlayer } from './MediaPlayer'
import { CleanPage } from './CleanPage'
import { isPlayableMedia } from './mediaUtils'
import './App.css'
import './signal-slate.css'

const PdfViewer = lazy(() => import('./PdfViewer'))

class PdfPreviewBoundary extends Component<{ children: React.ReactNode; source: string }, { failed: boolean }> {
  state = { failed: false }
  static getDerivedStateFromError() { return { failed: true } }
  render() { return this.state.failed ? <div className="pdf-message" role="alert">{de.pdfOpenError}<a href={this.props.source} target="_blank" rel="noopener noreferrer">{de.pdfOpenBrowser}</a></div> : this.props.children }
}

type ViewMode = 'list' | 'details' | 'grid'
type MainPage = 'files' | 'recent' | 'transfers' | 'more' | 'recycle' | 'clean'
type ContextMenuState = { entry: FileEntry; top: number; left: number } | null
type ToastMessage = { id: number; message: string; action?: { label: string; run: () => void } }
type FileTemplate = 'text' | 'markdown' | 'json' | 'csv' | 'html' | 'javascript'
type GroupMode = 'none' | 'type' | 'date' | 'size'
type VirtualRow = { type: 'group'; key: string; label: string } | { type: 'entry'; key: string; entry: FileEntry } | { type: 'entries'; key: string; entries: FileEntry[] }
type FolderHistory = { paths: Array<string | null>; index: number }

const RECENT_KEY = 'windows-mobile-explorer:recent'
const LEGACY_FAVORITES_KEY = 'windows-mobile-explorer:favorites'
const LAST_PATH_KEY = 'windows-mobile-explorer:last-path'
const FOLDER_HISTORY_KEY = 'windows-mobile-explorer:folder-history'
const SORT_KEY = 'windows-mobile-explorer:sort'
const MAX_FOLDER_HISTORY = 80
const TEXT_SIZE_KEY = 'windows-mobile-explorer:text-size'
const TEXT_SIZE_PERCENTAGES = [100, 125, 150, 175, 200]
const PAGE_SIZE = 160
const FILE_TEMPLATES: Record<FileTemplate, { label: string; extension: string }> = {
  text: { label: de.uiLeereTextdatei, extension: '.txt' },
  markdown: { label: de.uiMarkdown, extension: '.md' },
  json: { label: de.uiJSON, extension: '.json' },
  csv: { label: de.uiCSVMitKopfzeile, extension: '.csv' },
  html: { label: de.uiHTMLSeite, extension: '.html' },
  javascript: { label: de.uiJavaScript, extension: '.js' },
}

function getRecent(): FileEntry[] {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]')
    if (!Array.isArray(value)) return []
    return value.filter((entry): entry is FileEntry => entry !== null && typeof entry === 'object' &&
      typeof entry.name === 'string' && typeof entry.path === 'string' && typeof entry.extension === 'string' &&
      typeof entry.modified === 'string' && typeof entry.contentType === 'string' && typeof entry.isDirectory === 'boolean')
  } catch { return [] }
}

function getLegacyFavorites(): string[] | null {
  try {
    const stored = localStorage.getItem(LEGACY_FAVORITES_KEY)
    if (stored === null) return null
    const paths: unknown = JSON.parse(stored)
    return Array.isArray(paths) ? paths.filter((path): path is string => typeof path === 'string') : []
  } catch { return null }
}

function getLastPath(): string | null {
  try { return localStorage.getItem(LAST_PATH_KEY) || null } catch { return null }
}

function saveLastPath(path: string | null) {
  try {
    if (path) localStorage.setItem(LAST_PATH_KEY, path)
    else localStorage.removeItem(LAST_PATH_KEY)
  } catch { /* the current session still keeps its navigation state */ }
}

function getFolderHistory(): FolderHistory {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(FOLDER_HISTORY_KEY) ?? 'null')
    if (!stored || typeof stored !== 'object' || !('paths' in stored) || !('index' in stored)) return { paths: [null], index: 0 }
    const value = stored as { paths: unknown; index: unknown }
    if (!Array.isArray(value.paths) || value.paths.length === 0 || value.paths.length > MAX_FOLDER_HISTORY ||
      value.paths[0] !== null || !value.paths.every((path) => path === null || (typeof path === 'string' && path.length > 0)) ||
      !Number.isInteger(value.index) || (value.index as number) < 0 || (value.index as number) >= value.paths.length)
      return { paths: [null], index: 0 }
    return { paths: value.paths as Array<string | null>, index: value.index as number }
  } catch { return { paths: [null], index: 0 } }
}

function saveFolderHistory(paths: Array<string | null>, index: number) {
  try { localStorage.setItem(FOLDER_HISTORY_KEY, JSON.stringify({ paths, index })) } catch { /* device history can be unavailable */ }
}

function getTextSize(): number {
  try {
    const stored = Number(localStorage.getItem(TEXT_SIZE_KEY))
    return TEXT_SIZE_PERCENTAGES.includes(stored) ? stored : 100
  } catch { return 100 }
}

function isTransientUploadFailure(cause: unknown): boolean {
  if (cause instanceof TypeError) return true
  return cause instanceof ApiException && (cause.statusCode === 408 || cause.statusCode === 429 || (cause.statusCode !== undefined && cause.statusCode >= 500))
}

function waitForUploadRetry(delay: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) { reject(new DOMException('Upload abgebrochen', 'AbortError')); return }
    const timer = setTimeout(() => { signal.removeEventListener('abort', abort); resolve() }, delay)
    const abort = () => { clearTimeout(timer); signal.removeEventListener('abort', abort); reject(new DOMException('Upload abgebrochen', 'AbortError')) }
    signal.addEventListener('abort', abort, { once: true })
  })
}

function isAbortFailure(cause: unknown): boolean {
  return cause instanceof Error && cause.name === 'AbortError'
}

const UPLOAD_STORAGE_PREFIX = 'windows-mobile-explorer:upload:'
function uploadStorageKey(destination: string, file: File) { return `${UPLOAD_STORAGE_PREFIX}${encodeURIComponent(destination)}:${encodeURIComponent(file.name)}:${file.size}:${file.lastModified}` }
function forgetUploadId(id: string) {
  try { for (let index = localStorage.length - 1; index >= 0; index--) { const key = localStorage.key(index); if (key?.startsWith(UPLOAD_STORAGE_PREFIX) && localStorage.getItem(key) === id) localStorage.removeItem(key) } } catch { /* local persistence can be disabled by the browser */ }
}

function parentPath(path: string): string | null {
  const trimmed = path.replace(/[\\/]+$/, '')
  const slash = Math.max(trimmed.lastIndexOf('\\'), trimmed.lastIndexOf('/'))
  if (slash < 0) return null
  if (slash === 2 && /^[A-Za-z]:/.test(trimmed)) return trimmed.slice(0, 3)
  if (slash === 0) return path.startsWith('\\\\') ? null : '\\'
  return trimmed.slice(0, slash)
}

function sameWindowsPath(left: string, right: string): boolean {
  const normalize = (path: string) => path.replaceAll('/', '\\').replace(/[\\]+$/, '').toLocaleLowerCase()
  return normalize(left) === normalize(right)
}

function pathParts(path: string): Array<{ label: string; path: string }> {
  if (/^[A-Za-z]:\\?$/.test(path)) return [{ label: path.slice(0, 2), path: path.slice(0, 3) }]
  if (path.startsWith('\\\\')) {
    const pieces = path.replace(/^\\\\/, '').split('\\').filter(Boolean)
    const serverShare = pieces.slice(0, 2).join('\\')
    const result = [{ label: serverShare, path: `\\\\${serverShare}\\` }]
    let current = `\\\\${serverShare}\\`
    pieces.slice(2).forEach((part) => {
      current = current.endsWith('\\') ? `${current}${part}` : `${current}\\${part}`
      result.push({ label: part, path: current })
    })
    return result
  }
  const root = path.slice(0, 3)
  const result = [{ label: root.slice(0, 2), path: root }]
  let current = root
  path.slice(3).split('\\').filter(Boolean).forEach((part) => {
    current = current.endsWith('\\') ? `${current}${part}` : `${current}\\${part}`
    result.push({ label: part, path: current })
  })
  return result
}

function sizeMegabytesToBytes(value: string): number | undefined {
  if (!value.trim()) return undefined
  const number = Number(value)
  return Number.isFinite(number) && number >= 0 ? Math.floor(number * 1024 * 1024) : undefined
}

function localDateBound(value: string, endOfDay = false): string | undefined {
  if (!value) return undefined
  const date = new Date(`${value}T00:00:00`)
  if (Number.isNaN(date.getTime())) return undefined
  if (endOfDay) date.setHours(23, 59, 59, 999)
  return date.toISOString()
}

function displayFileName(entry: FileEntry, showExtensions: boolean): string {
  if (showExtensions || entry.isDirectory || !entry.extension) return entry.name
  return entry.name.slice(0, -(entry.extension.length + 1))
}

function getGroup(entry: FileEntry, mode: GroupMode): { label: string; rank: number } {
  if (mode === 'type') {
    if (entry.isDirectory) return { label: de.uiOrdner, rank: 0 }
    const extension = entry.extension.trim().toUpperCase()
    return { label: extension ? de.fileGroup(extension) : de.uiOhneDateiendung, rank: extension ? extension.charCodeAt(0) + 1 : 1 }
  }
  if (mode === 'size') {
    if (entry.isDirectory) return { label: de.uiOrdner, rank: -1 }
    const bytes = entry.size ?? 0
    if (bytes === 0) return { label: de.uiLeer, rank: 0 }
    if (bytes < 16 * 1024) return { label: de.uiSehrKleinUnter16KB, rank: 1 }
    if (bytes < 1024 * 1024) return { label: de.uiKleinUnter1MB, rank: 2 }
    if (bytes < 128 * 1024 * 1024) return { label: de.uiMittelUnter128MB, rank: 3 }
    if (bytes < 1024 * 1024 * 1024) return { label: de.uiGrossUnter1GB, rank: 4 }
    return { label: de.uiSehrGrossAb1GB, rank: 5 }
  }
  const date = new Date(entry.modified)
  if (Number.isNaN(date.getTime())) return { label: de.uiDatumUnbekannt, rank: 6 }
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  date.setHours(0, 0, 0, 0)
  const days = Math.floor((today.getTime() - date.getTime()) / 86_400_000)
  if (days <= 0) return { label: de.uiHeute, rank: 0 }
  if (days === 1) return { label: de.uiGestern, rank: 1 }
  if (days < 7) return { label: de.uiDieseWoche, rank: 2 }
  if (days < 14) return { label: de.uiLetzteWoche, rank: 3 }
  if (date.getMonth() === today.getMonth() && date.getFullYear() === today.getFullYear()) return { label: de.uiDiesenMonat, rank: 4 }
  return { label: de.uiAlter, rank: 5 }
}

function compareEntries(a: FileEntry, b: FileEntry, sort: string, descending: boolean): number {
  const direction = descending ? -1 : 1
  let result = 0
  if (sort === 'modified') result = Date.parse(a.modified) - Date.parse(b.modified)
  else if (sort === 'size' || sort === 'folder-size') {
    if (sort === 'folder-size' && a.isDirectory && b.isDirectory && Boolean(a.sizeIncomplete) !== Boolean(b.sizeIncomplete)) return a.sizeIncomplete ? 1 : -1
    result = (a.size ?? -1) - (b.size ?? -1)
  }
  else if (sort === 'type') result = a.extension.localeCompare(b.extension, de.collatorLocale, { sensitivity: 'base' })
  else result = a.name.localeCompare(b.name, de.collatorLocale, { numeric: true, sensitivity: 'base' })
  return result === 0 ? a.name.localeCompare(b.name, de.collatorLocale, { numeric: true, sensitivity: 'base' }) : result * direction
}

function entrySizeLabel(entry: FileEntry): string | null {
  if (entry.size === null || (entry.isDirectory && entry.sizeIncomplete == null)) return null
  return `${entry.sizeIncomplete ? '≥ ' : ''}${formatBytes(entry.size)}`
}

function fileKind(entry: FileEntry): 'image' | 'video' | 'audio' | 'code' | 'archive' | 'document' | 'file' {
  if (entry.isDirectory) return 'file'
  const ext = entry.extension.toLowerCase()
  if (['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg', 'heic', 'heif', 'tif', 'tiff', 'avif'].includes(ext)) return 'image'
  if (['mp4', 'mov', 'm4v', 'mkv', 'avi', 'webm', 'wmv', 'mpeg', 'mpg'].includes(ext)) return 'video'
  if (['mp3', 'm4a', 'wav', 'flac', 'aac', 'ogg', 'wma', 'opus'].includes(ext)) return 'audio'
  if (['js', 'jsx', 'ts', 'tsx', 'css', 'html', 'json', 'xml', 'yml', 'yaml', 'py', 'cs', 'go', 'rs', 'java', 'sh', 'ps1', 'md', 'txt', 'log', 'ini', 'toml'].includes(ext)) return 'code'
  if (['zip', '7z', 'rar', 'tar', 'gz', 'bz2'].includes(ext)) return 'archive'
  if (['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'odt', 'rtf'].includes(ext)) return 'document'
  return 'file'
}

function EntryGlyph({ entry, large = false }: { entry: FileEntry; large?: boolean }) {
  if (entry.isDirectory) return <span className={`entry-glyph folder-glyph${large ? ' is-large' : ''}`}><Folder fill="currentColor" strokeWidth={1.6} /></span>
  const kind = fileKind(entry)
  const Icon = kind === 'image' ? FileImage : kind === 'video' ? Film : kind === 'audio' ? AudioLines : kind === 'code' ? FileCode2 : kind === 'archive' ? FileArchive : kind === 'document' ? FileText : File
  return <span className={`entry-glyph ${kind}-glyph${large ? ' is-large' : ''}`}><Icon strokeWidth={1.7} /></span>
}

function EntryThumbnail({ entry, large = false }: { entry: FileEntry; large?: boolean }) {
  const [failedPath, setFailedPath] = useState('')
  const kind = fileKind(entry)
  const failed = failedPath === entry.path
  if (failed || !['image', 'video', 'document'].includes(kind)) return <EntryGlyph entry={entry} large={large} />
  return <img className={`entry-thumbnail${large ? ' is-large' : ''}`} src={explorerApi.thumbnailUrl(entry.path, large ? 480 : 160)} alt="" loading="lazy" onError={() => setFailedPath(entry.path)} />
}

function App() {
  const [health, setHealth] = useState<Health | null>(null)
  const [pinStatus, setPinStatus] = useState<PinStatus | null>(null)
  const [pinChecked, setPinChecked] = useState(false)
  const [pinError, setPinError] = useState('')
  const [pinSheetOpen, setPinSheetOpen] = useState(false)
  const [drives, setDrives] = useState<Drive[]>([])
  const [knownFolders, setKnownFolders] = useState<KnownFolder[]>([])
  const [quickAccessFolders, setQuickAccessFolders] = useState<FileEntry[]>([])
  const [shareSettings, setShareSettings] = useState<ExplorerShareSettings | null>(null)
  const [shareSettingsLoading, setShareSettingsLoading] = useState(false)
  const [shareSettingsSaving, setShareSettingsSaving] = useState(false)
  const [shareSettingsError, setShareSettingsError] = useState('')
  const [currentPath, setCurrentPath] = useState<string | null>(null)
  const [history, setHistory] = useState<Array<string | null>>(() => getFolderHistory().paths)
  const [historyIndex, setHistoryIndex] = useState(() => getFolderHistory().index)
  const [entries, setEntries] = useState<FileEntry[]>([])
  const [hasMore, setHasMore] = useState(false)
  const [totalCount, setTotalCount] = useState<number | null>(null)
  const [pageLoading, setPageLoading] = useState(false)
  const [loading, setLoading] = useState(true)
  const [connection, setConnection] = useState<'online' | 'offline' | 'checking'>('checking')
  const [error, setError] = useState('')
  const [mainPage, setMainPage] = useState<MainPage>('files')
  const [navigationOpen, setNavigationOpen] = useState(false)
  const [view, setView] = useState<ViewMode>('list')
  const [groupBy, setGroupBy] = useState<GroupMode>('none')
  const [sort, setSort] = useState(() => {
    try { const saved = JSON.parse(localStorage.getItem(SORT_KEY) ?? '{}'); return ['name', 'modified', 'size', 'folder-size', 'type'].includes(saved.sort) ? saved.sort as string : 'name' } catch { return 'name' }
  })
  const [descending, setDescending] = useState(() => {
    try { return JSON.parse(localStorage.getItem(SORT_KEY) ?? '{}').descending === true } catch { return false }
  })
  const [folderSizeState, setFolderSizeState] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle')
  const [folderSizeRefreshKey, setFolderSizeRefreshKey] = useState(0)
  const folderSizeRefreshRef = useRef(0)
  const [query, setQuery] = useState('')
  const [deepSearchActive, setDeepSearchActive] = useState(false)
  const [deepSearching, setDeepSearching] = useState(false)
  const [searchContent, setSearchContent] = useState(false)
  const [homeQuery, setHomeQuery] = useState('')
  const [showHidden, setShowHidden] = useState(false)
  const [showSystem, setShowSystem] = useState(() => localStorage.getItem('windows-mobile-explorer:show-system') === 'true')
  const [showExtensions, setShowExtensions] = useState(() => localStorage.getItem('windows-mobile-explorer:show-extensions') !== 'false')
  const [extensionFilter, setExtensionFilter] = useState('')
  const [minSizeFilter, setMinSizeFilter] = useState('')
  const [maxSizeFilter, setMaxSizeFilter] = useState('')
  const [modifiedAfterFilter, setModifiedAfterFilter] = useState('')
  const [modifiedBeforeFilter, setModifiedBeforeFilter] = useState('')
  const [filter, setFilter] = useState('all')
  const [filterOpen, setFilterOpen] = useState(false)
  const [folderSearchOpen, setFolderSearchOpen] = useState(false)
  const [viewMenuOpen, setViewMenuOpen] = useState(false)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [pathOpen, setPathOpen] = useState(false)
  const [newFileDialogOpen, setNewFileDialogOpen] = useState(false)
  const [newFileTemplate, setNewFileTemplate] = useState<FileTemplate>('text')
  const [newFileName, setNewFileName] = useState<string>(de.uiNeueDateiTxt)
  const [pathInput, setPathInput] = useState('')
  const [preview, setPreview] = useState<FileEntry | null>(null)
  const previewReturnFocusRef = useRef<HTMLElement | null>(null)
  const [editorDocument, setEditorDocument] = useState<TextDocument | null>(null)
  const [editorContent, setEditorContent] = useState('')
  const [editorLoading, setEditorLoading] = useState(false)
  const [editorSaving, setEditorSaving] = useState(false)
  const [editorError, setEditorError] = useState('')
  const [previewText, setPreviewText] = useState<string | null>(null)
  const [previewError, setPreviewError] = useState('')
  const [previewZoom, setPreviewZoom] = useState(1)
  const [mediaQueue, setMediaQueue] = useState<FileEntry[]>([])
  const mediaQueueRef = useRef<FileEntry[]>([])
  const [mediaStartPath, setMediaStartPath] = useState('')
  const [mediaSessionId, setMediaSessionId] = useState(0)
  const [mediaFolderLoading, setMediaFolderLoading] = useState(false)
  const mediaFolderController = useRef<AbortController | null>(null)
  useEffect(() => { mediaQueueRef.current = mediaQueue }, [mediaQueue])
  useEffect(() => () => mediaFolderController.current?.abort(), [])
  const [properties, setProperties] = useState<FileEntry | null>(null)
  const propertiesReturnFocusRef = useRef<HTMLElement | null>(null)
  const [propertyDetails, setPropertyDetails] = useState<FileProperties | null>(null)
  const [propertiesLoading, setPropertiesLoading] = useState(false)
  const [propertiesError, setPropertiesError] = useState('')
  const [contextMenu, setContextMenu] = useState<ContextMenuState>(null)
  const [selected, setSelected] = useState<string[]>([])
  const [selectionMode, setSelectionMode] = useState(false)
  const [selectAllProgress, setSelectAllProgress] = useState<{ loaded: number; total: number } | null>(null)
  const [favorites, setFavorites] = useState<string[]>([])
  const [recent, setRecent] = useState<FileEntry[]>(getRecent)
  const [windowsRecentEntries, setWindowsRecentEntries] = useState<FileEntry[]>([])
  const [windowsRecentLoading, setWindowsRecentLoading] = useState(false)
  const [windowsRecentError, setWindowsRecentError] = useState('')
  const [jobs, setJobs] = useState<JobSnapshot[]>([])
  const [clipboard, setClipboard] = useState<{ paths: string[]; mode: 'copy' | 'move' } | null>(null)
  const [picker, setPicker] = useState<{ kind: 'move' | 'zip' | 'unzip' | 'upload'; sources: string[] } | null>(null)
  const [pickerPath, setPickerPath] = useState<string | null>(null)
  const [pickerFolders, setPickerFolders] = useState<FileEntry[]>([])
  const [pickerLoading, setPickerLoading] = useState(false)
  const [recycleEntries, setRecycleEntries] = useState<RecycleBinEntry[]>([])
  const [recycleLoading, setRecycleLoading] = useState(false)
  const [uploads, setUploads] = useState<UploadSession[]>([])
  const [uploadingIds, setUploadingIds] = useState<string[]>([])
  const [reconnectingUploadIds, setReconnectingUploadIds] = useState<string[]>([])
  const activeUploadControllers = useRef(new Map<string, AbortController>())
  const [uploadConflict, setUploadConflict] = useState<UploadSession | null>(null)
  const [selectedUploadFiles, setSelectedUploadFiles] = useState<File[]>([])
  const jobSnapshots = useRef(new Map<string, JobSnapshot>())
  const locallyStartedJobs = useRef(new Set<string>())
  const undoToastsShown = useRef(new Set<string>())
  const [theme, setTheme] = useState<'light' | 'dark'>(() => {
    const stored = localStorage.getItem('windows-mobile-explorer:theme')
    return stored === 'dark' || stored === 'light' ? stored : 'dark'
  })
  const [textSize, setTextSize] = useState(getTextSize)
  const [toast, setToast] = useState<ToastMessage | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [refreshKey, setRefreshKey] = useState(0)
  const [columnCount, setColumnCount] = useState(() => window.innerWidth < 390 ? 2 : window.innerWidth < 700 ? 3 : 5)
  const [pathModalError, setPathModalError] = useState('')
  const contentRef = useRef<HTMLDivElement>(null)
  const breadcrumbRef = useRef<HTMLElement>(null)
  const folderSearchRef = useRef<HTMLInputElement>(null)
  const lastTouchY = useRef<number | null>(null)
  const requestRef = useRef<AbortController | null>(null)
  const loadedViewRef = useRef<string | null>(null)
  const entriesRef = useRef(entries)
  entriesRef.current = entries
  const searchAbortRef = useRef<AbortController | null>(null)
  const selectAllController = useRef<AbortController | null>(null)
  const uploadInputRef = useRef<HTMLInputElement>(null)
  const resumeUploadId = useRef<string | null>(null)
  const restoreLocationAttempted = useRef(false)
  const navigationLocked = useRef(false)
  const appShellRef = useRef<HTMLDivElement>(null)
  const navigationToggleRef = useRef<HTMLButtonElement>(null)
  const moreReturnPageRef = useRef<MainPage>('files')
  const mediaPlayerActive = mediaQueue.length > 0
  const selectionActive = selectionMode || selected.length > 0

  useEffect(() => {
    try { localStorage.setItem(SORT_KEY, JSON.stringify({ sort, descending })) } catch { /* browser storage may be unavailable */ }
  }, [sort, descending])
  useEffect(() => {
    if (!currentPath || mainPage !== 'files' || sort !== 'folder-size') { setFolderSizeState('idle'); return }
    const controller = new AbortController()
    let timer = 0
    let refresh = folderSizeRefreshKey !== folderSizeRefreshRef.current
    folderSizeRefreshRef.current = folderSizeRefreshKey
    setFolderSizeState('loading')
    const poll = async () => {
      try {
        const result = await explorerApi.folderSizes(currentPath, controller.signal, refresh)
        refresh = false
        if (controller.signal.aborted) return
        if (result.status === 'ready') { setFolderSizeState('ready'); setRefreshKey((value) => value + 1) }
        else timer = window.setTimeout(() => void poll(), 1500)
      } catch { if (!controller.signal.aborted) setFolderSizeState('error') }
    }
    void poll()
    return () => { controller.abort(); window.clearTimeout(timer) }
  }, [currentPath, mainPage, sort, folderSizeRefreshKey])

  useEffect(() => {
    if (folderSearchOpen) folderSearchRef.current?.focus({ preventScroll: true })
  }, [folderSearchOpen])
  useEffect(() => {
    setFolderSearchOpen(false)
    setViewMenuOpen(false)
    setFilterOpen(false)
  }, [currentPath])

  useLayoutEffect(() => {
    const shell = appShellRef.current
    const header = shell?.querySelector<HTMLElement>('.topbar')
    if (!shell || !header) return
    const update = () => shell.style.setProperty('--navigation-top', `${header.getBoundingClientRect().bottom}px`)
    const observer = new ResizeObserver(update)
    observer.observe(header)
    update()
    return () => observer.disconnect()
  }, [])

  useLayoutEffect(() => {
    const shell = appShellRef.current
    if (!shell || !mediaPlayerActive) return
    const controls = Array.from(shell.querySelectorAll<HTMLElement>('.explorer-content .folder-footer, .explorer-content .selection-bar, .explorer-content .bottom-nav'))
    const updatePosition = () => {
      const top = Math.min(window.innerHeight, ...controls.filter((element) => element.getClientRects().length > 0).map((element) => element.getBoundingClientRect().top))
      const bottom = Math.max(12, window.innerHeight - top + 12)
      shell.style.setProperty('--player-bottom-offset', `${bottom}px`)
      const mini = shell.querySelector<HTMLElement>('.player-mini')
      shell.style.setProperty('--player-toast-offset', `${bottom + (mini?.getBoundingClientRect().height ?? 128) + 12}px`)
    }
    const observer = new ResizeObserver(updatePosition)
    controls.forEach((element) => observer.observe(element))
    const player = shell.querySelector<HTMLElement>('.player-root')
    if (player) observer.observe(player)
    window.addEventListener('resize', updatePosition)
    updatePosition()
    return () => {
      observer.disconnect()
      window.removeEventListener('resize', updatePosition)
      shell.style.removeProperty('--player-bottom-offset')
      shell.style.removeProperty('--player-toast-offset')
    }
  }, [mediaPlayerActive, mainPage, currentPath, selectionActive])

  const notify = useCallback((message: string, action?: ToastMessage['action']) => {
    const id = Date.now()
    setToast({ id, message, action })
    window.setTimeout(() => setToast((current) => current?.id === id ? null : current), 4200)
  }, [])

  const showUndoToast = useCallback((job: JobSnapshot) => {
    if (!['completed', 'completed-with-skips'].includes(job.status) || job.kind === 'undo-batch' || undoToastsShown.current.has(job.id)) return
    undoToastsShown.current.add(job.id)
    const undo = () => {
      void explorerApi.undo()
        .then((undoJob) => {
          jobSnapshots.current.set(undoJob.id, undoJob)
          setJobs((current) => [undoJob, ...current.filter((item) => item.id !== undoJob.id)])
          notify(de.uiRuckgangigWirdAusgefuhrt)
          setRefreshKey((value) => value + 1)
        })
        .catch((cause) => notify(cause instanceof Error ? cause.message : de.uiDerVorgangKannNichtRuckgangigGemachtWerden))
    }
    notify(job.message, { label: de.uiRuckgangig, run: undo })
    setRefreshKey((value) => value + 1)
  }, [notify])

  const updateShowSystem = useCallback((value: boolean) => {
    setShowSystem(value)
    try { localStorage.setItem('windows-mobile-explorer:show-system', String(value)) } catch { /* device preferences can be unavailable */ }
  }, [])

  const updateShowExtensions = useCallback((value: boolean) => {
    setShowExtensions(value)
    try { localStorage.setItem('windows-mobile-explorer:show-extensions', String(value)) } catch { /* device preferences can be unavailable */ }
  }, [])

  const loadHome = useCallback(async () => {
    setLoading(true)
    setError('')
    try {
      const [status, driveList, folders, quickAccessList, sharing, savedFavoritePaths] = await Promise.all([
        explorerApi.health(), explorerApi.drives(), explorerApi.knownFolders(), explorerApi.quickAccess(), explorerApi.shareSettings(), explorerApi.favorites(),
      ])
      let favoritePaths = savedFavoritePaths
      const legacyFavorites = getLegacyFavorites()
      if (legacyFavorites !== null) {
        if (favoritePaths.length === 0) {
          const verifiedFavorites: string[] = []
          for (const path of legacyFavorites.slice(0, 100)) {
            try {
              const folder = await explorerApi.entries({ path, pageSize: 1 })
              verifiedFavorites.push(folder.path)
            } catch { /* Ignore paths that no longer exist or are no longer shared. */ }
          }
          if (verifiedFavorites.length > 0)
            favoritePaths = await explorerApi.updateFavorites([...new Set(verifiedFavorites)])
        }
        try { localStorage.removeItem(LEGACY_FAVORITES_KEY) } catch { /* Server favorites remain the source of truth. */ }
      }
      setHealth(status)
      setDrives(driveList)
      setKnownFolders(folders)
      setQuickAccessFolders(quickAccessList)
      setShareSettings(sharing)
      setFavorites(favoritePaths)
      setConnection('online')
      return true
    } catch (cause) {
      setConnection('offline')
      setError(cause instanceof Error ? cause.message : de.uiDerServerIstNichtErreichbar)
      return false
    } finally { setLoading(false) }
  }, [])

  const restoreLastLocation = useCallback(async () => {
    if (restoreLocationAttempted.current) return
    restoreLocationAttempted.current = true
    const savedPath = getLastPath()
    const storedHistory = getFolderHistory()
    let restoredPaths = storedHistory.paths
    let restoredIndex = storedHistory.index
    if (restoredPaths[restoredIndex] !== savedPath) {
      restoredPaths = savedPath ? [null, savedPath] : [null]
      restoredIndex = savedPath ? 1 : 0
    }
    if (!savedPath && restoredIndex === 0 && restoredPaths.length === 1) {
      setHistory(restoredPaths)
      setHistoryIndex(restoredIndex)
      saveFolderHistory(restoredPaths, restoredIndex)
      window.history.replaceState({ explorerPath: null, explorerIndex: 0 }, '', location.pathname)
      return
    }
    const controller = new AbortController()
    const timeout = window.setTimeout(() => controller.abort(), 10_000)
    try {
      if (savedPath) {
        const current = await explorerApi.entries({ path: savedPath, pageSize: 1, signal: controller.signal })
        restoredPaths[restoredIndex] = current.path
        saveLastPath(current.path)
      }
      setHistory(restoredPaths)
      setHistoryIndex(restoredIndex)
      setCurrentPath(restoredPaths[restoredIndex])
      saveFolderHistory(restoredPaths, restoredIndex)
      setMainPage('files')
      window.history.replaceState({ explorerPath: null, explorerIndex: 0 }, '', location.pathname)
      for (let index = 1; index < restoredPaths.length; index++)
        window.history.pushState({ explorerPath: restoredPaths[index], explorerIndex: index }, '', location.pathname)
      const positionDelta = restoredIndex - (restoredPaths.length - 1)
      if (positionDelta !== 0) window.history.go(positionDelta)
    } catch {
      saveLastPath(null)
      setHistory([null])
      setHistoryIndex(0)
      saveFolderHistory([null], 0)
      setCurrentPath(null)
      window.history.replaceState({ explorerPath: null, explorerIndex: 0 }, '', location.pathname)
      notify(de.uiLetzterOrdnerNichtErreichbar)
    } finally { window.clearTimeout(timeout) }
  }, [notify])

  const checkPinStatus = useCallback(async () => {
    setPinChecked(false)
    setPinError('')
    try {
      const status = await explorerApi.pinStatus()
      setPinStatus(status)
      setPinChecked(true)
      if (!status.isConfigured || status.isUnlocked) {
        if (await loadHome()) await restoreLastLocation()
      }
    } catch (cause) {
      setPinChecked(true)
      setPinError(cause instanceof Error ? cause.message : de.pinOffline)
      setConnection('offline')
      setLoading(false)
    }
  }, [loadHome, restoreLastLocation])

  useEffect(() => { void checkPinStatus() }, [checkPinStatus])
  useEffect(() => {
    if (mainPage !== 'recent' || !pinChecked || !pinStatus || (pinStatus.isConfigured && !pinStatus.isUnlocked)) return
    let active = true
    setWindowsRecentLoading(true)
    setWindowsRecentError('')
    explorerApi.windowsRecent()
      .then((items) => { if (active) setWindowsRecentEntries(items) })
      .catch((cause) => { if (active) setWindowsRecentError(cause instanceof Error ? cause.message : de.uiWindowsZuletztKonnteNichtGeladenWerden) })
      .finally(() => { if (active) setWindowsRecentLoading(false) })
    return () => { active = false }
  }, [mainPage, pinChecked, pinStatus, refreshKey])
  useEffect(() => {
    const handlePinLocked = () => {
      navigationLocked.current = true
      requestRef.current?.abort()
      searchAbortRef.current?.abort()
      selectAllController.current?.abort()
      activeUploadControllers.current.forEach((controller) => controller.abort())
      try {
        localStorage.removeItem(RECENT_KEY)
        localStorage.removeItem(LAST_PATH_KEY)
        localStorage.removeItem(FOLDER_HISTORY_KEY)
        for (let index = localStorage.length - 1; index >= 0; index--) {
          const key = localStorage.key(index)
          if (key?.startsWith(UPLOAD_STORAGE_PREFIX)) localStorage.removeItem(key)
        }
      } catch { /* browser storage may be unavailable */ }
      window.history.replaceState({ explorerPath: null, explorerIndex: 0 }, '', location.pathname)
      setRecent([])
      setClipboard(null)
      setEditorContent('')
      setPreviewText(null)
      setSelected([])
      setQuery('')
      setNavigationOpen(false)
      setContextMenu(null)
      setFavorites([])
      setUploadConflict(null)
      setSelectedUploadFiles([])
      setPinStatus((current) => current?.isConfigured ? { ...current, isUnlocked: false } : current)
      setPinChecked(true)
      setCurrentPath(null)
      setHistory([null])
      setHistoryIndex(0)
      setEntries([])
      setDrives([])
      setKnownFolders([])
      setQuickAccessFolders([])
      setWindowsRecentEntries([])
      setShareSettings(null)
      setPreview(null)
      mediaFolderController.current?.abort()
      setMediaQueue([])
      setEditorDocument(null)
      setProperties(null)
      setJobs([])
      setRecycleEntries([])
      setUploads([])
    }
    window.addEventListener('explorer:pin-locked', handlePinLocked)
    return () => window.removeEventListener('explorer:pin-locked', handlePinLocked)
  }, [])
  useEffect(() => {
    if (!pinChecked || !pinStatus || (pinStatus.isConfigured && !pinStatus.isUnlocked)) return
    let active = true
    explorerApi.jobs().then((items) => {
      if (!active) return
      for (const item of items) if (!jobSnapshots.current.has(item.id)) jobSnapshots.current.set(item.id, item)
      setJobs((current) => {
        const currentIds = new Set(current.map((item) => item.id))
        return [...current, ...items.filter((item) => !currentIds.has(item.id))].slice(0, 200)
      })
    }).catch(() => {})
    const events = new EventSource(explorerApi.jobsUrl())
    events.addEventListener('job', (event) => {
      try {
        const job = JSON.parse((event as MessageEvent<string>).data) as JobSnapshot
        jobSnapshots.current.set(job.id, job)
        setJobs((current) => [job, ...current.filter((item) => item.id !== job.id)].slice(0, 200))
        if (locallyStartedJobs.current.has(job.id)) showUndoToast(job)
      } catch { /* ignore malformed event payloads */ }
    })
    events.onerror = () => {
      void explorerApi.pinStatus().then((status) => {
        if (active && status.isConfigured && !status.isUnlocked) window.dispatchEvent(new Event('explorer:pin-locked'))
      }).catch(() => { /* connectivity is checked independently */ })
    }
    return () => { active = false; events.close() }
  }, [pinChecked, pinStatus, showUndoToast])
  useEffect(() => {
    if (!picker) return
    if (pickerPath === null) { setPickerFolders([]); return }
    let active = true
    setPickerLoading(true)
    explorerApi.entries({ path: pickerPath, pageSize: 100, sort: 'name' })
      .then((page) => { if (active) setPickerFolders(page.entries.filter((entry) => entry.isDirectory)) })
      .catch((cause) => { if (active) notify(cause instanceof Error ? cause.message : de.uiDerOrdnerKannNichtGeoffnetWerden) })
      .finally(() => { if (active) setPickerLoading(false) })
    return () => { active = false }
  }, [picker, pickerPath, notify])
  useEffect(() => {
    if (mainPage !== 'recycle') return
    let active = true
    setRecycleLoading(true)
    explorerApi.recycleBin().then((items) => { if (active) setRecycleEntries(items) })
      .catch((cause) => { if (active) notify(cause instanceof Error ? cause.message : de.uiDerPapierkorbKonnteNichtGeladenWerden) })
      .finally(() => { if (active) setRecycleLoading(false) })
    return () => { active = false }
  }, [mainPage, notify])
  useEffect(() => {
    if (mainPage !== 'transfers') return
    let active = true
    explorerApi.uploads().then((items) => { if (active) setUploads(items) })
      .catch((cause) => { if (active) notify(cause instanceof Error ? cause.message : de.uiUploadSitzungenKonntenNichtGeladenWerden) })
    return () => { active = false }
  }, [mainPage, notify])
  useEffect(() => {
    if (!properties) { setPropertyDetails(null); setPropertiesError(''); return }
    let active = true
    setPropertiesLoading(true)
    setPropertiesError('')
    explorerApi.properties(properties.path)
      .then((details) => { if (active) setPropertyDetails(details) })
      .catch((cause) => { if (active) setPropertiesError(cause instanceof Error ? cause.message : de.uiEigenschaftenKonntenNichtGeladenWerden) })
      .finally(() => { if (active) setPropertiesLoading(false) })
    return () => { active = false }
  }, [properties])
  useEffect(() => {
    document.documentElement.dataset.theme = theme
    localStorage.setItem('windows-mobile-explorer:theme', theme)
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', theme === 'dark' ? '#080d16' : '#eef2f6')
  }, [theme])
  useEffect(() => {
    const updateTextScale = () => setAppTextScale(textSize)
    const updateWhenVisible = () => {
      if (document.visibilityState === 'visible') updateTextScale()
    }
    updateTextScale()
    window.addEventListener('pageshow', updateTextScale)
    window.addEventListener('resize', updateTextScale)
    document.addEventListener('visibilitychange', updateWhenVisible)
    localStorage.setItem(TEXT_SIZE_KEY, String(textSize))
    return () => {
      window.removeEventListener('pageshow', updateTextScale)
      window.removeEventListener('resize', updateTextScale)
      document.removeEventListener('visibilitychange', updateWhenVisible)
    }
  }, [textSize])
  useEffect(() => {
    const onResize = () => setColumnCount(window.innerWidth < 390 ? 2 : window.innerWidth < 700 ? 3 : window.innerWidth < 1100 ? 4 : 6)
    window.addEventListener('resize', onResize)
    return () => window.removeEventListener('resize', onResize)
  }, [])
  useEffect(() => {
    if (pinChecked && pinStatus && (!pinStatus.isConfigured || pinStatus.isUnlocked)) navigationLocked.current = false
  }, [pinChecked, pinStatus])
  useEffect(() => {
    const onPop = () => {
      if (navigationLocked.current) return
      const state = window.history.state as { explorerPath?: string | null } | null
      if (state && 'explorerPath' in state) {
        const path = state.explorerPath ?? null
        const requestedIndex = (state as { explorerIndex?: unknown }).explorerIndex
        let index = Number.isInteger(requestedIndex) ? requestedIndex as number : history.lastIndexOf(path)
        if (index < 0 || index >= history.length || history[index] !== path) {
          const nextHistory = [...history.slice(0, historyIndex + 1), path]
          index = nextHistory.length - 1
          if (nextHistory.length > MAX_FOLDER_HISTORY) {
            nextHistory.splice(1, nextHistory.length - MAX_FOLDER_HISTORY)
            index = nextHistory.length - 1
          }
          setHistory(nextHistory)
          saveFolderHistory(nextHistory, index)
          window.history.replaceState({ explorerPath: path, explorerIndex: index }, '', location.pathname)
        } else saveFolderHistory(history, index)
        setHistoryIndex(index)
        setCurrentPath(path)
        saveLastPath(path)
        setMainPage('files')
      }
    }
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [history, historyIndex])

  useEffect(() => {
    const breadcrumb = breadcrumbRef.current
    if (breadcrumb) breadcrumb.scrollLeft = breadcrumb.scrollWidth
  }, [currentPath])

  const navigate = useCallback((path: string | null, replace = false) => {
    searchAbortRef.current?.abort()
    selectAllController.current?.abort()
    setDeepSearchActive(false)
    setDeepSearching(false)
    setMainPage('files')
    setSelected([])
    setSelectionMode(false)
    setQuery('')
    setFilter('all')
    setCurrentPath(path)
    saveLastPath(path)
    let next = replace ? [...history.slice(0, historyIndex), path] : [...history.slice(0, historyIndex + 1), path]
    if (next.length > MAX_FOLDER_HISTORY) {
      next = [null, ...next.slice(next.length - (MAX_FOLDER_HISTORY - 1))]
    }
    const nextIndex = next.length - 1
    setHistory(next)
    setHistoryIndex(nextIndex)
    saveFolderHistory(next, nextIndex)
    if (replace) window.history.replaceState({ explorerPath: path, explorerIndex: nextIndex }, '', location.pathname)
    else window.history.pushState({ explorerPath: path, explorerIndex: nextIndex }, '', location.pathname)
    if (contentRef.current) contentRef.current.scrollTop = 0
  }, [history, historyIndex])

  const goBack = () => {
    if (historyIndex <= 0) return
    const index = historyIndex - 1
    setHistoryIndex(index)
    setCurrentPath(history[index])
    saveLastPath(history[index])
    saveFolderHistory(history, index)
    setMainPage('files')
    window.history.back()
  }
  const goForward = () => {
    if (historyIndex >= history.length - 1) return
    const index = historyIndex + 1
    setHistoryIndex(index)
    setCurrentPath(history[index])
    saveLastPath(history[index])
    saveFolderHistory(history, index)
    setMainPage('files')
    window.history.forward()
  }

  const loadFirstPage = useCallback(async () => {
    if (!currentPath) return
    requestRef.current?.abort()
    const controller = new AbortController()
    requestRef.current = controller
    const viewKey = JSON.stringify([currentPath, sort, descending, query, showHidden, showSystem, extensionFilter, minSizeFilter, maxSizeFilter, modifiedAfterFilter, modifiedBeforeFilter, filter])
    const preserveList = loadedViewRef.current === viewKey && entriesRef.current.length > 0
    const targetCount = preserveList ? entriesRef.current.length : PAGE_SIZE
    if (!preserveList) {
      loadedViewRef.current = null
      setEntries([])
      setHasMore(false)
      setTotalCount(null)
    }
    setError('')
    setLoading(true)
    try {
      const updated: FileEntry[] = []
      let page: FolderPage
      do {
        page = await explorerApi.entries({
          path: currentPath, skip: updated.length, pageSize: preserveList ? Math.min(500, targetCount - updated.length) : PAGE_SIZE, sort, descending, query, showHidden, showSystem,
          extension: extensionFilter, minSize: sizeMegabytesToBytes(minSizeFilter), maxSize: sizeMegabytesToBytes(maxSizeFilter),
          modifiedAfter: localDateBound(modifiedAfterFilter), modifiedBefore: localDateBound(modifiedBeforeFilter, true),
          refresh: updated.length === 0, kind: filter, signal: controller.signal,
        })
        updated.push(...page.entries)
      } while (page.hasMore && page.entries.length > 0 && updated.length < targetCount)
      if (controller.signal.aborted) return
      if (page.path !== currentPath) {
        setCurrentPath(page.path)
        setHistory((current) => {
          const next = current.map((path, index) => index === historyIndex ? page.path : path)
          saveFolderHistory(next, historyIndex)
          return next
        })
        saveLastPath(page.path)
        const browserState = window.history.state as { explorerPath?: string | null } | null
        if (browserState && 'explorerPath' in browserState)
          window.history.replaceState({ ...browserState, explorerPath: page.path, explorerIndex: historyIndex }, '', location.pathname)
      }
      loadedViewRef.current = viewKey
      setEntries(updated)
      setHasMore(page.hasMore)
      setTotalCount(page.totalCount)
      setConnection('online')
    } catch (cause) {
      if (controller.signal.aborted) return
      setConnection('offline')
      setError(cause instanceof Error ? cause.message : de.uiDerOrdnerKannNichtGeoffnetWerden)
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }, [currentPath, historyIndex, sort, descending, query, showHidden, showSystem, extensionFilter, minSizeFilter, maxSizeFilter, modifiedAfterFilter, modifiedBeforeFilter, filter])

  useEffect(() => {
    void loadFirstPage()
    return () => requestRef.current?.abort()
  }, [loadFirstPage, refreshKey])

  const fetchNextPage = useCallback(async () => {
    if (!currentPath || !hasMore || pageLoading || loading || deepSearchActive) return
    const controller = requestRef.current
    if (!controller || controller.signal.aborted) return
    setPageLoading(true)
    try {
      const page: FolderPage = await explorerApi.entries({
        path: currentPath, skip: entries.length, pageSize: PAGE_SIZE, sort, descending, query, showHidden, showSystem,
        extension: extensionFilter, minSize: sizeMegabytesToBytes(minSizeFilter), maxSize: sizeMegabytesToBytes(maxSizeFilter),
        modifiedAfter: localDateBound(modifiedAfterFilter), modifiedBefore: localDateBound(modifiedBeforeFilter, true),
        kind: filter, signal: controller.signal,
      })
      if (controller.signal.aborted || requestRef.current !== controller) return
      setEntries((current) => [...current, ...page.entries])
      setHasMore(page.hasMore)
      setTotalCount(page.totalCount)
    } catch (cause) {
      if (!controller.signal.aborted) notify(cause instanceof Error ? cause.message : de.uiWeitereEintrageKonntenNichtGeladenWerden)
    } finally { setPageLoading(false) }
  }, [currentPath, hasMore, pageLoading, loading, deepSearchActive, entries.length, sort, descending, query, showHidden, showSystem,
    extensionFilter, minSizeFilter, maxSizeFilter, modifiedAfterFilter, modifiedBeforeFilter, filter, notify])

  const startRecursiveSearch = async () => {
    if (!currentPath || !query.trim()) { notify(de.uiGibZuerstEinenSuchbegriffEin); return }
    searchAbortRef.current?.abort()
    const controller = new AbortController()
    searchAbortRef.current = controller
    setDeepSearchActive(true)
    setDeepSearching(true)
    setEntries([])
    setHasMore(false)
    setLoading(false)
    setError('')
    let count = 0
    try {
      const response = await fetch(explorerApi.searchUrl({
        root: currentPath, query, showHidden, showSystem, content: searchContent, extension: extensionFilter,
        minSize: sizeMegabytesToBytes(minSizeFilter), maxSize: sizeMegabytesToBytes(maxSizeFilter),
        modifiedAfter: localDateBound(modifiedAfterFilter), modifiedBefore: localDateBound(modifiedBeforeFilter, true),
      }), { signal: controller.signal, credentials: 'same-origin' })
      if (!response.ok || !response.body) throw new Error(response.ok ? de.uiDieSucheKonnteNichtGestartetWerden : de.searchError(response.status))
      const reader = response.body.getReader()
      const decoder = new TextDecoder()
      let buffer = ''
      let batch: FileEntry[] = []
      while (true) {
        const { value, done } = await reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        const blocks = buffer.split('\n\n')
        buffer = blocks.pop() ?? ''
        for (const block of blocks) {
          const event = block.split('\n').find((line) => line.startsWith('event:'))?.slice(6).trim()
          const data = block.split('\n').find((line) => line.startsWith('data:'))?.slice(5).trim()
          if (event === 'result' && data) { batch.push(JSON.parse(data) as FileEntry); count += 1 }
        }
        if (batch.length) { setEntries((current) => [...current, ...batch]); batch = [] }
      }
      setTotalCount(count)
      setConnection('online')
    } catch (cause) {
      if (!controller.signal.aborted) notify(cause instanceof Error ? cause.message : de.uiDieSucheIstFehlgeschlagen)
    } finally { if (!controller.signal.aborted) setDeepSearching(false) }
  }

  useEffect(() => {
    if (!currentPath || mainPage !== 'files') return
    const events = new EventSource(explorerApi.watchUrl(currentPath))
    let refreshTimer = 0
    let connectionCheckTimer = 0
    let connectionProbe: AbortController | null = null
    let connectionProbeVersion = 0
    let disposed = false
    const refresh = () => {
      window.clearTimeout(refreshTimer)
      refreshTimer = window.setTimeout(() => { setDeepSearchActive(false); searchAbortRef.current?.abort(); setRefreshKey((value) => value + 1) }, 1200)
    }
    const verifyConnection = () => {
      if (disposed) return
      window.clearTimeout(connectionCheckTimer)
      connectionProbe?.abort()
      const version = ++connectionProbeVersion
      connectionCheckTimer = window.setTimeout(() => {
        const probe = new AbortController()
        connectionProbe = probe
        const timeout = window.setTimeout(() => probe.abort(), 5_000)
        void explorerApi.health(probe.signal).then(
          () => { if (!disposed && version === connectionProbeVersion) setConnection('online') },
          () => { if (!disposed && version === connectionProbeVersion) setConnection('offline') },
        ).finally(() => {
          window.clearTimeout(timeout)
          if (connectionProbe === probe) connectionProbe = null
        })
      }, 500)
    }
    events.addEventListener('change', refresh)
    events.onerror = verifyConnection
    events.onopen = () => {
      if (disposed) return
      connectionProbeVersion++
      window.clearTimeout(connectionCheckTimer)
      connectionProbe?.abort()
      connectionProbe = null
      setConnection('online')
    }
    return () => {
      disposed = true
      connectionProbeVersion++
      window.clearTimeout(refreshTimer)
      window.clearTimeout(connectionCheckTimer)
      connectionProbe?.abort()
      events.close()
    }
  }, [currentPath, mainPage])

  const filteredEntries = useMemo(() => {
    if (filter === 'all') return entries
    if (filter === 'folders') return entries.filter((entry) => entry.isDirectory)
    const kinds: Record<string, string[]> = {
      images: ['image'], videos: ['video'], audio: ['audio'], documents: ['document'], archives: ['archive'], code: ['code'],
    }
    return entries.filter((entry) => !entry.isDirectory && kinds[filter]?.includes(fileKind(entry)))
  }, [entries, filter])

  const displayRows = useMemo(() => {
    const groups: Array<{ label: string; rank: number; entries: FileEntry[] }> = []
    if (groupBy === 'none') groups.push({ label: '', rank: 0, entries: filteredEntries })
    else {
      const byLabel = new Map<string, { label: string; rank: number; entries: FileEntry[] }>()
      for (const entry of filteredEntries) {
        const group = getGroup(entry, groupBy)
        const existing = byLabel.get(group.label)
        if (existing) existing.entries.push(entry)
        else byLabel.set(group.label, { ...group, entries: [entry] })
      }
      groups.push(...byLabel.values())
      groups.sort((a, b) => groupBy === 'type'
        ? a.label.localeCompare(b.label, de.collatorLocale, { numeric: true, sensitivity: 'base' })
        : a.rank - b.rank)
    }

    const rows: VirtualRow[] = []
    for (const group of groups) {
      if (groupBy !== 'none') rows.push({ type: 'group', key: `group-${group.label}`, label: group.label })
      const ordered = groupBy === 'none' ? group.entries : [...group.entries].sort((a, b) =>
        Number(b.isDirectory) - Number(a.isDirectory) || compareEntries(a, b, sort, descending))
      if (view === 'grid') {
        for (let index = 0; index < ordered.length; index += columnCount)
          rows.push({ type: 'entries', key: `row-${group.label}-${index}`, entries: ordered.slice(index, index + columnCount) })
      } else {
        for (const entry of ordered) rows.push({ type: 'entry', key: entry.path, entry })
      }
    }
    return rows
  }, [filteredEntries, groupBy, view, columnCount, sort, descending])

  const virtualizer = useVirtualizer({
    count: displayRows.length + (hasMore ? 1 : 0),
    getScrollElement: () => contentRef.current,
    estimateSize: (index) => displayRows[index]?.type === 'group' ? 28 : view === 'grid' ? 132 : 48,
    overscan: 8,
  })
  const virtualItems = virtualizer.getVirtualItems()
  useEffect(() => {
    const last = virtualItems.at(-1)
    if (last && last.index >= displayRows.length - 3 && hasMore) void fetchNextPage()
  }, [virtualItems, displayRows.length, hasMore, fetchNextPage])

  const remember = (entry: FileEntry) => {
    const next = [entry, ...recent.filter((item) => item.path !== entry.path)].slice(0, 40)
    setRecent(next)
    localStorage.setItem(RECENT_KEY, JSON.stringify(next))
  }

  const uniqueMedia = (items: FileEntry[]) => {
    const seen = new Set<string>()
    return items.filter((item) => {
      if (!isPlayableMedia(item)) return false
      const key = item.path.replaceAll('/', '\\').toLocaleLowerCase()
      if (seen.has(key)) return false
      seen.add(key)
      return true
    })
  }

  const playMedia = (entry: FileEntry, playlist?: FileEntry[]) => {
    setPreview(null)
    setMediaQueue((current) => uniqueMedia(playlist ?? [...current, entry]))
    setMediaStartPath(entry.path)
    setMediaSessionId((value) => value + 1)
    remember(entry)
  }

  const enqueueMedia = (items: FileEntry[]) => {
    const playable = uniqueMedia(items)
    if (playable.length === 0) { notify(de.mediaNoFiles); return }
    const currentQueue = mediaQueueRef.current
    const merged = uniqueMedia([...currentQueue, ...playable])
    const added = merged.length - currentQueue.length
    mediaQueueRef.current = merged
    setMediaQueue(merged)
    if (currentQueue.length === 0) {
      setMediaStartPath(playable[0].path)
      setMediaSessionId((value) => value + 1)
      remember(playable[0])
    }
    notify(added > 0 ? de.mediaQueued(added) : de.mediaAlreadyQueued)
  }

  const queueFolder = async (path: string, playNow = false) => {
    mediaFolderController.current?.abort()
    const controller = new AbortController()
    mediaFolderController.current = controller
    setMediaFolderLoading(true)
    const items: FileEntry[] = []
    try {
      let skip = 0
      while (!controller.signal.aborted) {
        const page = await explorerApi.entries({ path, skip, pageSize: 500, sort, descending, showHidden, showSystem, signal: controller.signal })
        items.push(...page.entries.filter(isPlayableMedia))
        if (!page.hasMore || page.entries.length === 0) break
        skip += page.entries.length
      }
      if (controller.signal.aborted) return
      if (items.length === 0) { notify(de.mediaNoFiles); return }
      if (playNow) playMedia(items[0], items)
      else enqueueMedia(items)
    } catch (cause) {
      if (!controller.signal.aborted) notify(cause instanceof Error ? cause.message : de.mediaFolderLoadFailed)
    } finally {
      if (mediaFolderController.current === controller) {
        mediaFolderController.current = null
        setMediaFolderLoading(false)
      }
    }
  }

  const openEntry = async (entry: FileEntry, trigger?: HTMLElement) => {
    if (selectionMode) { toggleSelected(entry.path); return }
    let openedEntry = entry
    if (entry.isShortcut) {
      try { openedEntry = await explorerApi.resolveShortcut(entry.path) }
      catch (cause) { notify(cause instanceof Error ? cause.message : de.uiDieVerknupfungKonnteNichtGeoffnetWerden); return }
    }
    if (openedEntry.isDirectory) navigate(openedEntry.path)
    else if (isPlayableMedia(openedEntry)) playMedia(openedEntry)
    else {
      previewReturnFocusRef.current = trigger ?? (document.activeElement instanceof HTMLElement ? document.activeElement : null)
      remember(openedEntry)
      setPreview(openedEntry)
      setPreviewText(null)
      setPreviewError('')
      setPreviewZoom(1)
    }
  }

  const toggleSelected = (path: string) => setSelected((current) => current.includes(path) ? current.filter((item) => item !== path) : [...current, path])

  const selectAllEntries = async () => {
    if (!currentPath || selectAllController.current || deepSearching) return

    if (deepSearchActive) {
      const paths = filteredEntries.map((entry) => entry.path)
      setSelected(paths)
      setSelectionMode(true)
      notify(de.uiElementsSelected(paths.length))
      return
    }

    const controller = new AbortController()
    selectAllController.current = controller
    setSelected([])
    setSelectAllProgress({ loaded: 0, total: totalCount ?? 0 })
    let skip = 0
    let expectedTotal: number | null = null
    const paths: string[] = []
    const seen = new Set<string>()
    try {
      while (!controller.signal.aborted) {
        const page = await explorerApi.entries({
          path: currentPath, skip, pageSize: 500, sort, descending, query, showHidden, showSystem,
          extension: extensionFilter, minSize: sizeMegabytesToBytes(minSizeFilter), maxSize: sizeMegabytesToBytes(maxSizeFilter),
          modifiedAfter: localDateBound(modifiedAfterFilter), modifiedBefore: localDateBound(modifiedBeforeFilter, true),
          kind: filter, signal: controller.signal,
        })
        if (expectedTotal !== null && page.totalCount !== expectedTotal)
          throw new Error(de.uiFolderChangedDuringSelection)
        expectedTotal = page.totalCount
        for (const entry of page.entries) {
          if (seen.has(entry.path)) throw new Error(de.uiFolderChangedDuringSelection)
          seen.add(entry.path)
          paths.push(entry.path)
        }
        skip += page.entries.length
        setSelectAllProgress({ loaded: paths.length, total: expectedTotal ?? paths.length })
        if (!page.hasMore || page.entries.length === 0) break
      }
      if (controller.signal.aborted) return
      if (expectedTotal !== null && paths.length !== expectedTotal) throw new Error(de.uiFolderChangedDuringSelection)
      setSelected(paths)
      setSelectionMode(true)
      notify(de.uiElementsSelected(paths.length))
    } catch (cause) {
      if (!controller.signal.aborted) notify(cause instanceof Error ? cause.message : de.uiAllEntriesCouldNotBeSelected)
    } finally {
      if (selectAllController.current === controller) selectAllController.current = null
      setSelectAllProgress(null)
    }
  }

  useEffect(() => {
    selectAllController.current?.abort()
    setSelectAllProgress(null)
    setSelected([])
    setSelectionMode(false)
  }, [currentPath, mainPage, query, showHidden, showSystem, extensionFilter, minSizeFilter, maxSizeFilter, modifiedAfterFilter, modifiedBeforeFilter, filter, deepSearchActive])

  useEffect(() => {
    selectAllController.current?.abort()
    setSelectAllProgress(null)
  }, [sort, descending, refreshKey])

  const openPreview = useCallback(async (entry: FileEntry) => {
    const kind = fileKind(entry)
    if (kind !== 'code') return
    try {
      const response = await fetch(explorerApi.streamUrl(entry), { credentials: 'same-origin' })
      if (!response.ok) throw new Error(de.uiDieDateiKannNichtGelesenWerden)
      const text = await response.text()
      if (text.length > 2_000_000) {
        setPreviewError(de.uiDieDateiIstGrosserAls2MBLadeSieHerunterUmDenVollstandigenInhaltZuOffnen)
        setPreviewText(text.slice(0, 2_000_000))
      } else setPreviewText(text)
    } catch (cause) { setPreviewError(cause instanceof Error ? cause.message : de.uiVorschauNichtVerfugbar) }
  }, [])

  useEffect(() => { if (preview) void openPreview(preview) }, [preview, openPreview])

  const openEditor = async (entry: FileEntry) => {
    setEditorLoading(true)
    setEditorError('')
    try {
      const document = await explorerApi.readText(entry.path)
      setEditorDocument(document)
      setEditorContent(document.content)
      setPreview(null)
    } catch (cause) { setEditorError(cause instanceof Error ? cause.message : de.uiDieDateiKannNichtBearbeitetWerden) }
    finally { setEditorLoading(false) }
  }

  const saveEditor = async () => {
    if (!editorDocument) return
    setEditorSaving(true)
    setEditorError('')
    try {
      const saved = await explorerApi.saveText({ path: editorDocument.path, content: editorContent, eTag: editorDocument.eTag })
      setEditorDocument(saved)
      setEditorContent(saved.content)
      setRefreshKey((value) => value + 1)
      notify(de.uiDateiGespeichert)
    } catch (cause) { setEditorError(cause instanceof Error ? cause.message : de.uiDieDateiKonnteNichtGespeichertWerden) }
    finally { setEditorSaving(false) }
  }

  const refresh = async () => {
    setRefreshing(true)
    if (currentPath && sort === 'folder-size') setFolderSizeRefreshKey((value) => value + 1)
    if (!currentPath) await loadHome()
    else { setDeepSearchActive(false); searchAbortRef.current?.abort(); setRefreshKey((value) => value + 1) }
    window.setTimeout(() => setRefreshing(false), 550)
  }

  const openSettings = async () => {
    setSettingsOpen(true)
    setShareSettingsLoading(true)
    setShareSettingsError('')
    try { setShareSettings(await explorerApi.shareSettings()) }
    catch (cause) { setShareSettingsError(cause instanceof Error ? cause.message : de.uiFreigabeEinstellungenLadenFehlgeschlagen) }
    finally { setShareSettingsLoading(false) }
  }

  const saveShareSettings = async (next: ExplorerShareSettings) => {
    setShareSettingsSaving(true)
    setShareSettingsError('')
    try {
      const saved = await explorerApi.updateShareSettings(next)
      setShareSettings(saved)
      setHealth((current) => current ? { ...current, readOnly: saved.readOnlyMode, shareAllDrives: saved.shareAllDrives } : current)
      const [driveList, folders, quickAccessList] = await Promise.all([explorerApi.drives(), explorerApi.knownFolders(), explorerApi.quickAccess()])
      setDrives(driveList)
      setKnownFolders(folders)
      setQuickAccessFolders(quickAccessList)
      if (currentPath) {
        try { await explorerApi.entries({ path: currentPath, pageSize: 1 }); setRefreshKey((value) => value + 1) }
        catch {
          setCurrentPath(null)
          saveLastPath(null)
          setHistory([null])
          setHistoryIndex(0)
          saveFolderHistory([null], 0)
          window.history.replaceState({ explorerPath: null, explorerIndex: 0 }, '', location.pathname)
        }
      }
      notify(de.uiFreigabeEinstellungenGespeichert)
    } catch (cause) {
      setShareSettingsError(cause instanceof Error ? cause.message : de.uiFreigabeEinstellungenSpeichernFehlgeschlagen)
    } finally { setShareSettingsSaving(false) }
  }

  const startPathDialog = () => {
    setPathInput(currentPath ?? '')
    setPathModalError('')
    setPathOpen(true)
  }
  const submitPath = (event: React.FormEvent) => {
    event.preventDefault()
    const target = pathInput.trim()
    if (!target) { setPathModalError(de.pathRequired); return }
    setPathOpen(false)
    navigate(target)
  }

  const toggleFavorite = async (entry: FileEntry) => {
    if (!entry.isDirectory) return
    const isFavorite = favorites.some((path) => sameWindowsPath(path, entry.path))
    const next = isFavorite ? favorites.filter((path) => !sameWindowsPath(path, entry.path)) : [entry.path, ...favorites]
    try {
      setFavorites(await explorerApi.updateFavorites(next))
      notify(isFavorite ? de.uiAusFavoritenEntfernt : de.uiZuFavoritenHinzugefugt)
    } catch (cause) {
      try { setFavorites(await explorerApi.favorites()) } catch { /* keep the last confirmed list */ }
      notify(cause instanceof Error ? cause.message : de.uiFavoritenKonntenNichtGespeichertWerden)
    }
  }

  const copyPath = async (path: string) => {
    try { await navigator.clipboard.writeText(path); notify(de.uiPfadKopiert) }
    catch { notify(de.uiDerPfadKonnteNichtKopiertWerden) }
  }

  const sharePreparedFile = (file: NonNullable<ShareData['files']>[number], entry: FileEntry, canRetry = true) => {
    const handleError = (cause: unknown) => {
      const name = typeof cause === 'object' && cause !== null && 'name' in cause ? (cause as { name?: unknown }).name : undefined
      if (name === 'AbortError') return
      if (name === 'NotAllowedError') {
        if (canRetry) {
          notify(de.uiDieDateiIstBereitTippeAufTeilenUmDasIOSMenuZuOffnen, { label: de.share, run: () => sharePreparedFile(file, entry, false) })
        } else {
          downloadEntry(entry, de.uiDasTeilenMenuIstNichtVerfugbarTeileDieHeruntergeladeneDateiUberDieDateienApp)
        }
        return
      }
      notify(cause instanceof Error ? cause.message : de.uiDieDateiKonnteNichtGeteiltWerden)
    }
    try { void navigator.share({ files: [file], title: entry.name }).catch(handleError) }
    catch (cause) { handleError(cause) }
  }

  const shareEntry = async (entry: FileEntry) => {
    if (entry.isDirectory || !entry.size || entry.size > 32 * 1024 * 1024 || typeof navigator.canShare !== 'function' || typeof navigator.share !== 'function') {
      downloadEntry(entry, entry.isDirectory
        ? de.uiZIPDownloadGestartetTeileDasArchivAnschliessendUberDieDateienApp
        : de.uiDownloadGestartetTeileDieDateiAnschliessendUberDieDateienApp)
      return
    }
    try {
      const response = await fetch(explorerApi.streamUrl(entry, true), { credentials: 'same-origin' })
      if (!response.ok) throw new Error(de.uiDieDateiKonnteNichtFurDasTeilenGeladenWerden)
      const blob = await response.blob()
      const file = new globalThis.File([blob], entry.name, { type: blob.type || entry.contentType || 'application/octet-stream' })
      if (!navigator.canShare({ files: [file] })) {
        downloadEntry(entry, de.uiDownloadGestartetTeileDieDateiAnschliessendUberDieDateienApp)
        return
      }
      sharePreparedFile(file, entry)
    } catch (cause) {
      notify(cause instanceof Error ? cause.message : de.uiDieDateiKonnteNichtGeteiltWerden)
    }
  }

  const downloadEntry = (entry: FileEntry, message?: string) => {
    const anchor = document.createElement('a')
    anchor.href = entry.isDirectory ? explorerApi.archiveUrl(entry.path) : explorerApi.streamUrl(entry, true)
    anchor.rel = 'noopener'
    anchor.click()
    notify(message ?? (entry.isDirectory ? de.uiZIPDownloadGestartet : de.uiDownloadGestartet))
  }

  const startJob = async (request: { kind: string; sources: string[]; destination?: string; newName?: string; template?: string; permanent?: boolean }) => {
    try {
      const job = await explorerApi.enqueue(request)
      locallyStartedJobs.current.add(job.id)
      const known = jobSnapshots.current.get(job.id)
      const latest = known && ['completed', 'completed-with-skips', 'failed', 'cancelled'].includes(known.status)
        ? known
        : job
      jobSnapshots.current.set(job.id, latest)
      setJobs((current) => [latest, ...current.filter((item) => item.id !== latest.id)].slice(0, 200))
      if (['completed', 'completed-with-skips'].includes(latest.status)) showUndoToast(latest)
      else if (['failed', 'cancelled'].includes(latest.status)) notify(latest.message)
      else notify(de.uiVorgangGestartetDerPCArbeitetWeiterAuchWennDuDieAppSchliesst)
      return job
    } catch (cause) {
      notify(cause instanceof Error ? cause.message : de.uiDerVorgangKonnteNichtGestartetWerden)
      return null
    }
  }

  const openFolderPicker = (kind: 'move' | 'zip' | 'unzip' | 'upload', sources: string[]) => {
    setPicker({ kind, sources })
    setPickerPath(currentPath)
  }

  const runContextAction = async (action: string, entry: FileEntry) => {
    const paths = selected.includes(entry.path) ? selected : [entry.path]
    setContextMenu(null)
    switch (action) {
      case 'open': openEntry(entry); return
      case 'enqueue': {
        const chosen = paths.length > 1 ? currentFolderEntries.filter((item) => paths.includes(item.path)) : [entry]
        enqueueMedia(chosen)
        return
      }
      case 'queue-folder': await queueFolder(entry.path); return
      case 'play-folder': await queueFolder(entry.path, true); return
      case 'download': downloadEntry(entry); return
      case 'share': await shareEntry(entry); return
      case 'favorite': toggleFavorite(entry); return
      case 'copy-path': await copyPath(entry.path); return
      case 'properties': setProperties(entry); return
      case 'edit': await openEditor(entry); return
      case 'copy': setClipboard({ paths, mode: 'copy' }); notify(de.copyQueued(paths.length)); return
      case 'cut': setClipboard({ paths, mode: 'move' }); notify(de.moveQueued(paths.length)); return
      case 'paste':
        if (clipboard && currentPath) {
          const job = await startJob({ kind: clipboard.mode, sources: clipboard.paths, destination: currentPath })
          if (job && clipboard.mode === 'move') setClipboard(null)
          setSelected([])
        }
        return
      case 'move': openFolderPicker('move', paths); return
      case 'zip': openFolderPicker('zip', paths.slice(0, 1)); return
      case 'unzip': openFolderPicker('unzip', paths.slice(0, 1)); return
      case 'rename': {
        const name = window.prompt(de.uiNeuerNameBeiSerienNameExtN, entry.name)
        if (name?.trim()) { await startJob({ kind: paths.length > 1 ? 'rename-series' : 'rename', sources: paths, newName: name.trim() }); setSelected([]) }
        return
      }
      case 'delete':
        if (await startJob({ kind: 'delete', sources: paths })) setSelected([])
        return
      case 'permanent-delete':
        if (window.confirm(de.confirmDeleteMany(paths.length))) {
          if (await startJob({ kind: 'delete', sources: paths, permanent: true })) setSelected([])
        }
        return
      case 'new-file': {
        if (!currentPath) return
        setNewFileTemplate('text')
        setNewFileName(de.uiNeueDateiTxt)
        setNewFileDialogOpen(true)
        return
      }
      case 'new-folder': {
        if (!currentPath) return
        const name = window.prompt(de.uiNameDesNeuenOrdners, '')
        if (name?.trim()) await startJob({ kind: action, sources: [], destination: currentPath, newName: name.trim() })
        return
      }
      case 'show-in-explorer':
        try { await explorerApi.openOnPc(entry.path, true); notify(de.uiExplorerWurdeAmSpeicherortGeoffnet) }
        catch (cause) { notify(cause instanceof Error ? cause.message : de.uiDieDateiKannAmPCNichtGeoffnetWerden) }
        return
      case 'select': setSelected(paths); setSelectionMode(true); return
    }
  }

  const confirmFolderPick = async () => {
    if (!picker || !pickerPath) return
    const { kind, sources } = picker
    setPicker(null)
    if (kind === 'move') {
      await startJob({ kind: 'move', sources, destination: pickerPath })
      setSelected([])
    } else if (kind === 'zip') {
      await startJob({ kind: 'zip', sources, destination: pickerPath })
    } else if (kind === 'unzip') {
      await startJob({ kind: 'unzip', sources, destination: pickerPath })
    } else {
      const selectedFiles = selectedUploadFiles
      setSelectedUploadFiles([])
      for (const file of selectedFiles) await startUploadFile(file, pickerPath)
    }
  }

  const startUploadFile = async (file: File, destination: string, resumeId?: string) => {
    const taskToken = resumeId ?? `pending-${Date.now()}-${Math.random().toString(16).slice(2)}`
    const controller = new AbortController()
    activeUploadControllers.current.set(taskToken, controller)
    setUploadingIds((current) => [...current, taskToken])
    let session: UploadSession | null = null
    try {
      session = await explorerApi.createUpload({ fileName: file.name, size: file.size, destination, resumeId }, controller.signal)
      activeUploadControllers.current.delete(taskToken)
      activeUploadControllers.current.set(session.id, controller)
      setUploadingIds((current) => [...current.filter((id) => id !== taskToken), session!.id])
      setUploads((current) => [session!, ...current.filter((item) => item.id !== session!.id)])
      try { localStorage.setItem(uploadStorageKey(destination, file), session.id) } catch { /* Server-side sessions remain available from the transfers page. */ }
      let offset = session.offset
      const chunkSize = 8 * 1024 * 1024
      const { sha256 } = await import('@noble/hashes/sha2.js')
      let fileHash = sha256.create()
      let hashedBytes = 0
      const catchUpHash = async (end: number) => {
        if (end < hashedBytes) {
          fileHash.destroy()
          fileHash = sha256.create()
          hashedBytes = 0
        }
        while (hashedBytes < end) {
          controller.signal.throwIfAborted()
          const next = Math.min(end, hashedBytes + chunkSize)
          fileHash.update(new Uint8Array(await file.slice(hashedBytes, next).arrayBuffer()))
          hashedBytes = next
        }
      }
      while (offset < file.size) {
        await catchUpHash(offset)
        const chunk = file.slice(offset, Math.min(file.size, offset + chunkSize))
        const bytes = new Uint8Array(await chunk.arrayBuffer())
        const digest = await crypto.subtle.digest(de.uiSHA256, bytes)
        const checksum = Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, '0')).join('')
        let result: Awaited<ReturnType<typeof explorerApi.uploadChunk>>
        try {
          result = await explorerApi.uploadChunk(session.id, offset, chunk, checksum, controller.signal)
        } catch (cause) {
          if (!isTransientUploadFailure(cause)) throw cause
          setReconnectingUploadIds((current) => current.includes(session!.id) ? current : [...current, session!.id])
          let delay = 500
          while (true) {
            await waitForUploadRetry(delay, controller.signal)
            try {
              // Reconcile with the server first: the last chunk may have been saved even if its reply was lost.
              session = await explorerApi.createUpload({ fileName: file.name, size: file.size, destination, resumeId: session!.id }, controller.signal)
              offset = session.offset
              setUploads((current) => [session!, ...current.filter((item) => item.id !== session!.id)])
              break
            } catch (reconnectError) {
              if (!isTransientUploadFailure(reconnectError)) throw reconnectError
              delay = Math.min(delay * 2, 15_000)
            }
          }
          setReconnectingUploadIds((current) => current.filter((id) => id !== session!.id))
          continue
        }
        if (result.offset !== offset + bytes.byteLength) throw new Error(de.uiDerUploadWurdeUnterbrochenUndKannFortgesetztWerden)
        fileHash.update(bytes)
        offset = hashedBytes = result.offset
        session = { ...session, offset, state: result.complete ? 'ready' : 'uploading' }
        setUploads((current) => [session!, ...current.filter((item) => item.id !== session!.id)])
      }
      try {
        await catchUpHash(file.size)
        controller.signal.throwIfAborted()
        const fullChecksum = Array.from(fileHash.digest(), (byte) => byte.toString(16).padStart(2, '0')).join('')
        const committed = await explorerApi.commitUpload(session.id, undefined, fullChecksum, controller.signal)
        if (committed.completed) {
          setUploads((current) => current.filter((item) => item.id !== session!.id))
          forgetUploadId(session.id)
          setRefreshKey((value) => value + 1)
          notify(de.uploadSuccess(file.name, committed.sha256))
        } else {
          setUploads((current) => current.filter((item) => item.id !== session!.id))
          forgetUploadId(session.id)
          notify(de.uiUploadUbersprungen)
        }
      } catch (cause) {
        if (cause instanceof ApiException && cause.code === 'upload_conflict') {
          setUploadConflict(session)
          return false
        }
        throw cause
      }
      return true
    } catch (cause) {
      if (isAbortFailure(cause)) return false
      if (session) {
        try { setUploads(await explorerApi.uploads()) } catch { /* retain the last known progress */ }
        notify(cause instanceof TypeError ? de.uiVerbindungUnterbrochenDerUploadBleibtGespeichertWahleDieDateiSpaterErneutZumFortsetzen : cause instanceof Error ? cause.message : de.uiDerUploadWurdeUnterbrochenUndKannFortgesetztWerden)
      } else notify(cause instanceof Error ? cause.message : de.uiDerUploadKonnteNichtGestartetWerden)
      return false
    } finally {
      for (const id of [taskToken, session?.id]) {
        if (id && activeUploadControllers.current.get(id) === controller) activeUploadControllers.current.delete(id)
      }
      setUploadingIds((current) => current.filter((id) => id !== taskToken && id !== session?.id))
      setReconnectingUploadIds((current) => current.filter((id) => id !== taskToken && id !== session?.id))
    }
  }

  const handleUploadSelection = (event: React.ChangeEvent<HTMLInputElement>) => {
    const chosen = Array.from(event.target.files ?? [])
    event.target.value = ''
    const resumeId = resumeUploadId.current
    resumeUploadId.current = null
    if (resumeId) {
      const session = uploads.find((item) => item.id === resumeId)
      const file = chosen[0]
      if (!session || !file) return
      if (file.name !== session.fileName || file.size !== session.size) { notify(de.uiWahleDieselbeDateiMitGleichemNamenUndGleicherGrosseAus); return }
      void startUploadFile(file, session.destination, session.id)
      return
    }
    if (!chosen.length) return
    setSelectedUploadFiles(chosen)
    openFolderPicker('upload', [])
  }

  const finishUploadConflict = async (choice: 'replace' | 'skip' | 'keep-both') => {
    if (!uploadConflict) return
    try {
      const result = await explorerApi.commitUpload(uploadConflict.id, choice)
      setUploads((current) => current.filter((item) => item.id !== uploadConflict.id))
      forgetUploadId(uploadConflict.id)
      setUploadConflict(null)
      if (result.completed) {
        setRefreshKey((value) => value + 1)
        notify(de.uploadCompleted(result.sha256))
      } else notify(de.uiUploadUbersprungen)
    } catch (cause) { notify(cause instanceof Error ? cause.message : de.uiUploadKonnteNichtAbgeschlossenWerden) }
  }

  const cancelUpload = async (session: UploadSession) => {
    activeUploadControllers.current.get(session.id)?.abort()
    try {
      await explorerApi.cancelUpload(session.id)
      setUploads((current) => current.filter((item) => item.id !== session.id))
      forgetUploadId(session.id)
      notify(de.uiUnvollstandigerUploadEntfernt)
    } catch (cause) { notify(cause instanceof Error ? cause.message : de.uiUploadKonnteNichtAbgebrochenWerden) }
  }

  const showContext = (entry: FileEntry, bounds?: DOMRect) => {
    const left = Math.max(12, Math.min((bounds?.left ?? window.innerWidth / 2) + 12, window.innerWidth - 340))
    const top = Math.max(80, Math.min((bounds?.top ?? window.innerHeight / 2) + 12, window.innerHeight - 420))
    setContextMenu({ entry, left, top })
  }

  const onPullStart = (event: React.TouchEvent<HTMLDivElement>) => {
    lastTouchY.current = contentRef.current?.scrollTop === 0 && event.touches.length === 1 ? event.touches[0]?.clientY ?? null : null
  }
  const onPullEnd = (event: React.TouchEvent<HTMLDivElement>) => {
    const start = lastTouchY.current
    lastTouchY.current = null
    if (start !== null && (contentRef.current?.scrollTop ?? 1) <= 0 && (event.changedTouches[0]?.clientY ?? start) - start > 78) void refresh()
  }

  const currentLabel = currentPath ? pathParts(currentPath).at(-1)?.label ?? currentPath : de.thisPc
  const currentFolderIsFavorite = currentPath !== null && favorites.some((path) => sameWindowsPath(path, currentPath))
  const currentFolderEntries = currentPath ? filteredEntries : []
  const canGoUp = Boolean(currentPath && parentPath(currentPath))
  const openPage = (page: MainPage) => {
    setMainPage(page)
    setSelected([])
    setSelectionMode(false)
    setContextMenu(null)
    if (page === 'files') {
      if (currentPath === null) void loadHome()
      else setRefreshKey((value) => value + 1)
    }
  }
  const visibleFavorites = favorites.map((path) => ({ path, name: path.split(/[\\/]/).filter(Boolean).at(-1) ?? path }))
  const filteredDrives = drives.filter((drive) => drive.displayName.toLowerCase().includes(homeQuery.toLowerCase()) || drive.name.toLowerCase().includes(homeQuery.toLowerCase()))
  const filteredFolders = knownFolders.filter((folder) => folder.name.toLowerCase().includes(homeQuery.toLowerCase()))
  const knownRootPaths = new Set([...drives.map((drive) => drive.name), ...knownFolders.map((folder) => folder.path)].map((path) => path.replace(/[\\/]+$/, '').toLocaleLowerCase()))
  const filteredQuickAccess = quickAccessFolders.filter((folder) => {
    const normalizedPath = folder.path.replace(/[\\/]+$/, '').toLocaleLowerCase()
    return !knownRootPaths.has(normalizedPath) &&
      (folder.name.toLocaleLowerCase().includes(homeQuery.toLocaleLowerCase()) || folder.path.toLocaleLowerCase().includes(homeQuery.toLocaleLowerCase()))
  })
  const filteredSharedRoots = (shareSettings?.roots ?? []).filter((root) => {
    const key = root.path.replace(/[\\/]+$/, '').toLocaleLowerCase()
    const title = root.label || root.path.split(/[\\/]/).filter(Boolean).at(-1) || root.path
    return !knownRootPaths.has(key) && (title.toLocaleLowerCase().includes(homeQuery.toLocaleLowerCase()) || root.path.toLocaleLowerCase().includes(homeQuery.toLocaleLowerCase()))
  })

  const unlockApp = async (pin: string) => {
    await explorerApi.unlockPin(pin)
    setPinStatus({ isConfigured: true, isUnlocked: true, retryAfterSeconds: 0 })
    setPinError('')
    if (await loadHome()) await restoreLastLocation()
  }

  const lockApp = async () => {
    try {
      await explorerApi.lockPin()
      window.dispatchEvent(new Event('explorer:pin-locked'))
    } catch (cause) { notify(cause instanceof Error ? cause.message : de.uiDieAppKonnteNichtGesperrtWerden) }
  }

  if (!pinChecked || pinError || !pinStatus || (pinStatus.isConfigured && !pinStatus.isUnlocked)) {
    return <PinGate loading={!pinChecked} error={pinError} status={pinStatus} onRetry={() => void checkPinStatus()} onUnlock={unlockApp} />
  }

  const navigationProps = { currentPath, isFilesPage: mainPage === 'files', drives, knownFolders, quickAccessFolders, favorites, sharedRoots: shareSettings?.roots ?? [], onNavigate: navigate }

  return (
    <div ref={appShellRef} className={`app-shell ss-theme${mediaPlayerActive ? ' has-media-player' : ''}${selectionActive ? ' has-selection' : ''}`} onClick={() => contextMenu && setContextMenu(null)}>
      <header className={`topbar${navigationOpen ? ' navigation-open' : ''}`}>
        <button ref={navigationToggleRef} id="folder-navigation-toggle" className="icon-button navigation-toggle" aria-label={navigationOpen ? de.uiNavigationSchliessen : de.uiNavigationOffnen} aria-expanded={navigationOpen} aria-controls={navigationOpen ? 'folder-navigation-drawer' : undefined} onClick={(event) => { event.currentTarget.focus(); setNavigationOpen((open) => !open) }}>{navigationOpen ? <X /> : <Menu />}</button>
        <div className="topbar-controls" inert={navigationOpen}>
        <div className="brand-mark" aria-hidden="true"><FolderOpen size={19} strokeWidth={2.1} /></div>
        <div className="brand-copy"><span className="brand-name">{de.appName}</span></div>
        <button className="icon-button home-navigation-button" aria-label={de.thisPc} title={de.thisPc} onClick={() => navigate(null)}><HardDrive /></button>
        {mainPage === 'files' && <button className={`icon-button top-refresh${refreshing ? ' is-refreshing' : ''}`} onClick={() => void refresh()} aria-label={de.refresh} title={de.refresh}><RefreshCw /></button>}
        <span className={`connection-pill ${connection}`} aria-label={connection === 'online' ? de.uiVerbunden : de.connectionLost}>
          <span className="connection-dot" />{connection === 'online' ? de.uiVerbunden : connection === 'checking' ? de.uiVerbinde : de.uiOffline}
        </span>
        </div>
      </header>

      <div className="explorer-body">
      <aside className="folder-sidebar"><FolderNavigation {...navigationProps} /></aside>
      <div className="explorer-content" inert={navigationOpen}>

      {mainPage === 'files' && currentPath === null && (
        <main ref={contentRef} className="workspace home-scroll" onTouchStart={onPullStart} onTouchEnd={onPullEnd}>
          <section className="home-intro">
            <h1 className="home-kicker"><span className="kicker-line" />System</h1>
            <label className="search-field home-search"><Search size={18} aria-hidden="true" /><input value={homeQuery} onChange={(event) => setHomeQuery(event.target.value)} placeholder={de.searchHome} aria-label={de.searchHome} />{homeQuery && <button className="clear-search" aria-label={de.uiSucheLoschen} onClick={() => setHomeQuery('')}><X size={16} /></button>}<kbd>{de.shortcutHomeSearch}</kbd></label>
          </section>

          {error && <ErrorBanner message={error} onRetry={() => void loadHome()} />}
          <section className="section-block drive-section"><SectionHeading icon={<HardDrive size={16} />} title={de.drives} trailing={<span className="count-pill">{de.number(drives.length)}</span>} /><div className="drive-list">
            {loading && drives.length === 0 ? <div className="loading-panel"><LoaderCircle className="spin" size={20} /> {' '}{de.uiLaufwerkeWerdenGesucht}</div> : filteredDrives.map((drive) => <DriveCard key={drive.name} drive={drive} onOpen={() => drive.ready && navigate(drive.name)} />)}
            {!loading && filteredDrives.length === 0 && <div className="empty-panel"><HardDrive size={22} /><strong>{de.uiKeineLaufwerkeGefunden}</strong><span>{de.uiPrufeDieTailscaleVerbindungUndAktualisiere}</span></div>}
          </div></section>
          {visibleFavorites.length > 0 && <section className="section-block"><SectionHeading icon={<Heart size={16} />} title={de.uiFavoriten} trailing={<span className="count-pill">{de.number(visibleFavorites.length)}</span>} /><div className="favorite-rail">{visibleFavorites.map((favorite) => <button key={favorite.path} className="favorite-chip" onClick={() => navigate(favorite.path)}><span className="favorite-icon"><Pin size={15} fill="currentColor" /></span><span>{favorite.name}</span><ChevronRight size={15} /></button>)}</div></section>}

          <div className="home-shortcuts-layout">
          {filteredQuickAccess.length > 0 && <section className="section-block quick-section"><SectionHeading icon={<Pin size={16} />} title="Fast Access" /><div className="quick-grid">
            {filteredQuickAccess.map((folder) => <button key={folder.path} className="quick-card quick-documents" onClick={() => navigate(folder.path)}><span className="quick-icon"><Folder size={18} /></span><span className="quick-label">{folder.name}</span><span className="quick-arrow"><ArrowRight size={14} /></span></button>)}
          </div></section>}

          <section className="section-block quick-section"><SectionHeading icon={<PanelTop size={16} />} title="Important folders" /><div className="quick-grid">
            {filteredFolders.map((folder) => <button key={folder.id} className={`quick-card quick-${folder.id}${!folder.available ? ' is-unavailable' : ''}`} onClick={() => folder.available && navigate(folder.path)} disabled={!folder.available}><span className="quick-icon">{folderIcon(folder.id)}</span><span className="quick-label">{folder.name}</span><span className="quick-arrow"><ArrowRight size={14} /></span></button>)}
            {filteredFolders.length === 0 && !loading && <div className="empty-inline">{de.uiKeinePassendenOrdner}</div>}
          </div></section>
          </div>

          {filteredSharedRoots.length > 0 && <section className="section-block shared-root-section"><SectionHeading icon={<FolderOpen size={16} />} title={de.uiFreigegebeneOrdner} trailing={<span className="count-pill">{de.number(filteredSharedRoots.length)}</span>} /><div className="drive-list">
            {filteredSharedRoots.map((root) => <button key={root.path} className="shared-root-card" onClick={() => navigate(root.path)}><span className="settings-row-icon"><FolderOpen size={19} /></span><span className="shared-root-card-copy"><strong>{root.label || root.path.split(/[\\/]/).filter(Boolean).at(-1) || root.path}</strong><small>{root.path}</small></span>{(shareSettings?.readOnlyMode || root.readOnly) && <LockKeyhole size={14} aria-label={de.readOnly} />}<ChevronRight size={17} /></button>)}
          </div></section>}

        </main>
      )}

      {mainPage === 'files' && currentPath !== null && <>
        <div className="folder-toolbar"><div className="path-entry-button"><nav ref={breadcrumbRef} className="breadcrumb-list" aria-label={de.uiPfadnavigation}>{pathParts(currentPath).map((part, index, all) => <button type="button" key={`${part.path}-${index}`} className="crumb-part" aria-label={de.uiZuOrdnerNavigieren(part.label)} aria-current={index === all.length - 1 ? 'location' : undefined} onClick={() => navigate(part.path)}><span className="crumb-label">{index === 0 && currentPath.startsWith('\\\\') ? `//${part.label.replaceAll('\\', '/')}` : part.label}</span>{(index < all.length - 1 || (all.length === 1 && /^[A-Za-z]:$/.test(part.label))) && <span className="crumb-separator" aria-hidden="true">/</span>}</button>)}</nav><button className="path-edit-button" onClick={startPathDialog} aria-label={de.pathTitle(currentPath)}><ChevronDown size={14} /></button></div></div>
        <h1 className="visually-hidden">{currentLabel}</h1>
        <div className="compact-folder-tools" role="toolbar" aria-label="Ordnerwerkzeuge">
          <button className={`icon-button${folderSearchOpen || query ? ' active' : ''}`} aria-label={de.search} aria-expanded={folderSearchOpen} title={de.search} onClick={() => { setFolderSearchOpen((open) => !open); setViewMenuOpen(false); setFilterOpen(false) }}><Search /></button>
          <button className={`icon-button folder-star${currentFolderIsFavorite ? ' is-favorite' : ''}`} aria-label={currentFolderIsFavorite ? de.uiFavoritEntfernen : de.uiOrdnerZuFavoritenHinzufugen} title={currentFolderIsFavorite ? de.uiFavoritEntfernen : de.uiOrdnerZuFavoritenHinzufugen} onClick={() => {
          const synthetic: FileEntry = { name: currentLabel, path: currentPath, isDirectory: true, size: null, modified: '', created: '', extension: '', contentType: 'inode/directory', isHidden: false, isReadOnly: false, isLink: false }
          toggleFavorite(synthetic)
        }}><Heart size={19} fill={currentFolderIsFavorite ? 'currentColor' : 'none'} /></button>
          <button className={`icon-button${selectionMode ? ' active' : ''}`} aria-label={selectionMode ? de.done : de.uiAuswahlen} title={selectionMode ? de.done : de.uiAuswahlen} aria-pressed={selectionMode} onClick={() => { if (selectionMode) selectAllController.current?.abort(); setSelectionMode((value) => !value); setSelected([]) }}><Check /></button>
          <button className={`icon-button filter-button${filterOpen ? ' active' : ''}`} aria-label={de.uiSortierungUndFilter} title={de.uiSortierungUndFilter} aria-expanded={filterOpen} onClick={() => { setFilterOpen((open) => !open); setViewMenuOpen(false); setFolderSearchOpen(false) }}><SlidersHorizontal /></button>
          <button className={`icon-button${viewMenuOpen ? ' active' : ''}`} aria-label={de.uiAnsicht} title={de.uiAnsicht} aria-expanded={viewMenuOpen} onClick={() => { setViewMenuOpen((open) => !open); setFilterOpen(false); setFolderSearchOpen(false) }}>{view === 'list' ? <List /> : view === 'details' ? <TableProperties /> : <PanelTop />}</button>
          <span className="compact-folder-count" role="status">{sort === 'folder-size' && folderSizeState === 'loading' ? 'Ordnergrößen werden berechnet …' : sort === 'folder-size' && folderSizeState === 'error' ? 'Größe nicht verfügbar' : totalCount === null ? '…' : de.items(totalCount)}</span>
        </div>
        {folderSearchOpen && <div className="folder-search-panel"><label className="search-field folder-search"><Search size={17} /><input ref={folderSearchRef} value={query} onChange={(event) => { searchAbortRef.current?.abort(); setDeepSearchActive(false); setDeepSearching(false); setQuery(event.target.value) }} placeholder={de.search} aria-label={de.search} />{query && <button type="button" className="clear-search" aria-label={de.uiSucheLoschen} onClick={() => { searchAbortRef.current?.abort(); setDeepSearchActive(false); setDeepSearching(false); setQuery(''); folderSearchRef.current?.focus() }}><X size={16} /></button>}<kbd>{de.shortcutFolderSearch}</kbd></label><button className="deep-search-button" disabled={!query.trim() || deepSearching} onClick={() => void startRecursiveSearch()}>{deepSearching ? <LoaderCircle className="spin" size={14} /> : <FolderOpen size={14} />} <span>Auch Unterordner durchsuchen</span></button></div>}
        {viewMenuOpen && <div className="compact-view-menu" role="group" aria-label={de.uiAnsicht}>{([['list', de.list, List], ['details', de.details, TableProperties], ['grid', de.grid, PanelTop]] as const).map(([mode, label, Icon]) => <button key={mode} className={view === mode ? 'selected' : ''} aria-pressed={view === mode} onClick={() => { setView(mode); setViewMenuOpen(false) }}><Icon size={18} />{label}{view === mode && <Check size={16} />}</button>)}</div>}
        {filterOpen && <div className="filter-strip">
          <button className="sort-control" onClick={() => setDescending((value) => !value)} aria-label={descending ? de.uiAbsteigendSortiert : de.uiAufsteigendSortiert}><SortAsc size={16} /><span>{sort === 'folder-size' ? de.folderSize : de[sort as 'name' | 'modified' | 'size' | 'type']}</span><ArrowDown size={13} className={descending ? 'sort-desc' : ''} /></button>
          <div className="filter-chips" role="group" aria-label={de.uiDateitypFiltern}>{[['all', de.uiAlle], ['folders', de.uiOrdner], ['images', de.uiBilder], ['videos', de.uiVideos], ['documents', de.uiDokumente], ['audio', de.uiAudio], ['archives', de.uiArchive], ['code', de.uiTextCode]].map(([id, label]) => <button key={id} className={`filter-chip${filter === id ? ' active' : ''}`} onClick={() => setFilter(id)}>{label}</button>)}</div>
          <label className="filter-field">{de.uiEndung}<input value={extensionFilter} onChange={(event) => setExtensionFilter(event.target.value)} placeholder={de.uiZBPdf} autoCapitalize="off" autoCorrect="off" /></label>
          <label className="filter-field">{de.uiMindestensMB}<input type="number" min="0" step="0.1" value={minSizeFilter} onChange={(event) => setMinSizeFilter(event.target.value)} inputMode="decimal" /></label>
          <label className="filter-field">{de.uiHochstensMB}<input type="number" min="0" step="0.1" value={maxSizeFilter} onChange={(event) => setMaxSizeFilter(event.target.value)} inputMode="decimal" /></label>
          <label className="filter-field">{de.uiGeandertAb}<input type="date" value={modifiedAfterFilter} onChange={(event) => setModifiedAfterFilter(event.target.value)} /></label>
          <label className="filter-field">{de.uiGeandertBis}<input type="date" value={modifiedBeforeFilter} onChange={(event) => setModifiedBeforeFilter(event.target.value)} /></label>
          <button className={`filter-chip hidden-chip${showHidden ? ' active' : ''}`} onClick={() => setShowHidden((value) => !value)}>{showHidden && <Check size={13} />}{de.hidden}</button>
          <label className="filter-content-label"><input type="checkbox" checked={searchContent} onChange={(event) => setSearchContent(event.target.checked)} /> {' '}{de.uiInhalteDurchsuchenTextdateienBis4MB}</label>
          <label className="sort-select-label">{de.uiSortierenNach}<select value={sort} onChange={(event) => setSort(event.target.value)}><option value="name">{de.name}</option><option value="modified">{de.modified}</option><option value="size">{de.size}</option><option value="folder-size">{de.folderSize}</option><option value="type">{de.type}</option></select></label>
          <label className="sort-select-label">{de.uiGruppierenNach}<select aria-label={de.uiGruppierenNach} value={groupBy} onChange={(event) => setGroupBy(event.target.value as GroupMode)}><option value="none">{de.uiKeine}</option><option value="type">{de.type}</option><option value="date">{de.uiDatum}</option><option value="size">{de.size}</option></select></label>
        </div>}
        <main className={`workspace folder-scroll${view === 'details' ? ' details-view' : ''}`} ref={contentRef} onTouchStart={onPullStart} onTouchEnd={onPullEnd} onTouchCancel={() => { lastTouchY.current = null }}>
          {refreshing && <div className="pull-indicator"><LoaderCircle className="spin" size={15} /> {' '}{de.uiWirdAktualisiert}</div>}
          {error && <ErrorBanner message={error} onRetry={() => void refresh()} />}
          {loading && filteredEntries.length === 0 && !error && <div className="loading-panel folder-loading"><LoaderCircle className="spin" size={20} /> {' '}{de.uiOrdnerWirdGelesen}</div>}
          {!loading && !deepSearching && !error && filteredEntries.length === 0 && <div className="empty-folder"><span className="empty-folder-icon"><Folder size={30} /></span><strong>{query || filter !== 'all' ? de.noResults : de.emptyFolder}</strong><span>{query ? de.uiPasseDenSuchbegriffAnOderLoscheDenFilter : de.uiHierErscheinenDateienSobaldWindowsEtwasDarinSpeichert}</span></div>}
          {deepSearching && <div className="page-loading"><LoaderCircle className="spin" size={16} /> {' '}{de.uiSucheInUnterordnernLauftErgebnisseErscheinenFortlaufend}</div>}
          {filteredEntries.length > 0 && <div className={`entry-virtual-list mode-${view}`} style={{ height: `${virtualizer.getTotalSize()}px` }}>
            {virtualItems.map((virtualItem) => {
              const row = displayRows[virtualItem.index]
              if (!row) return <div className="virtual-row loading-row" key="loading" style={{ transform: `translateY(${virtualItem.start}px)`, height: `${virtualItem.size}px` }}><LoaderCircle className="spin" size={18} /> {' '}{de.uiWeitereEintrageWerdenGeladen}</div>
              if (row.type === 'group') return <div key={row.key} className="virtual-row group-header" style={{ transform: `translateY(${virtualItem.start}px)`, height: `${virtualItem.size}px` }}>{row.label}</div>
              if (row.type === 'entries') return <div key={row.key} className="virtual-row grid-virtual-row" style={{ transform: `translateY(${virtualItem.start}px)`, height: `${virtualItem.size}px`, gridTemplateColumns: `repeat(${columnCount}, minmax(0, 1fr))` }}>{row.entries.map((entry) => <EntryCard key={entry.path} entry={entry} selected={selected.includes(entry.path)} mode={view} showExtensions={showExtensions} onOpen={(trigger) => openEntry(entry, trigger)} onLongPress={(rect) => showContext(entry, rect)} onSelect={() => toggleSelected(entry.path)} onSwipe={() => showContext(entry)} onMore={() => showContext(entry)} />)}</div>
              const entry = row.entry
              return <EntryCard key={entry.path} entry={entry} selected={selected.includes(entry.path)} mode={view} showExtensions={showExtensions} onOpen={(trigger) => openEntry(entry, trigger)} onLongPress={(rect) => showContext(entry, rect)} onSelect={() => toggleSelected(entry.path)} onSwipe={() => showContext(entry)} onMore={() => showContext(entry)} style={{ transform: `translateY(${virtualItem.start}px)`, height: `${virtualItem.size}px` }} />
            })}
          </div>}
          {pageLoading && filteredEntries.length > 0 && <div className="page-loading"><LoaderCircle className="spin" size={16} /> {' '}{de.uiMehrDateien}</div>}
          <div className="list-end-spacer" />
        </main>
        <div className="folder-footer"><span>{deepSearchActive ? de.searchResults(totalCount ?? filteredEntries.length, deepSearching) : filteredEntries.length ? de.loadedItems(filteredEntries.length) : ' '}{hasMore ? de.additionalItems : ''}</span>{!deepSearchActive && (entries.some(isPlayableMedia) || hasMore) && <button className="folder-play-button" aria-label={mediaPlayerActive ? de.mediaAddFolderToQueue : de.mediaPlayFolder} title={mediaPlayerActive ? de.mediaAddFolderToQueue : de.mediaPlayFolder} disabled={mediaFolderLoading} onClick={() => void queueFolder(currentPath, !mediaPlayerActive)}>{mediaFolderLoading ? <LoaderCircle className="spin" size={16} /> : mediaPlayerActive ? <Music2 size={16} /> : <Play size={16} />}<span>{mediaPlayerActive ? de.mediaAddAll : de.mediaPlayAll}</span></button>}<span>{health?.readOnly ? <><LockKeyhole size={12} /> {' '}{de.uiNurLesen}</> : de.uiAktuell}</span></div>
        {(selectionMode || selected.length > 0) && <div className="selection-bar" role="toolbar" aria-label={de.uiMehrfachauswahl}>
          <button className="selection-clear" onClick={() => { selectAllController.current?.abort(); setSelected([]); setSelectionMode(false) }} aria-label={de.uiAuswahlAufheben}><X size={17} /></button>
          <strong>{de.number(selected.length)} {' '}{de.uiAusgewahlt}</strong>
          <button className="select-all" disabled={Boolean(selectAllProgress) || deepSearching} onClick={() => void selectAllEntries()}>
            {selectAllProgress ? de.uiSelectAllProgress(selectAllProgress.loaded, selectAllProgress.total) : deepSearching ? de.uiSearchRunning : de.uiAlleAuswahlen}
          </button>
          <button className="selection-action" disabled={selected.length === 0} onClick={() => { const first = currentFolderEntries.find((entry) => selected.includes(entry.path)); if (first) showContext(first) }}><MoreHorizontal size={19} /> {' '}{de.uiAktionen}</button>
        </div>}
      </>}

      {mainPage === 'recent' && <main ref={contentRef} className="workspace secondary-page">
        <PageHeading icon={<Clock3 />} title={de.uiZuletztGeoffnet} subtitle={de.uiDateienDieAufDemPCUndDiesemGeratVerwendetWurden} onRefresh={() => { setRecent(getRecent()); setRefreshKey((value) => value + 1) }} />
        {windowsRecentError && <div className="property-error" role="alert">{windowsRecentError}</div>}
        {windowsRecentLoading && windowsRecentEntries.length === 0 && <div className="loading-panel" role="status"><LoaderCircle className="spin" size={18} />{' '}{de.loading}</div>}
        {!windowsRecentLoading && windowsRecentEntries.length === 0 && recent.length === 0 ? <EmptyState icon={<Clock3 />} title={de.uiNochKeineDateien} body={de.uiGeoffneteDateienErscheinenHier} /> : <>
          {windowsRecentEntries.length > 0 && <section className="recent-group"><h2>{de.uiZuletztAmPCVerwendet}</h2><div className="recent-list">{windowsRecentEntries.map((entry) => <EntryCard key={`windows:${entry.path}`} entry={entry} selected={false} mode="list" showExtensions={showExtensions} onOpen={(trigger) => openEntry(entry, trigger)} onLongPress={(rect) => showContext(entry, rect)} onSelect={() => {}} onSwipe={() => showContext(entry)} onMore={() => showContext(entry)} />)}</div></section>}
          {recent.length > 0 && <section className="recent-group"><h2>{de.uiAufDiesemGeratGeoffnet}</h2><div className="recent-list">{recent.map((entry) => <EntryCard key={`device:${entry.path}`} entry={entry} selected={false} mode="list" showExtensions={showExtensions} onOpen={(trigger) => openEntry(entry, trigger)} onLongPress={(rect) => showContext(entry, rect)} onSelect={() => {}} onSwipe={() => showContext(entry)} onMore={() => showContext(entry)} />)}</div></section>}
        </>}</main>}
      {mainPage === 'transfers' && <main ref={contentRef} className="workspace secondary-page">
        <PageHeading icon={<Download />} title={de.transfers} subtitle={de.uiPCAuftrageBleibenAktivWennSafariGeschlossenWirdUploadsLassenSichNachEinerUnterbrechungFortsetzen} onRefresh={() => { explorerApi.jobs().then(setJobs).catch(() => {}); explorerApi.uploads().then(setUploads).catch(() => {}) }} />
        <div className="upload-toolbar"><button className="primary-button" onClick={() => { resumeUploadId.current = null; uploadInputRef.current?.click() }}><Upload size={17} /> {' '}{de.uiFotosUndDateienAuswahlen}</button><input ref={uploadInputRef} className="file-input-hidden" type="file" multiple aria-label={de.uiFotosUndDateienAuswahlen} onChange={handleUploadSelection} /><span>{de.uiMehrereDateienWiederaufnahmeNachNetzabbruch}</span></div>
        {uploads.length > 0 && <section className="transfer-section"><h2>{de.uiUploads}</h2><div className="upload-list">{uploads.map((session) => <UploadRow key={session.id} session={session} uploading={uploadingIds.includes(session.id)} reconnecting={reconnectingUploadIds.includes(session.id)} onResume={() => { resumeUploadId.current = session.id; uploadInputRef.current?.click() }} onCancel={() => void cancelUpload(session)} />)}</div></section>}
        {jobs.length > 0 ? <section className="transfer-section"><h2>{de.uiDateivorgange}</h2><div className="job-list">{jobs.map((job) => <JobCard key={job.id} job={job} onAction={(action) => explorerApi.jobAction(job.id, action).then((updated) => setJobs((current) => [updated, ...current.filter((item) => item.id !== updated.id)])).catch((cause) => notify(cause instanceof Error ? cause.message : de.uiDerAuftragKonnteNichtGeandertWerden))} />)}</div></section> : uploads.length === 0 && <EmptyState icon={<Smartphone />} title={de.uiNochKeineUbertragungen} body={de.uiWahleFotosOderDateienAusOderStarteEinenKopierauftragImDateiMenu} />}
      </main>}
      {mainPage === 'recycle' && <main ref={contentRef} className="workspace secondary-page"><PageHeading icon={<Trash2 />} title={de.uiPapierkorb} subtitle={de.uiDateienWerdenAnIhrenUrsprunglichenSpeicherortenWiederhergestellt} onRefresh={() => explorerApi.recycleBin().then(setRecycleEntries).catch((cause) => notify(cause instanceof Error ? cause.message : de.uiPapierkorbNichtErreichbar))} />{recycleLoading ? <div className="loading-panel"><LoaderCircle className="spin" size={20} /> {' '}{de.uiPapierkorbWirdGelesen}</div> : recycleEntries.length === 0 ? <EmptyState icon={<Trash2 />} title={de.uiDerPapierkorbIstLeer} body={de.uiGeloschteDateienErscheinenHierSofernWindowsSieInDenPapierkorbVerschiebenKonnte} /> : <><div className="recycle-actions"><button className="secondary-button" onClick={() => { if (window.confirm(de.uiDenGesamtenWindowsPapierkorbEndgultigLeeren)) explorerApi.emptyRecycleBin().then(() => { setRecycleEntries([]); notify(de.uiPapierkorbGeleert) }).catch((cause) => notify(cause instanceof Error ? cause.message : de.uiPapierkorbKonnteNichtGeleertWerden)) }}>{de.uiPapierkorbLeeren}</button></div><div className="recycle-list">{recycleEntries.map((item) => <RecycleRow key={item.id} item={item} onRestore={() => explorerApi.restoreRecycleBin(item.id).then(() => { setRecycleEntries((current) => current.filter((candidate) => candidate.id !== item.id)); notify(de.uiDateiWiederhergestellt) }).catch((cause) => notify(cause instanceof Error ? cause.message : de.uiWiederherstellenFehlgeschlagen))} onDelete={() => { if (window.confirm(de.confirmDeleteOne(item.name))) explorerApi.deleteRecycleBin(item.id).then(() => setRecycleEntries((current) => current.filter((candidate) => candidate.id !== item.id))).catch((cause) => notify(cause instanceof Error ? cause.message : de.uiLoschenFehlgeschlagen)) }} />)}</div></>}</main>}
      {mainPage === 'clean' && <main ref={contentRef} className="workspace secondary-page"><CleanPage /></main>}
      {mainPage === 'more' && <main ref={contentRef} className="workspace secondary-page"><PageHeading icon={<Settings2 />} title={de.more} subtitle={de.uiDateiaktionenPapierkorbUndVerbindungVerwalten} /><div className="settings-card-list">
        <button className="settings-row priority-row" disabled={!currentPath} onClick={() => { if (currentPath) void runContextAction('new-folder', { name: '', path: currentPath, isDirectory: true, size: null, modified: '', created: '', extension: '', contentType: '', isHidden: false, isReadOnly: false, isLink: false }) }}><span className="settings-row-icon"><FolderPlus size={19} /></span><span><strong>{de.uiNeuerOrdner}</strong><small>{currentPath ? de.uiImAktuellGeoffnetenOrdnerErstellen : 'Zuerst einen Ordner öffnen'}</small></span><ChevronRight /></button>
        <button className="settings-row priority-row" onClick={() => openPage('recycle')}><span className="settings-row-icon"><Trash2 size={19} /></span><span><strong>{de.uiPapierkorb}</strong><small>{de.uiWiederherstellenOderEndgultigLoschen}</small></span><ChevronRight /></button>
        <button className="settings-row priority-row" onClick={() => openPage('clean')}><span className="settings-row-icon"><Sparkles size={19} /></span><span><strong>Special Clean</strong><small>Temporäre Dateien und Caches prüfen</small></span><ChevronRight /></button>
        {currentPath && <button className="settings-row" onClick={() => void runContextAction('new-file', { name: '', path: currentPath, isDirectory: true, size: null, modified: '', created: '', extension: '', contentType: '', isHidden: false, isReadOnly: false, isLink: false })}><span className="settings-row-icon"><FilePlus2 size={19} /></span><span><strong>{de.uiNeueTextdatei}</strong><small>{de.uiLeereUTF8DateiAnlegen}</small></span><ChevronRight /></button>}
        {clipboard && currentPath && <button className="settings-row" onClick={() => void runContextAction('paste', { name: '', path: currentPath, isDirectory: true, size: null, modified: '', created: '', extension: '', contentType: '', isHidden: false, isReadOnly: false, isLink: false })}><span className="settings-row-icon"><ClipboardPaste size={19} /></span><span><strong>{de.uiEinfugen}</strong><small>{de.number(clipboard.paths.length)} {de.uiVorgemerkteElemente}</small></span><ChevronRight /></button>}
        <button className="settings-row" onClick={() => openPage('recent')}><span className="settings-row-icon"><Clock3 size={19} /></span><span><strong>{de.recent}</strong><small>{de.uiZuletztGeoffnet}</small></span><ChevronRight /></button>
        <button className="settings-row" onClick={() => openPage('transfers')}><span className="settings-row-icon"><Download size={19} /></span><span><strong>{de.transfers}</strong></span><ChevronRight /></button>
        <button className="settings-row" onClick={startPathDialog}><span className="settings-row-icon"><FolderOpen size={19} /></span><span><strong>{de.uiPfadOffnen}</strong><small>{de.uiDirektZuEinemOrdnerWechseln}</small></span><ChevronRight /></button>
        <button className="settings-row" onClick={() => setPinSheetOpen(true)}><span className="settings-row-icon"><LockKeyhole size={19} /></span><span><strong>{pinStatus.isConfigured ? de.pinTitle : de.pinCreate}</strong><small>{pinStatus.isConfigured ? de.uiPINAndernOderAusschalten : de.pinDescription}</small></span><ChevronRight /></button>
        {pinStatus.isConfigured && <button className="settings-row" onClick={() => void lockApp()}><span className="settings-row-icon"><LockKeyhole size={19} /></span><span><strong>{de.pinLockNow}</strong><small>{de.uiPINBeimNachstenOffnenErneutEingeben}</small></span><ChevronRight /></button>}
        <button className="settings-row" onClick={() => void openSettings()}><span className="settings-row-icon"><Settings2 size={19} /></span><span><strong>{de.uiEinstellungen}</strong><small>{de.uiDarstellungUndAnzeigeoptionen}</small></span><ChevronRight /></button>
        <button className="settings-row" onClick={() => void refresh()}><span className="settings-row-icon"><RefreshCw size={19} /></span><span><strong>{de.uiVerbindungPrufen}</strong><small>{connection === 'online' ? de.uiDerPCIstErreichbar : de.uiErneutVerbinden}</small></span><ChevronRight /></button>
        <button className="settings-row" onClick={() => window.location.reload()}><span className="settings-row-icon"><RefreshCw size={19} /></span><span><strong>App neu laden</strong><small>Die Oberfläche erneut öffnen</small></span><ChevronRight /></button>
        <div className="about-card"><span className="about-icon"><LockKeyhole size={18} /></span><div><strong>{de.uiPrivatUberTailscale}</strong><p>{de.uiDieVerbindungLauftVerschlusseltUberDeinTailscaleNetz}</p><small>{de.uiWindowsExplorer} {health?.version ?? '…'}</small></div></div>
      </div></main>}

      <nav className="bottom-nav" aria-label={de.uiHauptnavigation}>
        <NavItem active={mainPage === 'files'} icon={<Folder size={21} />} label={de.files} onClick={() => openPage('files')} />
        <button className="nav-item" onClick={() => navigate(null)} aria-label={de.thisPc} title={de.thisPc}><span className="nav-icon-wrap"><HardDrive size={21} /></span></button>
        <button className="nav-item" onClick={goBack} disabled={historyIndex <= 0} aria-label={de.back} title={de.back}><span className="nav-icon-wrap"><ArrowLeft size={21} /></span></button>
        <button className="nav-item" onClick={goForward} disabled={historyIndex >= history.length - 1} aria-label={de.forward} title={de.forward}><span className="nav-icon-wrap"><ArrowRight size={21} /></span></button>
        <button className="nav-item" onClick={() => { const parent = currentPath && parentPath(currentPath); if (parent) navigate(parent) }} disabled={!canGoUp} aria-label={de.up} title={de.up}><span className="nav-icon-wrap"><ArrowUp size={21} /></span></button>
        <span className="bottom-nav-spacer" aria-hidden="true" />
        <NavItem active={mainPage === 'more' || mainPage === 'recycle' || mainPage === 'recent' || mainPage === 'transfers' || mainPage === 'clean'} icon={<MoreHorizontal size={22} />} label={de.more} onClick={() => { if (mainPage === 'more') openPage(moreReturnPageRef.current); else { moreReturnPageRef.current = mainPage; openPage('more') } }} badge={jobs.some((job) => ['queued', 'running', 'paused', 'conflict'].includes(job.status)) ? 1 : 0} />
      </nav>

      </div>
      </div>
      {navigationOpen && <FolderNavigationDrawer {...navigationProps} toggleControl={navigationToggleRef.current} onClose={() => setNavigationOpen(false)} />}
      {mediaQueue.length > 0 && <MediaPlayer queue={mediaQueue} startPath={mediaStartPath} sessionId={mediaSessionId} onClose={() => { mediaFolderController.current?.abort(); setMediaQueue([]) }} onQueueChange={setMediaQueue} />}

      {contextMenu && <ActionSheet entry={contextMenu.entry} favorite={favorites.some((path) => sameWindowsPath(path, contextMenu.entry.path))} hasClipboard={Boolean(clipboard)} onClose={() => setContextMenu(null)} onAction={(action) => { void runContextAction(action, contextMenu.entry) }} />}

      {picker && <FolderPicker kind={picker.kind} path={pickerPath} drives={drives} knownFolders={knownFolders} folders={pickerFolders} loading={pickerLoading} onClose={() => setPicker(null)} onSelectPath={setPickerPath} onConfirm={() => void confirmFolderPick()} />}
      {uploadConflict && <UploadConflictDialog session={uploadConflict} onResolve={(choice) => void finishUploadConflict(choice)} onCancel={() => setUploadConflict(null)} />}
      {jobs.find((job) => job.status === 'conflict' && job.conflict) && <ConflictDialog job={jobs.find((job) => job.status === 'conflict' && job.conflict)!} onClose={() => { const job = jobs.find((item) => item.status === 'conflict' && item.conflict); if (job) void explorerApi.resolveConflict(job.id, 'skip', false).then((updated) => setJobs((current) => [updated, ...current.filter((item) => item.id !== updated.id)])).catch((cause) => notify(cause instanceof Error ? cause.message : de.uiKonfliktentscheidungFehlgeschlagen)) }} onResolve={(choice, applyToAll) => { const job = jobs.find((item) => item.status === 'conflict' && item.conflict); if (!job) return; explorerApi.resolveConflict(job.id, choice, applyToAll).then((updated) => setJobs((current) => [updated, ...current.filter((item) => item.id !== updated.id)])).catch((cause) => notify(cause instanceof Error ? cause.message : de.uiKonfliktentscheidungFehlgeschlagen)) }} />}

      {preview && <PreviewModal key={preview.path} entry={preview} text={previewText} error={previewError} zoom={previewZoom} returnFocusTarget={previewReturnFocusRef.current} onZoom={setPreviewZoom} onClose={() => { setPreview(null); setPreviewText(null); setPreviewError('') }} onDownload={() => downloadEntry(preview)} onProperties={(trigger) => { propertiesReturnFocusRef.current = trigger; setProperties(preview) }} onEdit={() => void openEditor(preview)} />}
      {editorDocument && <TextEditorModal document={editorDocument} content={editorContent} loading={editorLoading} saving={editorSaving} error={editorError} onChange={setEditorContent} onSave={() => void saveEditor()} onClose={() => { setEditorDocument(null); setEditorError('') }} onReload={() => void openEditor({ name: editorDocument.name, path: editorDocument.path, isDirectory: false, size: null, modified: editorDocument.modified, created: '', extension: editorDocument.name.split('.').at(-1) ?? '', contentType: 'text/plain', isHidden: false, isReadOnly: false, isLink: false })} />}
      {properties && <PropertiesSheet entry={properties} details={propertyDetails} loading={propertiesLoading} error={propertiesError} returnFocusTarget={propertiesReturnFocusRef.current} onClose={() => setProperties(null)} onHash={(algorithm) => { setPropertiesLoading(true); explorerApi.properties(properties.path, algorithm).then(setPropertyDetails).catch((cause) => setPropertiesError(cause instanceof Error ? cause.message : de.uiHashKonnteNichtBerechnetWerden)).finally(() => setPropertiesLoading(false)) }} onAttribute={(change) => { explorerApi.setAttributes(properties.path, change).then(() => explorerApi.properties(properties.path)).then((details) => { setPropertyDetails(details); setRefreshKey((value) => value + 1); notify(de.uiAttributeGespeichert) }).catch((cause) => notify(cause instanceof Error ? cause.message : de.uiAttributeKonntenNichtGeandertWerden)) }} />}
      {settingsOpen && <SettingsSheet
        showHidden={showHidden} setShowHidden={setShowHidden}
        showSystem={showSystem} setShowSystem={updateShowSystem}
        showExtensions={showExtensions} setShowExtensions={updateShowExtensions}
        theme={theme} setTheme={setTheme}
        textSize={textSize} setTextSize={setTextSize}
        shareSettings={shareSettings} loading={shareSettingsLoading} saving={shareSettingsSaving}
        error={shareSettingsError} drives={drives} knownFolders={knownFolders}
        onSave={saveShareSettings} onDraftChange={setShareSettings} onClose={() => setSettingsOpen(false)}
        onPath={startPathDialog} onRefresh={() => void refresh()}
      />}
      {pinSheetOpen && <PinSettingsSheet configured={pinStatus.isConfigured} onClose={() => setPinSheetOpen(false)} onDone={(configured) => { setPinStatus({ isConfigured: configured, isUnlocked: configured, retryAfterSeconds: 0 }); setPinSheetOpen(false); notify(configured ? de.pinSaved : de.pinDisabled) }} />}
      {newFileDialogOpen && <FileTemplateDialog name={newFileName} template={newFileTemplate} onNameChange={setNewFileName} onTemplateChange={(template) => { setNewFileTemplate(template); setNewFileName(de.newFileName(FILE_TEMPLATES[template].extension)) }} onClose={() => setNewFileDialogOpen(false)} onCreate={(name, template) => {
        if (!currentPath) return
        setNewFileDialogOpen(false)
        void startJob({ kind: 'new-file', sources: [], destination: currentPath, newName: name, template })
      }} />}
      {pathOpen && <PathDialog path={pathInput} error={pathModalError} onPathChange={(value) => { setPathInput(value); setPathModalError('') }} onErrorChange={setPathModalError} onClose={() => setPathOpen(false)} onSubmit={submitPath} />}
      {toast && <div className="toast" role="status"><span className="toast-mark"><Check size={14} /></span><span>{toast.message}</span>{toast.action && <button onClick={toast.action.run}>{toast.action.label}</button>}</div>}
    </div>
  )
}

function PathDialog({ path, error, onPathChange, onErrorChange, onClose, onSubmit }: { path: string; error: string; onPathChange: (value: string) => void; onErrorChange: (value: string) => void; onClose: () => void; onSubmit: (event: React.FormEvent<HTMLFormElement>) => void }) {
  const dialogRef = useModalDialogFocus<HTMLFormElement>(onClose)
  return <div className="modal-backdrop" onClick={onClose}><form ref={dialogRef} className="path-dialog" role="dialog" aria-modal="true" aria-labelledby="path-dialog-title" tabIndex={-1} onClick={(event) => event.stopPropagation()} onSubmit={onSubmit}><div className="dialog-heading"><span className="sheet-icon"><FolderOpen size={20} /></span><div><h2 id="path-dialog-title">{de.enterPath}</h2><p>{de.uiFugeEinenWindowsPfadEinOderTippeIhnEin}</p></div><button type="button" className="icon-button" aria-label={de.close} onClick={onClose}><X /></button></div><label className="path-input-label"><span>{de.uiOrdnerpfad}</span><input autoFocus data-dialog-initial-focus value={path} onChange={(event) => { onPathChange(event.target.value); onErrorChange('') }} placeholder={de.uiCUsers} autoCapitalize="off" autoCorrect="off" spellCheck={false} />{error && <small>{error}</small>}</label><div className="dialog-actions"><button type="button" className="secondary-button" onClick={onClose}>{de.cancel}</button><button type="submit" className="primary-button">{de.uiOrdnerOffnen}{' '}<ArrowRight size={16} /></button></div></form></div>
}

function folderIcon(id: string) {
  const size = 19
  if (id === 'desktop') return <PanelTop size={size} />
  if (id === 'documents') return <FileText size={size} />
  if (id === 'downloads') return <Download size={size} />
  if (id === 'pictures') return <FileImage size={size} />
  if (id === 'videos') return <Film size={size} />
  return <Music2 size={size} />
}

function PinGate({ loading, error, status, onRetry, onUnlock }: {
  loading: boolean
  error: string
  status: PinStatus | null
  onRetry: () => void
  onUnlock: (pin: string) => Promise<void>
}) {
  const [pin, setPin] = useState('')
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setMessage('')
    setBusy(true)
    try { await onUnlock(pin); setPin('') }
    catch (cause) { setMessage(cause instanceof Error ? cause.message : de.uiDieAppKonnteNichtEntsperrtWerden); setPin('') }
    finally { setBusy(false) }
  }

  return <main className="pin-gate ss-theme">
    <section className="pin-gate-card" aria-labelledby="pin-gate-title">
      <span className="pin-gate-mark"><LockKeyhole size={25} /></span>
      <p className="pin-gate-kicker">{de.appName}</p>
      <h1 id="pin-gate-title">{loading ? de.loading : error ? de.connectionLost : de.pinLocked}</h1>
      <p className="pin-gate-description">{error || (status?.isConfigured ? de.uiGibDeinenPINEinUmDeineDateienSicherZuOffnen : de.pinOffline)}</p>
      {status?.isConfigured && !error && <form className="pin-gate-form" onSubmit={(event) => void submit(event)}>
        <label className="path-input-label"><span>{de.pinEnter}</span><input autoFocus value={pin} onChange={(event) => { setPin(event.target.value.replace(/\D/g, '').slice(0, 12)); setMessage('') }} type="password" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6,12}" minLength={6} maxLength={12} required aria-label={de.pinEnter} /></label>
        {message && <p className="pin-feedback" role="alert">{message}</p>}
        {status.retryAfterSeconds > 0 && <p className="pin-feedback" role="status">{de.uiBitteWarte}{' '}{status.retryAfterSeconds} {' '}{de.uiSekundenVorDemNachstenVersuch}</p>}
        <button className="primary-button pin-submit" type="submit" disabled={busy || pin.length < 6 || status.retryAfterSeconds > 0}>{busy ? <><LoaderCircle className="spin" size={17} /> {' '}{de.uiWirdGepruft}</> : de.pinUnlock}</button>
      </form>}
      {(error || !status?.isConfigured) && <button className="primary-button pin-submit" onClick={onRetry} disabled={loading}>{loading ? <LoaderCircle className="spin" size={17} /> : <RefreshCw size={16} />}{de.pinRetry}</button>}
    </section>
  </main>
}

function PinSettingsSheet({ configured, onClose, onDone }: { configured: boolean; onClose: () => void; onDone: (configured: boolean) => void }) {
  const dialogRef = useModalDialogFocus<HTMLFormElement>(onClose)
  const [pin, setPin] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const save = async (event: React.FormEvent) => {
    event.preventDefault()
    setError('')
    if (pin !== confirmation) { setError(de.pinMismatch); return }
    setBusy(true)
    try { await explorerApi.configurePin(pin); onDone(true) }
    catch (cause) { setError(cause instanceof Error ? cause.message : de.uiDerPINKonnteNichtGespeichertWerden) }
    finally { setBusy(false) }
  }

  const disable = async () => {
    setError('')
    setBusy(true)
    try { await explorerApi.disablePin(); onDone(false) }
    catch (cause) { setError(cause instanceof Error ? cause.message : de.uiDerPINSchutzKonnteNichtAusgeschaltetWerden) }
    finally { setBusy(false) }
  }

  return <div className="modal-backdrop" onClick={onClose}><form ref={dialogRef} className="path-dialog pin-settings-dialog" role="dialog" aria-modal="true" aria-labelledby="pin-settings-title" tabIndex={-1} onClick={(event) => event.stopPropagation()} onSubmit={(event) => void save(event)}>
    <div className="dialog-heading"><span className="sheet-icon"><LockKeyhole size={20} /></span><div><h2 id="pin-settings-title">{configured ? de.pinChange : de.pinCreate}</h2><p>{de.pinDescription} {' '}{de.uiNurFurDiesesGeratTailscaleBleibtErforderlich}</p></div><button type="button" className="icon-button" onClick={onClose} aria-label={de.close}><X /></button></div>
    <label className="path-input-label"><span>{configured ? de.uiNeuerPIN : de.pinEnter}</span><input autoFocus data-dialog-initial-focus value={pin} onChange={(event) => setPin(event.target.value.replace(/\D/g, '').slice(0, 12))} type="password" inputMode="numeric" autoComplete="new-password" pattern="[0-9]{6,12}" minLength={6} maxLength={12} required aria-label={configured ? de.uiNeuerPIN : de.pinEnter} /></label>
    <small className="pin-hint">{de.pinDigits}</small>
    <label className="path-input-label"><span>{de.pinConfirm}</span><input value={confirmation} onChange={(event) => setConfirmation(event.target.value.replace(/\D/g, '').slice(0, 12))} type="password" inputMode="numeric" autoComplete="new-password" pattern="[0-9]{6,12}" minLength={6} maxLength={12} required aria-label={de.pinConfirm} /></label>
    {error && <p className="pin-feedback" role="alert">{error}</p>}
    <div className="dialog-actions pin-dialog-actions"><button type="button" className="secondary-button" onClick={onClose} disabled={busy}>{de.cancel}</button><button className="primary-button" type="submit" disabled={busy || pin.length < 6 || confirmation.length < 6}>{busy ? <LoaderCircle className="spin" size={16} /> : <Check size={16} />}{configured ? de.pinChange : de.pinCreate}</button></div>
    {configured && <button type="button" className="pin-disable-button" onClick={() => void disable()} disabled={busy}>{de.pinDisable}</button>}
  </form></div>
}

function SectionHeading({ icon, title, trailing }: { icon: React.ReactNode; title: string; trailing?: React.ReactNode }) {
  return <div className="section-heading"><div className="section-title-icon">{icon}</div><h2>{title}</h2>{trailing && <div className="section-trailing">{trailing}</div>}</div>
}

function DriveCard({ drive, onOpen }: { drive: Drive; onOpen: () => void }) {
  const used = drive.totalBytes !== null && drive.freeBytes !== null ? Math.max(0, drive.totalBytes - drive.freeBytes) : 0
  const percent = drive.totalBytes ? Math.min(100, (used / drive.totalBytes) * 100) : 0
  const driveLabel = drive.name.replace(/[\\/]+$/, '')
  return <button className={`drive-card${!drive.ready ? ' drive-not-ready' : ''}`} onClick={onOpen} disabled={!drive.ready}><span className="drive-icon-wrap"><HardDrive size={22} /></span><span className="drive-copy"><span className="drive-name-line"><strong>{driveLabel}{drive.displayName !== driveLabel ? ` · ${drive.displayName}` : ''}{drive.format ? ` · ${drive.format}` : ''}</strong>{drive.readOnly && <LockKeyhole size={13} aria-label={de.readOnly} />}</span>{!drive.ready && <span className="drive-type">{de.unavailable}</span>}{drive.ready && drive.totalBytes !== null && <span className="drive-meter"><span className="drive-meter-fill" style={{ width: `${percent}%` }} /></span>}<span className="drive-space">{drive.ready && drive.totalBytes !== null ? de.driveSpace(formatBytes(drive.freeBytes), formatBytes(drive.totalBytes)) : de.uiLaufwerkVerbindenUmEsZuOffnen}</span></span><span className="drive-chevron"><ChevronRight size={18} /></span></button>
}

function EntryCard({ entry, selected, mode, onOpen, onLongPress, onSelect, onSwipe, onMore, style, showExtensions = true }: { entry: FileEntry; selected: boolean; mode: ViewMode; onOpen: (trigger: HTMLButtonElement) => void; onLongPress: (rect: DOMRect) => void; onSelect: () => void; onSwipe: () => void; onMore: () => void; style?: React.CSSProperties; showExtensions?: boolean }) {
  const timer = useRef<number | undefined>(undefined)
  const longPressed = useRef(false)
  const startX = useRef<number | null>(null)
  const startY = useRef<number | null>(null)
  const begin = (event: React.PointerEvent<HTMLButtonElement>) => {
    longPressed.current = false
    if (event.pointerType === 'touch') {
      startX.current = event.clientX
      startY.current = event.clientY
      const target = event.currentTarget
      timer.current = window.setTimeout(() => { target.focus(); longPressed.current = true; onLongPress(target.getBoundingClientRect()) }, 520)
    }
  }
  const move = (event: React.PointerEvent<HTMLButtonElement>) => {
    if (startX.current !== null && (Math.abs(event.clientX - startX.current) > 10 || Math.abs(event.clientY - (startY.current ?? event.clientY)) > 10)) window.clearTimeout(timer.current)
  }
  const end = (event: React.PointerEvent<HTMLButtonElement>) => {
    window.clearTimeout(timer.current)
    if (startX.current !== null && event.clientX - startX.current < -75 && Math.abs(event.clientY - (startY.current ?? event.clientY)) < 45) { event.currentTarget.focus(); longPressed.current = true; onSwipe() }
    startX.current = null
    startY.current = null
  }
  const useDetails = mode === 'details'
  const sizeLabel = entrySizeLabel(entry)
  return <div className={`entry-card-shell entry-shell-${mode}${style ? ' virtual-row' : ''}`} style={style}>
    <button type="button" className={`entry-card entry-${mode}${selected ? ' selected' : ''}${entry.isHidden ? ' hidden-entry' : ''}`}
    onPointerDown={begin} onPointerMove={move} onPointerUp={end} onPointerCancel={() => window.clearTimeout(timer.current)}
    onClick={(event) => longPressed.current ? (longPressed.current = false) : onOpen(event.currentTarget)}
    onContextMenu={(event) => { event.preventDefault(); event.currentTarget.focus(); onLongPress(event.currentTarget.getBoundingClientRect()) }}
    onKeyDown={(event) => { if ((event.key === ' ' || event.key === 'Enter') && event.shiftKey) { event.preventDefault(); onLongPress(event.currentTarget.getBoundingClientRect()) } }}
    onAuxClick={(event) => { if (event.button === 1) onSelect() }} aria-label={de.itemAccessibleName(entry.isDirectory, entry.name, entry.isReadOnly)} aria-pressed={selected}>
    {mode === 'grid' ? <><span className="grid-icon-stage"><EntryThumbnail entry={entry} large /></span><span className="grid-entry-name">{displayFileName(entry, showExtensions)}</span>{sizeLabel && <span className="grid-entry-meta" title={entry.sizeIncomplete ? 'Größe unvollständig' : undefined}>{sizeLabel}</span>}</> : <><span className="entry-main"><span className="entry-icon-wrap"><EntryGlyph entry={entry} /><span className="entry-status">{entry.isLink ? <ArrowRight size={10} /> : null}</span></span><span className="entry-copy"><span className="entry-name-line"><span className="entry-name">{displayFileName(entry, showExtensions)}</span>{entry.isReadOnly && <LockKeyhole size={12} className="readonly-mark" />}</span>{!useDetails && sizeLabel && <span className="entry-subtitle" title={entry.sizeIncomplete ? 'Größe unvollständig' : undefined}>{sizeLabel}</span>}</span></span><span className={`entry-meta${useDetails ? ' details-meta' : ''}`}>{formatDate(entry.modified)}</span>{useDetails && <span className="entry-size">{entry.isDirectory ? (entry.sizeIncomplete === null || entry.sizeIncomplete === undefined ? '—' : `${entry.sizeIncomplete ? '≥ ' : ''}${formatBytes(entry.size)}`) : formatBytes(entry.size)}</span>}</>}
    </button>
    <button type="button" className={`entry-more${mode === 'grid' ? ' entry-more-grid' : ''}`} aria-label={de.moreActionsFor(entry.name)} onClick={(event) => { event.stopPropagation(); event.currentTarget.focus(); onMore() }}><MoreHorizontal size={19} /></button>
  </div>
}

function NavItem({ active, icon, label, onClick, badge }: { active: boolean; icon: React.ReactNode; label: string; onClick: () => void; badge?: number }) {
  return <button className={`nav-item${active ? ' active' : ''}`} onClick={onClick} aria-label={label} title={label} aria-current={active ? 'page' : undefined}><span className="nav-icon-wrap">{icon}{badge ? <i className="nav-badge" /> : null}</span></button>
}

function ErrorBanner({ message, onRetry }: { message: string; onRetry: () => void }) {
  return <div className="error-banner"><span className="error-icon"><CircleAlert size={18} /></span><span><strong>{de.uiVerbindungFehlgeschlagen}</strong><small>{message}</small></span><button onClick={onRetry} aria-label={de.pinRetry}><RefreshCw size={16} /></button></div>
}

function EmptyState({ icon, title, body }: { icon: React.ReactNode; title: string; body: string }) {
  return <div className="empty-state"><span className="empty-state-icon">{icon}</span><strong>{title}</strong><p>{body}</p></div>
}

function PageHeading({ icon, title, subtitle, onRefresh }: { icon: React.ReactNode; title: string; subtitle: string; onRefresh?: () => void }) {
  return <div className="secondary-heading"><span className="secondary-heading-icon">{icon}</span><div><h1>{title}</h1><p>{subtitle}</p></div>{onRefresh && <button className="icon-button" aria-label={de.refresh} onClick={onRefresh}><RefreshCw size={18} /></button>}</div>
}

function ActionSheet({ entry, favorite, hasClipboard, onClose, onAction }: { entry: FileEntry; favorite: boolean; hasClipboard: boolean; onClose: () => void; onAction: (action: string) => void }) {
  const sheetRef = useModalDialogFocus<HTMLElement>(onClose)
  const button = (action: string, icon: React.ReactNode, label: string, primary = false) => <ActionButton key={action} icon={icon} label={label} onClick={() => onAction(action)} primary={primary} />
  const isArchive = ['zip', '7z'].includes(entry.extension.toLowerCase())
  const isEditableText = fileKind(entry) === 'code'
  return <div className="modal-backdrop sheet-backdrop" onClick={onClose}><section ref={sheetRef} className="action-sheet" role="dialog" aria-modal="true" onClick={(event) => event.stopPropagation()} aria-label={de.actionsFor(entry.name)} tabIndex={-1}><div className="sheet-grabber" /><div className="sheet-entry"><span className="sheet-entry-icon"><EntryGlyph entry={entry} /></span><span className="sheet-entry-copy"><strong>{entry.name || de.uiAktuellerOrdner}</strong>{entrySizeLabel(entry) && <small title={entry.sizeIncomplete ? 'Größe unvollständig' : undefined}>{entrySizeLabel(entry)}</small>}</span><button className="icon-button" onClick={onClose} aria-label={de.close}><X size={19} /></button></div><div className="action-grid">
    {button('copy', <Copy />, de.copy, true)}
    {button('cut', <Scissors />, de.uiAusschneiden, true)}
    {button('move', <MoveRight />, de.move, true)}
    {button('delete', <Trash2 />, de.uiInPapierkorb, true)}
    {button('properties', <Info />, de.properties, true)}
    {button('open', <FolderOpen />, de.open)}
    {isPlayableMedia(entry) && button('enqueue', <Music2 />, de.mediaAddToQueue)}
    {entry.isDirectory && button('play-folder', <Play />, de.mediaPlayFolder)}
    {entry.isDirectory && button('queue-folder', <Music2 />, de.mediaAddFolderToQueue)}
    {button('download', entry.isDirectory ? <Archive /> : <Download />, entry.isDirectory ? de.uiAlsZIPLaden : de.download)}
    {button('share', <Share2 />, de.share)}
    {entry.isDirectory && button('favorite', favorite ? <Heart fill="currentColor" /> : <Pin />, favorite ? de.uiFavoritEntfernen : de.uiZumFavoriten)}
    {button('copy-path', <Copy />, de.uiPfadKopieren)}
    {hasClipboard && button('paste', <ClipboardPaste />, de.uiEinfugen)}
    {button('rename', <PenLine />, de.rename)}
    {button('zip', <Archive />, de.uiZIPErstellen)}
    {isArchive && button('unzip', <FileArchive />, de.uiEntpacken)}
    {button('show-in-explorer', <FolderOpen />, de.uiImExplorerZeigen)}
    {isEditableText && button('edit', <PenLine />, de.uiTextBearbeiten)}
    {button('permanent-delete', <Trash2 />, de.uiEndgultigLoschen)}
  </div><p className="long-press-note">{de.uiLangeDruckenFurDiesesMenuNachLinksWischenFurSchnellenZugriff}</p></section></div>
}

function ActionButton({ icon, label, onClick, primary = false }: { icon: React.ReactNode; label: string; onClick: () => void; primary?: boolean }) {
  return <button className={`action-button${primary ? ' primary-action' : ''}`} onClick={onClick}><span className="action-icon">{icon}</span><span>{label}</span></button>
}

function PreviewModal({ entry, text, error, zoom, onZoom, returnFocusTarget, onClose, onDownload, onProperties, onEdit }: { entry: FileEntry; text: string | null; error: string; zoom: number; onZoom: (zoom: number) => void; returnFocusTarget: HTMLElement | null; onClose: () => void; onDownload: () => void; onProperties: (trigger: HTMLButtonElement) => void; onEdit: () => void }) {
  const kind = fileKind(entry)
  const isMarkdown = ['md', 'markdown'].includes(entry.extension.toLowerCase())
  const isPdf = entry.extension.toLowerCase() === 'pdf'
  const source = entry.extension.toLowerCase() === 'svg' ? explorerApi.thumbnailUrl(entry.path, 1024) : explorerApi.streamUrl(entry)
  const overlayRef = useModalDialogFocus<HTMLDivElement>(onClose, returnFocusTarget)
  const imageRef = useRef<HTMLImageElement>(null)
  const pointers = useRef(new Map<number, { x: number; y: number }>())
  const pinchStart = useRef<{ distance: number; zoom: number } | null>(null)
  const panStart = useRef<{ x: number; y: number; left: number; top: number } | null>(null)
  const [imageOffset, setImageOffset] = useState({ x: 0, y: 0 })
  const [manipulatingImage, setManipulatingImage] = useState(false)
  const [markdownResult, setMarkdownResult] = useState<{ path: string; text: string; html: string } | null>(null)
  const [highlightedResult, setHighlightedResult] = useState<{ text: string; extension: string; html: string } | null>(null)
  const markdownReady = markdownResult?.path === entry.path && markdownResult.text === text
  const markdownLoading = isMarkdown && text !== null && !markdownReady
  const markdownContent = markdownReady ? markdownResult.html : text === null ? '' : `<pre>${escapeHtml(text)}</pre>`
  useEffect(() => {
    if (text === null || kind !== 'code' || !isMarkdown) return
    let cancelled = false
    void import('./markdownPreview').then(({ renderMarkdownPreview }) => {
      if (cancelled) return
      try { setMarkdownResult({ path: entry.path, text, html: renderMarkdownPreview(text, entry.path) }) }
      catch { setMarkdownResult({ path: entry.path, text, html: `<pre>${escapeHtml(text)}</pre>` }) }
    }).catch(() => {
      if (!cancelled) setMarkdownResult({ path: entry.path, text, html: `<pre>${escapeHtml(text)}</pre>` })
    })
    return () => { cancelled = true }
  }, [text, kind, isMarkdown, entry.extension, entry.path])
  useEffect(() => {
    if (text === null || kind !== 'code' || isMarkdown) return
    let cancelled = false
    void import('./codeHighlight').then(({ highlightCode }) => highlightCode(text, entry.extension)).then((result) => {
      if (!cancelled && result !== null) setHighlightedResult({ text, extension: entry.extension, html: result })
    }).catch(() => {})
    return () => { cancelled = true }
  }, [text, kind, isMarkdown, entry.extension])
  const highlighted = useMemo(() => {
    if (text === null || kind !== 'code' || isMarkdown) return ''
    return highlightedResult?.text === text && highlightedResult.extension === entry.extension
      ? highlightedResult.html
      : escapeHtml(text)
  }, [text, kind, isMarkdown, entry.extension, highlightedResult])
  const clampZoom = (value: number) => Math.max(0.5, Math.min(4, value))
  const clampOffset = (offset: { x: number; y: number }, scale: number) => {
    const image = imageRef.current
    const stage = image?.parentElement
    if (!image || !stage) return { x: 0, y: 0 }
    const maxX = Math.max(0, (image.offsetWidth * scale - stage.clientWidth) / 2)
    const maxY = Math.max(0, (image.offsetHeight * scale - stage.clientHeight) / 2)
    return { x: Math.max(-maxX, Math.min(maxX, offset.x)), y: Math.max(-maxY, Math.min(maxY, offset.y)) }
  }
  const setImageZoom = (value: number) => {
    const nextZoom = clampZoom(value)
    onZoom(nextZoom)
    setImageOffset((current) => clampOffset(current, nextZoom))
  }
  const pointerDistance = (points: Array<{ x: number; y: number }>) => Math.hypot(points[0].x - points[1].x, points[0].y - points[1].y)
  const handleImagePointerDown = (event: React.PointerEvent<HTMLImageElement>) => {
    event.currentTarget.setPointerCapture(event.pointerId)
    pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
    if (pointers.current.size === 2) {
      pinchStart.current = { distance: pointerDistance(Array.from(pointers.current.values())), zoom }
      panStart.current = null
    } else if (zoom > 1) panStart.current = { x: event.clientX, y: event.clientY, left: imageOffset.x, top: imageOffset.y }
    setManipulatingImage(true)
  }
  const handleImagePointerMove = (event: React.PointerEvent<HTMLImageElement>) => {
    if (!pointers.current.has(event.pointerId)) return
    pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
    if (pointers.current.size >= 2 && pinchStart.current) {
      const distance = pointerDistance(Array.from(pointers.current.values()))
      setImageZoom(pinchStart.current.zoom * distance / Math.max(1, pinchStart.current.distance))
    } else if (pointers.current.size === 1 && panStart.current && zoom > 1) {
      setImageOffset(clampOffset({ x: panStart.current.left + event.clientX - panStart.current.x, y: panStart.current.top + event.clientY - panStart.current.y }, zoom))
    }
  }
  const finishImagePointer = (event: React.PointerEvent<HTMLImageElement>) => {
    pointers.current.delete(event.pointerId)
    pinchStart.current = null
    const remaining = Array.from(pointers.current.values())[0]
    panStart.current = remaining && zoom > 1 ? { x: remaining.x, y: remaining.y, left: imageOffset.x, top: imageOffset.y } : null
    if (pointers.current.size === 0) setManipulatingImage(false)
  }
  return <div ref={overlayRef} className="preview-overlay" role="dialog" aria-modal="true" aria-label={`${de.uiVorschauFur} ${entry.name}`} tabIndex={-1} onClick={onClose}><div className="preview-topbar" onClick={(event) => event.stopPropagation()}><button className="preview-close" onClick={onClose} aria-label={de.close}><X /></button><div className="preview-file-title"><strong>{entry.name}</strong><span>{entry.extension.toUpperCase()} · {formatBytes(entry.size)}</span></div><button className="preview-more" onClick={(event) => onProperties(event.currentTarget)} aria-label={de.properties}><MoreHorizontal /></button></div><div className={`preview-stage preview-${kind}${isPdf ? ' preview-pdf' : ''}`} onClick={(event) => event.stopPropagation()}>
    {kind === 'image' && <><img ref={imageRef} src={source} alt={de.imageHelp(entry.name)} className={manipulatingImage ? 'image-manipulating' : undefined} style={{ transform: `translate3d(${imageOffset.x}px, ${imageOffset.y}px, 0) scale(${zoom})` }} onPointerDown={handleImagePointerDown} onPointerMove={handleImagePointerMove} onPointerUp={finishImagePointer} onPointerCancel={finishImagePointer} onLostPointerCapture={finishImagePointer} onDoubleClick={() => { setImageOffset({ x: 0, y: 0 }); setImageZoom(zoom === 1 ? 2 : 1) }} /><div className="zoom-controls"><button onClick={() => setImageZoom(zoom - 0.25)} aria-label={de.uiVerkleinern}>−</button><span>{Math.round(zoom * 100)}%</span><button onClick={() => setImageZoom(zoom + 0.25)} aria-label={de.uiVergrossern}>+</button></div></>}
    {kind === 'video' && <video src={source} controls playsInline preload="metadata" aria-label={entry.name} />}
    {kind === 'audio' && <div className="audio-preview"><span className="audio-disc"><AudioLines size={46} /></span><strong>{entry.name}</strong><span>{formatBytes(entry.size)}</span><audio src={source} controls preload="metadata" aria-label={entry.name} /></div>}
    {isPdf && <PdfPreviewBoundary source={source}><Suspense fallback={<div role="status">{de.pdfLoading}</div>}><PdfViewer source={source} name={entry.name} /></Suspense></PdfPreviewBoundary>}
    {kind === 'code' && (text === null || (isMarkdown && markdownLoading) ? <div className="code-preview markdown-loading" role="status" aria-label={de.loading}><LoaderCircle size={22} className="spin" /></div> : isMarkdown ? <article className="markdown-preview" dangerouslySetInnerHTML={{ __html: markdownContent }} /> : <div className="code-preview"><pre><code className="hljs" dangerouslySetInnerHTML={{ __html: highlighted }} /></pre></div>)}
    {kind === 'document' && !isPdf && <div className="unsupported-preview"><FileText size={42} /><strong>{de.uiVorschauFur}{' '}{entry.extension.toUpperCase()} {' '}{de.uiIstNichtVerfugbar}</strong><span>{de.uiLadeDieDateiHerunterUmSieInEinerPassendenAppZuOffnen}</span><button className="primary-button" onClick={onDownload}><Download size={16} /> {' '}{de.uiDateiLaden}</button></div>}
    {kind === 'archive' && <div className="unsupported-preview"><FileArchive size={42} /><strong>{de.uiArchivdatei}</strong><span>{de.uiEigenschaftenAnzeigenOderDateiLaden}</span><button className="primary-button" onClick={onDownload}><Download size={16} /> {' '}{de.uiArchivLaden}</button></div>}
    {kind === 'file' && <div className="unsupported-preview"><File size={42} /><strong>{de.uiKeineVorschauVerfugbar}</strong><span>{de.uiDuKannstDieDateiHerunterladenUndAufDemIPhoneOffnen}</span><button className="primary-button" onClick={onDownload}><Download size={16} /> {' '}{de.uiDateiLaden}</button></div>}
    {error && <div className="preview-error"><CircleAlert size={17} />{error}</div>}
  </div><div className="preview-bottom" onClick={(event) => event.stopPropagation()}><button onClick={onDownload}><Download size={17} /><span>{de.download}</span></button>{kind === 'code' && <button onClick={onEdit}><PenLine size={17} /><span>{de.uiBearbeiten}</span></button>}<button onClick={(event) => onProperties(event.currentTarget)}><Info size={17} /><span>{de.details}</span></button><span className="preview-safe-label"><LockKeyhole size={13} /> {' '}{de.uiPrivateVerbindung}</span></div></div>
}

function escapeHtml(value: string) { return value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#039;') }

function formatDuration(seconds: number) {
  const rounded = Math.max(0, Math.floor(seconds))
  const hours = Math.floor(rounded / 3600)
  const minutes = Math.floor((rounded % 3600) / 60)
  const remainder = rounded % 60
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${String(remainder).padStart(2, '0')}` : `${minutes}:${String(remainder).padStart(2, '0')}`
}

function PropertiesSheet({ entry, details, loading, error, returnFocusTarget, onClose, onHash, onAttribute }: { entry: FileEntry; details: FileProperties | null; loading: boolean; error: string; returnFocusTarget: HTMLElement | null; onClose: () => void; onHash: (algorithm: 'sha256' | 'md5') => void; onAttribute: (change: { readOnly?: boolean; hidden?: boolean; archive?: boolean }) => void }) {
  const dialogRef = useModalDialogFocus<HTMLElement>(onClose, returnFocusTarget)
  const date = (value?: string) => value ? new Intl.DateTimeFormat(de.locale, { dateStyle: 'long', timeStyle: 'short' }).format(new Date(value)) : '—'
  const rows: Array<[string, string]> = [
    [de.type, entry.isDirectory ? de.uiOrdner : entry.extension ? de.extensionFileType(entry.extension) : de.uiDatei],
    [de.size, entry.isDirectory && entry.sizeIncomplete != null && entry.size !== null ? `${entry.sizeIncomplete ? 'mind. ' : ''}${formatBytes(entry.size)}${entry.sizeIncomplete ? ' (unvollständig)' : ''}` : details?.size === null || details?.size === undefined ? (entry.isDirectory ? (loading ? de.uiWirdBerechnet : '—') : formatBytes(entry.size)) : formatBytes(details.size)],
    [de.uiErstellt, date(details?.created ?? entry.created)], [de.uiGeandert, date(details?.modified ?? entry.modified)],
    [de.uiZugriff, date(details?.accessed)], [de.uiBesitzer, details?.owner ?? de.uiNichtVerfugbar],
    [de.uiOneDrive, details?.oneDriveAvailability ?? de.uiKeinCloudStatusErkannt],
    [de.uiDateiversion, details?.fileVersion ?? '—'], [de.uiFirma, details?.company ?? '—'],
    ...(details?.photoTaken ? [[de.uiAufgenommen, date(details.photoTaken)] as [string, string]] : []),
    ...(details?.imageDimensions ? [[de.uiBildgrosse, details.imageDimensions] as [string, string]] : []),
    ...((details?.cameraManufacturer || details?.cameraModel) ? [[de.uiKamera, [details.cameraManufacturer, details.cameraModel].filter(Boolean).join(' ')] as [string, string]] : []),
    ...(details?.mediaDurationSeconds ? [[de.uiMedienlange, formatDuration(details.mediaDurationSeconds)] as [string, string]] : []),
    [de.uiAttribute, details?.attributes.join(' · ') ?? ([entry.isReadOnly && de.readOnly, entry.isHidden && de.uiVersteckt, entry.isLink && de.uiVerknupfung].filter(Boolean).join(' · ') || de.uiStandard)],
    ...(details?.hash ? [[de.hashLabel(details.hashAlgorithm ?? ''), details.hash] as [string, string]] : []),
    [de.uiPfad, entry.path],
  ]
  return <div className="modal-backdrop sheet-backdrop" onClick={onClose}><section ref={dialogRef} className="properties-sheet" role="dialog" aria-modal="true" aria-label={de.properties} tabIndex={-1} onClick={(event) => event.stopPropagation()}><div className="sheet-grabber" /><div className="dialog-heading"><span className="sheet-icon"><EntryGlyph entry={entry} /></span><div><h2>{de.properties}</h2><p>{entry.name}</p></div><button className="icon-button" onClick={onClose} aria-label={de.close}><X /></button></div>{loading && <div className="property-loading"><LoaderCircle className="spin" size={15} /> {' '}{de.uiEigenschaftenWerdenGelesen}</div>}{error && <div className="property-error">{error}</div>}<div className="property-list">{rows.map(([label, value]) => <div className="property-row" key={label}><span>{label}</span><strong>{value}</strong></div>)}</div><div className="property-toggles"><span>{de.uiAttributeAndern}</span><label><span>{de.readOnly}</span><input type="checkbox" checked={details?.attributes.includes(de.readOnly) ?? entry.isReadOnly} onChange={(event) => onAttribute({ readOnly: event.target.checked })} /></label><label><span>{de.uiVersteckt}</span><input type="checkbox" checked={details?.attributes.includes(de.uiVersteckt) ?? entry.isHidden} onChange={(event) => onAttribute({ hidden: event.target.checked })} /></label><label><span>{de.uiArchiv}</span><input type="checkbox" checked={details?.attributes.includes(de.uiArchiv) ?? false} onChange={(event) => onAttribute({ archive: event.target.checked })} /></label></div><div className="hash-actions"><button className="secondary-button" disabled={entry.isDirectory || loading} onClick={() => onHash('sha256')}>{de.uiSHA256Berechnen}</button><button className="secondary-button" disabled={entry.isDirectory || loading} onClick={() => onHash('md5')}>{de.uiMD5Berechnen}</button></div>{details?.permissions.length ? <details className="permission-details"><summary>{de.uiNTFSBerechtigungen}{details.permissions.length})</summary>{details.permissions.map((permission) => <p key={permission}>{permission}</p>)}</details> : null}<button className="secondary-button full-width" onClick={() => void navigator.clipboard.writeText(entry.path)}><Copy size={15} /> {' '}{de.uiPfadKopieren}</button></section></div>
}

function SettingsSheet({
  showHidden, setShowHidden, showSystem, setShowSystem, showExtensions, setShowExtensions,
  theme, setTheme, textSize, setTextSize, shareSettings, loading, saving, error, drives, knownFolders,
  onSave, onDraftChange, onClose, onPath, onRefresh,
}: {
  showHidden: boolean; setShowHidden: (value: boolean) => void
  showSystem: boolean; setShowSystem: (value: boolean) => void
  showExtensions: boolean; setShowExtensions: (value: boolean) => void
  theme: 'light' | 'dark'; setTheme: (value: 'light' | 'dark') => void
  textSize: number; setTextSize: (value: number) => void
  shareSettings: ExplorerShareSettings | null; loading: boolean; saving: boolean; error: string
  drives: Drive[]; knownFolders: KnownFolder[]
  onSave: (settings: ExplorerShareSettings) => Promise<void>
  onDraftChange: (settings: ExplorerShareSettings) => void
  onClose: () => void; onPath: () => void; onRefresh: () => void
}) {
  const dialogRef = useModalDialogFocus<HTMLElement>(onClose)
  const [rootPath, setRootPath] = useState('')
  const [rootReadOnly, setRootReadOnly] = useState(false)

  const draft = shareSettings
  const rootChoices = [
    ...drives.filter((drive) => drive.ready).map((drive) => ({ path: drive.name, label: drive.displayName })),
    ...knownFolders.filter((folder) => folder.available).map((folder) => ({ path: folder.path, label: folder.name })),
  ]
  const setShareValue = (change: Partial<ExplorerShareSettings>) => {
    if (shareSettings) onDraftChange({ ...shareSettings, ...change })
  }
  const updateRoot = (index: number, change: Partial<ShareRootSetting>) => {
    if (shareSettings) onDraftChange({
      ...shareSettings,
      roots: shareSettings.roots.map((root, rootIndex) => rootIndex === index ? { ...root, ...change } : root),
    })
  }
  const addRoot = () => {
    const path = rootPath.trim()
    if (!shareSettings || !path || shareSettings.roots.some((root) => root.path.toLocaleLowerCase() === path.toLocaleLowerCase())) return
    const choice = rootChoices.find((item) => item.path.toLocaleLowerCase() === path.toLocaleLowerCase())
    onDraftChange({
      ...shareSettings,
      roots: [...shareSettings.roots, { path, readOnly: rootReadOnly, label: choice?.label ?? null }],
    })
    setRootPath('')
    setRootReadOnly(false)
  }
  const removeRoot = (index: number) => {
    if (!shareSettings) return
    onDraftChange({ ...shareSettings, roots: shareSettings.roots.filter((_, rootIndex) => rootIndex !== index) })
  }

  return <div className="modal-backdrop sheet-backdrop" onClick={onClose}>
    <section ref={dialogRef} className="settings-sheet" role="dialog" aria-modal="true" aria-label={de.uiEinstellungen} tabIndex={-1} onClick={(event) => event.stopPropagation()}>
      <div className="sheet-grabber" />
      <div className="dialog-heading">
        <span className="sheet-icon"><Settings2 size={20} /></span>
        <div><h2>{de.uiEinstellungen}</h2><p>{de.uiDieWichtigstenAnzeigeoptionen}</p></div>
        <button className="icon-button" onClick={onClose} aria-label={de.close}><X /></button>
      </div>
      <div className="settings-toggle-row"><span><strong>{de.hidden}</strong><small>{de.uiWindowsDateienMitAttributVerstecktAnzeigen}</small></span><button role="switch" aria-label={de.hidden} aria-checked={showHidden} className={`switch${showHidden ? ' checked' : ''}`} onClick={() => setShowHidden(!showHidden)}><i /></button></div>
      <div className="settings-toggle-row"><span><strong>{de.uiSystemdateien}</strong><small>{de.uiDateienMitDemWindowsAttributSystemAnzeigen}</small></span><button role="switch" aria-label={de.uiSystemdateien} aria-checked={showSystem} className={`switch${showSystem ? ' checked' : ''}`} onClick={() => setShowSystem(!showSystem)}><i /></button></div>
      <div className="settings-toggle-row"><span><strong>{de.uiDateiendungen}</strong><small>{showExtensions ? de.uiWerdenAngezeigt : de.uiWerdenBeiDateienAusgeblendet}</small></span><button role="switch" aria-label={de.uiDateiendungen} aria-checked={showExtensions} className={`switch${showExtensions ? ' checked' : ''}`} onClick={() => setShowExtensions(!showExtensions)}><i /></button></div>
      <div className="settings-toggle-row"><span><strong>{de.uiDarstellung}</strong><small>{theme === 'dark' ? de.uiDunkel : de.uiHell} {' '}{de.uiAntippenZumWechseln}</small></span><button className="theme-toggle" onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')} aria-label={de.uiDarstellungWechseln}>{theme === 'dark' ? de.uiDunkel : de.uiHell}<ChevronDown size={14} /></button></div>
      <div className="settings-font-size-row"><span><strong>{de.uiTextgroesse}</strong><small>{de.uiTextgroesseBeschreibung}</small></span><select aria-label={de.uiTextgroesse} value={textSize} onChange={(event) => setTextSize(Number(event.target.value))}>{TEXT_SIZE_PERCENTAGES.map((percentage) => <option key={percentage} value={percentage}>{de.uiTextgroesseProzent(percentage)}</option>)}</select></div>

      <section className="share-settings-section" aria-labelledby="share-settings-heading">
        <h3 id="share-settings-heading">{de.uiFreigabeEinstellungen}</h3>
        <p className="share-settings-intro">{draft?.canEdit ? de.uiFreigabenWirkenSofortUndBleibenAufDemPCGespeichert : de.uiFreigabenLokalAendern}</p>
        {loading && <div className="loading-panel" role="status"><LoaderCircle className="spin" size={18} />{' '}{de.uiFreigabeEinstellungenWerdenGeladen}</div>}
        {error && <div className="property-error" role="alert">{error}</div>}
        {!loading && draft && !draft.canEdit && <ul className="share-root-list">
          <li>{draft.shareAllDrives ? de.uiAlleLaufwerkeFreigeben : de.uiFreigegebeneOrdner}{draft.readOnlyMode ? ' · ' + de.readOnly : ''}</li>
          {draft.roots.map((root) => <li key={root.path}>{root.label || root.path}{root.readOnly ? ' · ' + de.readOnly : ''}</li>)}
        </ul>}
        {!loading && draft?.canEdit && <>
          <div className="settings-toggle-row"><span><strong>{de.uiAlleLaufwerkeFreigeben}</strong><small>{de.uiJedesBereiteLaufwerkIstSichtbar}</small></span><button role="switch" aria-label={de.uiAlleLaufwerkeFreigeben} aria-checked={draft.shareAllDrives} disabled={saving || (!draft.shareAllDrives && draft.roots.length === 0)} className={`switch${draft.shareAllDrives ? ' checked' : ''}`} onClick={() => setShareValue({ shareAllDrives: !draft.shareAllDrives })}><i /></button></div>
          <div className="settings-toggle-row"><span><strong>{de.uiAlleFreigabenNurLesen}</strong><small>{de.uiDateienAufFreigabenNichtAndern}</small></span><button role="switch" aria-label={de.uiAlleFreigabenNurLesen} aria-checked={draft.readOnlyMode} disabled={saving} className={`switch${draft.readOnlyMode ? ' checked' : ''}`} onClick={() => setShareValue({ readOnlyMode: !draft.readOnlyMode })}><i /></button></div>
          <div className="settings-toggle-row"><span><strong>{de.uiSystemordnerSchreibgeschuetztHalten}</strong><small>{de.uiWindowsUndProgramFilesSindStandardmaessigGesperrt}</small></span><button role="switch" aria-label={de.uiSystemordnerSchreibgeschuetztHalten} aria-checked={draft.protectSystemFolders} disabled={saving} className={`switch${draft.protectSystemFolders ? ' checked' : ''}`} onClick={() => setShareValue({ protectSystemFolders: !draft.protectSystemFolders })}><i /></button></div>
          {!draft.protectSystemFolders && <p className="share-settings-warning" role="note">{de.uiSystemordnerschutzWarnung}</p>}

          <div className="share-roots-heading"><h4>{de.uiFreigegebeneOrdner}</h4><span>{draft.roots.length}</span></div>
          {draft.roots.length ? <div className="share-root-list">{draft.roots.map((root, index) => <div className="share-root-row" key={root.path.toLocaleLowerCase()}>
            <span className="share-root-copy"><strong>{root.label || root.path}</strong><small>{root.path}</small></span>
            <label className="share-root-readonly"><input type="checkbox" checked={root.readOnly} disabled={saving} aria-label={de.uiFreigabeNurLesen(root.path)} onChange={(event) => updateRoot(index, { readOnly: event.target.checked })} /><span>{de.readOnly}</span></label>
            <button className="icon-button share-root-remove" disabled={saving || (!draft.shareAllDrives && draft.roots.length === 1)} aria-label={de.uiFreigabeEntfernen(root.path)} onClick={() => removeRoot(index)}><X size={17} /></button>
          </div>)}</div> : <p className="share-roots-empty">{de.uiNochKeineEinzelnenFreigaben}</p>}
          <label className="path-input-label share-root-input"><span>{de.uiOrdnerpfad}</span><input list="share-root-options" value={rootPath} onChange={(event) => setRootPath(event.target.value)} placeholder={de.uiWindowsPfadEingebenOderAuswaehlen} autoCapitalize="off" autoCorrect="off" spellCheck={false} disabled={saving} /><datalist id="share-root-options">{rootChoices.map((item) => <option key={item.path} value={item.path}>{item.label}</option>)}</datalist></label>
          <label className="share-root-add-readonly"><input type="checkbox" checked={rootReadOnly} onChange={(event) => setRootReadOnly(event.target.checked)} disabled={saving} /><span>{de.uiDiesenOrdnerSchreibgeschuetztFreigeben}</span></label>
          <button className="secondary-button full-width" onClick={addRoot} disabled={saving || !rootPath.trim() || draft.roots.some((root) => root.path.toLocaleLowerCase() === rootPath.trim().toLocaleLowerCase())}>{de.uiOrdnerFreigeben}</button>
          <button className="primary-button full-width share-settings-save" onClick={() => void onSave(draft)} disabled={saving || loading}><span>{saving ? de.uiWirdGespeichert : de.save}</span>{saving ? <LoaderCircle className="spin" size={16} /> : <Check size={16} />}</button>
        </>}
      </section>

      <button className="settings-row sheet-settings-row" onClick={onPath}><span className="settings-row-icon"><FolderOpen size={18} /></span><span><strong>{de.uiPfadOffnen}</strong><small>{de.uiDirektZuEinemWindowsOrdner}</small></span><ChevronRight /></button>
      <button className="settings-row sheet-settings-row" onClick={onRefresh}><span className="settings-row-icon"><RefreshCw size={18} /></span><span><strong>{de.uiVerbindungAktualisieren}</strong><small>{de.uiOrdnerinhaltNeuLaden}</small></span><ChevronRight /></button>
      <div className="sheet-privacy"><LockKeyhole size={15} /><span>{de.uiDeineDateienBleibenAufDemWindowsPC}</span></div>
    </section>
  </div>
}
function FileTemplateDialog({ name, template, onNameChange, onTemplateChange, onClose, onCreate }: { name: string; template: FileTemplate; onNameChange: (name: string) => void; onTemplateChange: (template: FileTemplate) => void; onClose: () => void; onCreate: (name: string, template: FileTemplate) => void }) {
  const dialogRef = useModalDialogFocus<HTMLFormElement>(onClose)
  return <div className="modal-backdrop" onClick={onClose}><form ref={dialogRef} className="path-dialog file-template-dialog" role="dialog" aria-modal="true" aria-labelledby="new-file-title" tabIndex={-1} onClick={(event) => event.stopPropagation()} onSubmit={(event) => { event.preventDefault(); if (name.trim()) onCreate(name.trim(), template) }}><div className="dialog-heading"><span className="sheet-icon"><FilePlus2 size={20} /></span><div><h2 id="new-file-title">{de.uiNeueDateiErstellen}</h2><p>{de.uiWahleEinenDateitypUndNamen}</p></div><button type="button" className="icon-button" aria-label={de.close} onClick={onClose}><X /></button></div><label className="path-input-label"><span>{de.uiDateiname}</span><input autoFocus data-dialog-initial-focus required value={name} onChange={(event) => onNameChange(event.target.value)} autoCapitalize="off" autoCorrect="off" spellCheck={false} /></label><label className="path-input-label"><span>{de.uiVorlage}</span><select aria-label={de.uiVorlage} value={template} onChange={(event) => onTemplateChange(event.target.value as FileTemplate)}>{Object.entries(FILE_TEMPLATES).map(([value, option]) => <option key={value} value={value}>{option.label} ({option.extension})</option>)}</select></label><div className="dialog-actions"><button type="button" className="secondary-button" onClick={onClose}>{de.cancel}</button><button type="submit" className="primary-button">{de.uiErstellen}{' '}<FilePlus2 size={16} /></button></div></form></div>
}

function JobCard({ job, onAction }: { job: JobSnapshot; onAction: (action: 'pause' | 'resume' | 'cancel') => void }) {
  const terminal = ['completed', 'failed', 'cancelled'].includes(job.status)
  const percent = job.totalBytes ? Math.min(100, job.completedBytes / job.totalBytes * 100) : job.totalItems ? Math.min(100, job.completedItems / job.totalItems * 100) : 0
  const label = ({ copy: de.copy, move: de.move, delete: de.uiLoschen, rename: de.rename, 'rename-series': de.uiSerieUmbenennen, zip: de.uiZIPErstellen, unzip: de.uiEntpacken, 'new-folder': de.uiNeuerOrdner, 'new-file': de.uiNeueDatei, 'undo-batch': de.uiRuckgangig } as Record<string, string>)[job.kind] ?? de.uiDateivorgang
  const progress = job.completedBytes || job.totalBytes
    ? de.jobByteProgress(formatBytes(job.completedBytes), job.totalBytes ? formatBytes(job.totalBytes) : undefined)
    : de.jobItemProgress(job.completedItems, job.totalItems || undefined)
  const speed = job.bytesPerSecond ? de.transferRate(formatBytes(job.bytesPerSecond)) : `${Math.round(percent)} %`
  const remaining = job.remainingSeconds && job.remainingSeconds < 86400 ? de.remainingMinutes(Math.ceil(job.remainingSeconds / 60)) : ''
  const status = job.status === 'running' ? de.uiLauft : job.status === 'queued' ? de.uiWartet : job.status === 'paused' ? de.uiPausiert : job.status === 'conflict' ? de.uiKonflikt : job.status === 'failed' ? de.uiFehler : job.status === 'cancelled' ? de.uiAbgebrochen : de.done
  return <article className={`job-card job-${job.status}`}>
    <div className="job-card-head"><span className="job-icon"><Folder size={17} /></span><span className="job-title"><strong>{label}</strong><small>{job.message}</small></span><span className={`job-status status-${job.status}`}>{status}</span></div>
    {job.error && <p className="job-error">{job.error}</p>}
    <div className="job-progress"><span style={{ width: `${percent}%` }} /></div>
    <div className="job-meta"><span>{progress}</span><span>{speed}{remaining}</span></div>
    {!terminal && <div className="job-actions">{job.status === 'paused' ? <button onClick={() => onAction('resume')}><Play size={15} /> {' '}{de.uiFortsetzen}</button> : job.status === 'running' && <button onClick={() => onAction('pause')}><Pause size={15} /> {' '}{de.uiPausieren}</button>}<button onClick={() => onAction('cancel')}><Square size={14} /> {' '}{de.cancel}</button></div>}
  </article>
}
function RecycleRow({ item, onRestore, onDelete }: { item: RecycleBinEntry; onRestore: () => void; onDelete: () => void }) {
  return <article className="recycle-row"><span className="recycle-icon"><Trash2 size={18} /></span><span className="recycle-copy"><strong>{item.name}</strong><small>{item.originalPath}</small><small>{item.size === null ? (item.isDirectory ? de.uiOrdner : de.uiGrosseUnbekannt) : formatBytes(item.size)}{item.deletedAt ? de.deletedAt(formatDate(item.deletedAt)) : ''}</small></span><span className="recycle-row-actions"><button aria-label={de.restoreItem(item.name)} onClick={onRestore}><Undo2 size={17} /></button><button aria-label={de.permanentDeleteItem(item.name)} onClick={onDelete}><X size={17} /></button></span></article>
}
function FolderPicker({ kind, path, drives, knownFolders, folders, loading, onClose, onSelectPath, onConfirm }: { kind: 'move' | 'zip' | 'unzip' | 'upload'; path: string | null; drives: Drive[]; knownFolders: KnownFolder[]; folders: FileEntry[]; loading: boolean; onClose: () => void; onSelectPath: (path: string | null) => void; onConfirm: () => void }) {
  const dialogRef = useModalDialogFocus<HTMLElement>(onClose)
  const title = kind === 'move' ? de.uiZielordnerAuswahlen : kind === 'zip' ? de.uiZIPSpeichernIn : kind === 'unzip' ? de.uiHierEntpacken : de.uiDateienHochladenNach
  const parent = path ? parentPath(path) : null
  return <div className="modal-backdrop" onClick={onClose}><section ref={dialogRef} className="folder-picker" onClick={(event) => event.stopPropagation()} role="dialog" aria-modal="true" aria-label={title} tabIndex={-1}><div className="dialog-heading"><span className="sheet-icon"><FolderOpen size={20} /></span><div><h2>{title}</h2><p>{path ?? de.thisPc}</p></div><button className="icon-button" onClick={onClose} aria-label={de.close}><X /></button></div><div className="picker-navigation"><button className="secondary-button" onClick={() => onSelectPath(null)}><HardDrive size={15} /> {' '}{de.thisPc}</button><button className="secondary-button" disabled={!parent} onClick={() => onSelectPath(parent)}><ChevronUp size={15} /> {' '}{de.up}</button></div><div className="picker-list">{path === null ? <>{knownFolders.filter((folder) => folder.available).map((folder) => <button key={folder.id} className="picker-row" onClick={() => onSelectPath(folder.path)}><span className="settings-row-icon"><Folder size={18} /></span><span><strong>{folder.name}</strong><small>{folder.path}</small></span><ChevronRight size={17} /></button>)}{drives.filter((drive) => drive.ready).map((drive) => <button key={drive.name} className="picker-row" onClick={() => onSelectPath(drive.name)}><span className="settings-row-icon"><HardDrive size={18} /></span><span><strong>{drive.displayName}</strong><small>{drive.name}</small></span><ChevronRight size={17} /></button>)}</> : loading ? <div className="loading-panel"><LoaderCircle className="spin" size={18} /> {' '}{de.uiOrdnerWerdenGeladen}</div> : folders.length === 0 ? <div className="empty-inline">{de.uiKeineUnterordnerVorhanden}</div> : folders.map((folder) => <button key={folder.path} className="picker-row" onClick={() => onSelectPath(folder.path)}><span className="settings-row-icon"><Folder size={18} /></span><span><strong>{folder.name}</strong><small>{folder.path}</small></span><ChevronRight size={17} /></button>)}</div><div className="dialog-actions"><button type="button" className="secondary-button" onClick={onClose}>{de.cancel}</button><button type="button" className="primary-button" disabled={!path || loading} onClick={onConfirm}>{de.uiDiesenOrdnerVerwenden}{' '}<ArrowRight size={16} /></button></div></section></div>
}

function UploadRow({ session, uploading, reconnecting, onResume, onCancel }: { session: UploadSession; uploading: boolean; reconnecting: boolean; onResume: () => void; onCancel: () => void }) {
  const progress = session.size ? Math.min(100, session.offset / session.size * 100) : session.state === 'ready' ? 100 : 0
  return <article className="job-card upload-card"><div className="job-card-head"><span className="job-icon"><Upload size={17} /></span><span className="job-title"><strong>{session.fileName}</strong><small>{session.destination}</small></span><span className="job-status" role="status" aria-live="polite">{reconnecting ? de.reconnecting : uploading ? de.uiLadt : session.state === 'ready' ? de.uiBereit : de.uiFortsetzbar}</span></div><div className="job-progress" role="progressbar" aria-label={de.uiUploadFortschritt(session.fileName)} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(progress)}><span style={{ width: `${progress}%` }} /></div><div className="job-meta"><span>{formatBytes(session.offset)} {' '}{de.uiVon}{' '}{formatBytes(session.size)}</span><span>{Math.round(progress)} %</span></div><div className="job-actions"><button disabled={uploading} onClick={onResume}><Upload size={15} />{session.state === 'ready' ? de.uiAbschliessen : de.uiFortsetzen}</button><button onClick={onCancel}><X size={14} />{de.cancel}</button></div></article>
}

function UploadConflictDialog({ session, onResolve, onCancel }: { session: UploadSession; onResolve: (choice: 'replace' | 'skip' | 'keep-both') => void; onCancel: () => void }) {
  const dialogRef = useModalDialogFocus<HTMLElement>(onCancel)
  return <div className="modal-backdrop"><section ref={dialogRef} className="path-dialog conflict-dialog" role="dialog" aria-modal="true" aria-label={de.uiUploadkonflikt} tabIndex={-1}><div className="dialog-heading"><span className="sheet-icon"><CircleAlert size={20} /></span><div><h2>{de.uiDateiBereitsVorhanden}</h2><p>„{session.fileName}{de.uiIstImZielordnerSchonVorhanden}</p></div></div><p className="upload-conflict-destination">{session.destination}</p><div className="conflict-actions"><button className="secondary-button" onClick={onCancel}>{de.uiSpaterEntscheiden}</button><button className="secondary-button" onClick={() => onResolve('skip')}>{de.uiUberspringen}</button><button className="secondary-button" onClick={() => onResolve('keep-both')}>{de.uiBeideBehalten}</button><button className="primary-button" onClick={() => onResolve('replace')}>{de.uiErsetzen}</button></div></section></div>
}

function ConflictDialog({ job, onClose, onResolve }: { job: JobSnapshot; onClose: () => void; onResolve: (choice: string, applyToAll: boolean) => void }) {
  const dialogRef = useModalDialogFocus<HTMLElement>(onClose)
  const [applyToAll, setApplyToAll] = useState(false)
  return <div className="modal-backdrop"><section ref={dialogRef} className="path-dialog conflict-dialog" role="dialog" aria-modal="true" aria-label={de.uiDateikonflikt} tabIndex={-1}><div className="dialog-heading"><span className="sheet-icon"><CircleAlert size={20} /></span><div><h2>{de.uiDateiBereitsVorhanden}</h2><p>„{job.conflict?.destinationName}{de.uiIstImZielordnerSchonVorhanden}</p></div></div><div className="conflict-names"><span>{job.conflict?.sourceName}</span><span>→</span><span>{job.conflict?.destinationPath}</span></div><label className="conflict-all"><input type="checkbox" checked={applyToAll} onChange={(event) => setApplyToAll(event.target.checked)} /> {' '}{de.uiFurAlleWeiterenKonflikteUbernehmen}</label><div className="conflict-actions"><button className="secondary-button" onClick={() => onResolve('skip', applyToAll)}>{de.uiUberspringen}</button><button className="secondary-button" onClick={() => onResolve('keep-both', applyToAll)}>{de.uiBeideBehalten}</button><button className="primary-button" onClick={() => onResolve('replace', applyToAll)}>{de.uiErsetzen}</button></div></section></div>
}

function TextEditorModal({ document, content, loading, saving, error, onChange, onSave, onClose, onReload }: { document: TextDocument; content: string; loading: boolean; saving: boolean; error: string; onChange: (value: string) => void; onSave: () => void; onClose: () => void; onReload: () => void }) {
  const changed = content !== document.content
  const close = () => { if (changed && !window.confirm(de.uiUngespeicherteAnderungenVerwerfen)) return; onClose() }
  const dialogRef = useModalDialogFocus<HTMLDivElement>(close)
  return <div ref={dialogRef} className="editor-overlay" role="dialog" aria-modal="true" aria-label={`${de.uiTextBearbeiten} ${document.name}`} tabIndex={-1}><header className="editor-topbar"><button className="icon-button" aria-label={de.uiEditorSchliessen} onClick={close}><X /></button><div><strong>{document.name}</strong><small>{changed ? de.uiNichtGespeicherteAnderungen : de.uiUTF8Maximal2MB}</small></div><button className="primary-button editor-save" disabled={!changed || saving || loading} onClick={onSave}>{saving ? <LoaderCircle className="spin" size={16} /> : <Check size={16} />} {' '}{de.save}</button></header>{error && <div className="editor-error" role="alert"><span>{error}</span><button className="secondary-button" onClick={onReload}>{de.uiNeuLaden}</button></div>}{loading && <div className="editor-loading"><LoaderCircle className="spin" size={20} /> {' '}{de.uiDateiWirdGeladen}</div>}<textarea className="editor-textarea" aria-label={de.editorContent(document.name)} value={content} onChange={(event) => onChange(event.target.value)} autoCapitalize="off" autoCorrect="off" spellCheck={false} />{content.length > 1_900_000 && <div className="editor-counter">{new Intl.NumberFormat(de.locale).format(content.length)} {' '}{de.uiZeichenSpeichernBis2MB}</div>}</div>
}

export default App
