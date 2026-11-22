import DOMPurify from 'dompurify'
import { marked, Renderer } from 'marked'
import { explorerApi } from './api'

export function renderMarkdownPreview(source: string, documentPath: string): string {
  const renderer = new Renderer()
  renderer.image = ({ href, title, text: alt }) => {
    const imagePath = resolveRelativeImagePath(documentPath, href)
    if (!imagePath) return alt ? `<span>${escapeHtml(alt)}</span>` : ''
    const titleAttribute = title ? ` title="${escapeHtml(title)}"` : ''
    return `<img src="${escapeHtml(explorerApi.streamPathUrl(imagePath))}" alt="${escapeHtml(alt)}"${titleAttribute} loading="lazy">`
  }

  const rendered = marked.parse(source, { renderer, async: false })
  const sanitized = DOMPurify.sanitize(rendered, {
    USE_PROFILES: { html: true },
    FORBID_ATTR: ['style', 'srcset'],
    FORBID_TAGS: ['audio', 'button', 'embed', 'form', 'iframe', 'input', 'object', 'select', 'source', 'style', 'textarea', 'track', 'video'],
  })
  const parsed = new DOMParser().parseFromString(sanitized, 'text/html')

  for (const image of parsed.querySelectorAll<HTMLImageElement>('img')) {
    const imageSource = image.getAttribute('src') ?? ''
    if (!imageSource.startsWith('/api/v1/stream?path=')) {
      const alt = image.getAttribute('alt') ?? ''
      if (alt) image.replaceWith(parsed.createTextNode(alt))
      else image.remove()
    }
  }

  for (const link of parsed.querySelectorAll<HTMLAnchorElement>('a[href]')) {
    try {
      if (new URL(link.href, window.location.href).origin !== window.location.origin) {
        link.target = '_blank'
        link.rel = 'noopener noreferrer'
      }
    } catch {
      link.removeAttribute('href')
    }
  }

  return parsed.body.innerHTML
}

function resolveRelativeImagePath(documentPath: string, href: string): string | null {
  const rawPath = href.split(/[?#]/, 1)[0]
  let decoded: string
  try { decoded = decodeURIComponent(rawPath) }
  catch { decoded = rawPath }

  const relativePath = decoded.replaceAll('\\', '/')
  if (!relativePath || relativePath.includes('\0') || relativePath.startsWith('/') || /^[a-z][a-z\d+.-]*:/i.test(relativePath)) return null

  const windowsPath = documentPath.replaceAll('/', '\\')
  let root: string
  let segments: string[]
  const drive = /^([a-z]:\\)(.*)$/i.exec(windowsPath)
  if (drive) {
    root = drive[1]
    segments = drive[2].split('\\').filter(Boolean)
  } else {
    const network = /^\\\\([^\\]+)\\([^\\]+)(?:\\(.*))?$/.exec(windowsPath)
    if (!network) return null
    root = `\\\\${network[1]}\\${network[2]}\\`
    segments = (network[3] ?? '').split('\\').filter(Boolean)
  }

  segments.pop()
  for (const segment of relativePath.split('/')) {
    if (!segment || segment === '.') continue
    if (segment === '..') {
      if (segments.length === 0) return null
      segments.pop()
      continue
    }
    if (segment.includes(':') || /[<>"|?*]/.test(segment)) return null
    segments.push(segment)
  }

  return segments.length ? `${root}${segments.join('\\')}` : null
}

function escapeHtml(value: string): string {
  return value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#039;')
}
