import hljs from 'highlight.js/lib/core'
import type { LanguageFn } from 'highlight.js'

const extensionLanguages: Record<string, string> = {
  bash: 'bash', sh: 'bash', zsh: 'bash',
  c: 'c', h: 'c',
  cc: 'cpp', cpp: 'cpp', cxx: 'cpp', hpp: 'cpp',
  cs: 'csharp',
  css: 'css',
  dockerfile: 'dockerfile',
  go: 'go',
  htm: 'xml', html: 'xml', svg: 'xml', xml: 'xml',
  java: 'java',
  js: 'javascript', jsx: 'javascript', mjs: 'javascript', cjs: 'javascript',
  json: 'json', jsonc: 'json',
  ps1: 'powershell', psm1: 'powershell', psd1: 'powershell',
  py: 'python', pyw: 'python',
  sql: 'sql',
  ts: 'typescript', tsx: 'typescript', mts: 'typescript', cts: 'typescript',
  yaml: 'yaml', yml: 'yaml',
}

const languageLoaders: Record<string, () => Promise<{ default: LanguageFn }>> = {
  bash: () => import('highlight.js/lib/languages/bash'),
  c: () => import('highlight.js/lib/languages/c'),
  cpp: () => import('highlight.js/lib/languages/cpp'),
  csharp: () => import('highlight.js/lib/languages/csharp'),
  css: () => import('highlight.js/lib/languages/css'),
  dockerfile: () => import('highlight.js/lib/languages/dockerfile'),
  go: () => import('highlight.js/lib/languages/go'),
  xml: () => import('highlight.js/lib/languages/xml'),
  java: () => import('highlight.js/lib/languages/java'),
  javascript: () => import('highlight.js/lib/languages/javascript'),
  json: () => import('highlight.js/lib/languages/json'),
  powershell: () => import('highlight.js/lib/languages/powershell'),
  python: () => import('highlight.js/lib/languages/python'),
  sql: () => import('highlight.js/lib/languages/sql'),
  typescript: () => import('highlight.js/lib/languages/typescript'),
  yaml: () => import('highlight.js/lib/languages/yaml'),
}

const registeredLanguages = new Set<string>()

export async function highlightCode(source: string, extension: string): Promise<string | null> {
  const language = extensionLanguages[extension.toLowerCase().replace(/^\./, '')]
  const loadLanguage = language ? languageLoaders[language] : undefined
  if (!language || !loadLanguage) return null

  if (!registeredLanguages.has(language)) {
    const module = await loadLanguage()
    hljs.registerLanguage(language, module.default)
    registeredLanguages.add(language)
  }

  return hljs.highlight(source, { language, ignoreIllegals: true }).value
}
