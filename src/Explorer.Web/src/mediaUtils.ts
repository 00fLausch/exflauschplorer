import type { FileEntry } from './types'

const audioExtensions = new Set(['mp3', 'wav', 'm4a', 'aac', 'ogg', 'oga', 'opus', 'flac', 'weba', 'aiff', 'aif', 'wma'])
const videoExtensions = new Set(['mp4', 'm4v', 'webm', 'mov', 'ogv', 'mkv', 'avi', 'mpeg', 'mpg', '3gp', 'mts', 'm2ts', 'wmv'])
const nonMediaExtensions = new Set(['ts', 'm3u', 'm3u8', 'pls', 'xspf'])

function extension(entry: FileEntry) {
  return (entry.extension || entry.name.split('.').at(-1) || '').replace(/^\./, '').toLowerCase()
}

export function isVideoMedia(entry: FileEntry) {
  const ext = extension(entry)
  return !entry.isDirectory && !nonMediaExtensions.has(ext) && (entry.contentType.startsWith('video/') || videoExtensions.has(ext))
}

export function isPlayableMedia(entry: FileEntry) {
  const ext = extension(entry)
  return !entry.isDirectory && !nonMediaExtensions.has(ext) && (entry.contentType.startsWith('audio/') || audioExtensions.has(ext) || isVideoMedia(entry))
}

export function formatMediaTime(seconds: number) {
  if (!Number.isFinite(seconds) || seconds < 0) return '0:00'
  const whole = Math.floor(seconds)
  const hours = Math.floor(whole / 3600)
  const minutes = Math.floor((whole % 3600) / 60)
  const rest = String(whole % 60).padStart(2, '0')
  return hours ? `${hours}:${String(minutes).padStart(2, '0')}:${rest}` : `${minutes}:${rest}`
}
