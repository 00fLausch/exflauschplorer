export interface Drive {
  name: string
  displayName: string
  driveType: string
  format: string
  totalBytes: number | null
  freeBytes: number | null
  ready: boolean
  readOnly: boolean
}

export interface KnownFolder {
  id: string
  name: string
  path: string
  available: boolean
}

export interface ShareRootSetting {
  path: string
  readOnly: boolean
  label: string | null
}

export interface ExplorerShareSettings {
  shareAllDrives: boolean
  readOnlyMode: boolean
  protectSystemFolders: boolean
  roots: ShareRootSetting[]
}

export interface FileEntry {
  name: string
  path: string
  isDirectory: boolean
  size: number | null
  modified: string
  created: string
  extension: string
  contentType: string
  isHidden: boolean
  isReadOnly: boolean
  isLink: boolean
  isShortcut?: boolean
  sizeIncomplete?: boolean | null
}

export type WaveformResult = { peaks: number[]; duration: number } | { status: 'processing' }

export interface FolderPage {
  path: string
  entries: FileEntry[]
  skip: number
  pageSize: number
  totalCount: number | null
  hasMore: boolean
}

export interface Health {
  ok: boolean
  version: string
  user: string | null
  readOnly: boolean
  shareAllDrives: boolean
}

export interface PinStatus {
  isConfigured: boolean
  isUnlocked: boolean
  retryAfterSeconds: number
}

export interface JobSnapshot {
  id: string
  kind: string
  status: string
  message: string
  completedBytes: number
  totalBytes: number | null
  completedItems: number
  totalItems: number | null
  bytesPerSecond: number | null
  createdAt: string
  error: string | null
  operation: string | null
  conflict: { sourceName: string; destinationName: string; destinationPath: string } | null
  remainingSeconds: number | null
  skippedItems: number
}

export interface OperationRequest {
  kind: string
  sources: string[]
  destination?: string
  newName?: string
  template?: string
  permanent?: boolean
  conflictMode?: string
}

export interface RecycleBinEntry {
  id: string
  name: string
  originalPath: string
  size: number | null
  deletedAt: string | null
  isDirectory: boolean
}

export interface FileProperties {
  path: string
  name: string
  isDirectory: boolean
  size: number | null
  created: string
  modified: string
  accessed: string
  attributes: string[]
  owner: string | null
  permissions: string[]
  fileVersion: string | null
  company: string | null
  oneDriveAvailability: string | null
  photoTaken: string | null
  imageDimensions: string | null
  cameraManufacturer: string | null
  cameraModel: string | null
  mediaDurationSeconds: number | null
  hashAlgorithm: string | null
  hash: string | null
}

export interface TextDocument {
  path: string
  name: string
  content: string
  eTag: string
  modified: string
}

export interface UploadSession {
  id: string
  fileName: string
  destination: string
  size: number
  offset: number
  state: string
  createdAt: string
}

export interface UploadCommitResult {
  completed: boolean
  skipped: boolean
  path: string | null
  sha256: string
}

export interface ApiError {
  message: string
  code?: string
}
