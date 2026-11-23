import { useEffect, useRef, useState } from 'react'
import { Check, ChevronDown, LoaderCircle, RefreshCw, Trash2 } from 'lucide-react'
import { apiRequest, formatBytes } from './api'
import { useModalDialogFocus } from './useModalDialogFocus'

interface CleanItem { id: string; category: string; path: string; bytes: number; lastWriteUtc: string }
interface CleanCategory { id: string; title: string; description: string; exists: boolean; truncated: boolean; items: CleanItem[] }
interface CleanScan { id: string; createdUtc: string; categories: CleanCategory[] }
interface CleanResult { deleted: number; skipped: number; bytesFreed: number }

export function CleanPage() {
  const [scan, setScan] = useState<CleanScan | null>(null)
  const [selected, setSelected] = useState<string[]>([])
  const [expanded, setExpanded] = useState<string[]>([])
  const [loading, setLoading] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [confirm, setConfirm] = useState(false)
  const [error, setError] = useState('')
  const [result, setResult] = useState<CleanResult | null>(null)
  const abort = useRef<AbortController | null>(null)
  useEffect(() => () => abort.current?.abort(), [])
  const items = scan?.categories.flatMap(category => category.items) ?? []
  const selectedSet = new Set(selected)
  const chosen = items.filter(item => selectedSet.has(item.id))
  const total = chosen.reduce((sum, item) => sum + item.bytes, 0)

  const runScan = async () => {
    abort.current?.abort()
    const controller = new AbortController()
    abort.current = controller
    setLoading(true); setError(''); setResult(null); setSelected([]); setExpanded([]); setScan(null)
    try { const next = await apiRequest<CleanScan>('/clean/scan', { signal: controller.signal }); if (!controller.signal.aborted) setScan(next) }
    catch (cause) { if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Suche fehlgeschlagen.') }
    finally { if (!controller.signal.aborted) setLoading(false) }
  }
  const toggleCategory = (category: CleanCategory) => {
    const ids = category.items.map(item => item.id)
    setSelected(current => { const group = new Set(ids); return ids.every(id => current.includes(id)) ? current.filter(id => !group.has(id)) : [...new Set([...current, ...ids])] })
  }
  const remove = async () => {
    if (!scan || selected.length === 0) return
    setDeleting(true); setError('')
    try {
      const next = await apiRequest<CleanResult>('/clean/delete', { method: 'POST', body: JSON.stringify({ scanId: scan.id, itemIds: selected }) })
      setResult(next); setScan(null); setSelected([]); setConfirm(false)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Löschen fehlgeschlagen.'); setConfirm(false) }
    finally { setDeleting(false) }
  }
  return <div className="clean-page">
    <div className="clean-intro"><span className="clean-kicker">SPECIAL CLEAN</span><h1>Aufräumen</h1><p>Finde ältere temporäre Dateien und Caches. Du wählst jede Datei selbst aus.</p></div>
    <button className="primary-button clean-scan-button" onClick={() => void runScan()} disabled={loading || deleting}>{loading ? <LoaderCircle className="spin" size={18} /> : <RefreshCw size={18} />}{loading ? 'Suche läuft …' : scan ? 'Erneut prüfen' : 'Jetzt prüfen'}</button>
    {error && <p className="clean-error" role="alert">{error}</p>}
    {result && <p className="clean-result" role="status">{result.deleted} Dateien gelöscht · {formatBytes(result.bytesFreed)} freigegeben{result.skipped > 0 ? ` · ${result.skipped} veränderte oder gesperrte Dateien übersprungen` : ''}</p>}
    {scan && <>
      <p className="clean-hint">Nur Dateien, die seit mindestens 48 Stunden nicht verändert wurden. Keine Datei ist vorausgewählt.</p>
      {scan.categories.map(category => <section className="clean-category" key={category.id}>
        <div className="clean-category-head"><div><h2>{category.title}</h2><p>{category.description}</p></div><strong>{formatBytes(category.items.reduce((sum, item) => sum + item.bytes, 0))}</strong></div>
        {!category.exists ? <p className="clean-muted">Ordner nicht vorhanden oder nicht freigegeben.</p> : category.items.length === 0 ? <p className="clean-muted">Keine passenden Dateien gefunden.</p> : <>
          <button className="clean-select" onClick={() => toggleCategory(category)}>{category.items.every(item => selectedSet.has(item.id)) ? 'Auswahl dieser Kategorie aufheben' : `Alle ${category.items.length} Dateien auswählen`}</button>
          <details className="clean-items" onToggle={event => { const open = event.currentTarget.open; setExpanded(current => open ? [...new Set([...current, category.id])] : current.filter(id => id !== category.id)) }}><summary>Dateien und Speicherorte anzeigen <ChevronDown size={16} /></summary>
            {expanded.includes(category.id) && category.items.map(item => <label className="clean-item" key={item.id}><input type="checkbox" checked={selectedSet.has(item.id)} onChange={event => setSelected(current => event.target.checked ? [...current, item.id] : current.filter(id => id !== item.id))} /><span><strong>{item.path.split(/[\\/]/).at(-1)}</strong><small>{item.path}</small></span><b>{formatBytes(item.bytes)}</b></label>)}
          </details>
        </>}
        {category.truncated && <p className="clean-muted">Vorschau begrenzt oder einzelne Bereiche nicht lesbar.</p>}
      </section>)}
      <div className="clean-selection"><span>{selected.length} ausgewählt · {formatBytes(total)}</span><button className="primary-button" disabled={selected.length === 0 || deleting} onClick={() => setConfirm(true)}><Trash2 size={17} />Auswahl löschen</button></div>
    </>}
    {confirm && <CleanConfirmation items={chosen} bytes={total} busy={deleting} onCancel={() => setConfirm(false)} onConfirm={() => void remove()} />}
  </div>
}

function CleanConfirmation({ items, bytes, busy, onCancel, onConfirm }: { items: CleanItem[]; bytes: number; busy: boolean; onCancel: () => void; onConfirm: () => void }) {
  const ref = useModalDialogFocus<HTMLDivElement>(onCancel)
  return <div className="modal-backdrop" onClick={onCancel}><div ref={ref} className="clean-confirm" role="dialog" aria-modal="true" aria-labelledby="clean-confirm-title" tabIndex={-1} onClick={event => event.stopPropagation()}>
    <h2 id="clean-confirm-title">Ausgewählte Dateien aufräumen?</h2><p>{items.length} Dateien · {formatBytes(bytes)}. Temporäre Dateien und unvollständige Downloads gehen in den Papierkorb. Cache-Dateien werden endgültig gelöscht.</p>
    <details><summary>Ausgewählte Dateien prüfen</summary><ul>{items.map(item => <li key={item.id}>{item.path}</li>)}</ul></details>
    <div className="dialog-actions"><button className="secondary-button" data-dialog-initial-focus disabled={busy} onClick={onCancel}>Abbrechen</button><button className="primary-button" disabled={busy} onClick={onConfirm}>{busy ? <LoaderCircle className="spin" size={16} /> : <Check size={16} />}Endgültig löschen</button></div>
  </div></div>
}
