import { useEffect, useRef, useState } from 'react'
import { ChevronLeft, ChevronRight, LoaderCircle, Minus, Plus } from 'lucide-react'
import * as pdfjs from 'pdfjs-dist/legacy/build/pdf.mjs'
import type { PDFDocumentProxy, RenderTask } from 'pdfjs-dist'
import workerUrl from 'pdfjs-dist/legacy/build/pdf.worker.min.mjs?url'
import { de } from './locales/de'
import './PdfViewer.css'

pdfjs.GlobalWorkerOptions.workerSrc = workerUrl

export default function PdfViewer({ source, name }: { source: string; name: string }) {
  const [document, setDocument] = useState<PDFDocumentProxy | null>(null)
  const [pageNumber, setPageNumber] = useState(1)
  const [pageInput, setPageInput] = useState('1')
  const [zoom, setZoom] = useState(1)
  const [width, setWidth] = useState(0)
  const [loading, setLoading] = useState(true)
  const [rendering, setRendering] = useState(false)
  const [error, setError] = useState('')
  const [attempt, setAttempt] = useState(0)
  const [passwordPrompt, setPasswordPrompt] = useState<'required' | 'incorrect' | null>(null)
  const [password, setPassword] = useState('')
  const passwordCallback = useRef<((password: string) => void) | null>(null)
  const scroller = useRef<HTMLDivElement>(null)
  const canvas = useRef<HTMLCanvasElement>(null)
  const renderTask = useRef<RenderTask | null>(null)

  useEffect(() => {
    let cancelled = false
    setDocument(null)
    setLoading(true)
    setError('')
    setPasswordPrompt(null)
    const assets = `/pdfjs/${pdfjs.version}/`
    const task = pdfjs.getDocument({
      url: source,
      withCredentials: true,
      cMapUrl: `${assets}cmaps/`,
      cMapPacked: true,
      standardFontDataUrl: `${assets}standard_fonts/`,
      wasmUrl: `${assets}wasm/`,
      iccUrl: `${assets}iccs/`,
      disableStream: true,
      disableAutoFetch: true,
      canvasMaxAreaInBytes: 64 * 1024 * 1024,
    })
    task.onPassword = (callback: (password: string) => void, reason: number) => {
      if (cancelled) return
      passwordCallback.current = callback
      setPassword('')
      setPasswordPrompt(reason === pdfjs.PasswordResponses.INCORRECT_PASSWORD ? 'incorrect' : 'required')
      setLoading(false)
    }
    void task.promise.then((loaded) => {
      if (cancelled) return
      setDocument(loaded)
      setPageNumber(1)
      setPageInput('1')
      setPasswordPrompt(null)
      setLoading(false)
    }).catch(() => {
      if (!cancelled) { setLoading(false); setError(de.pdfOpenError) }
    })
    return () => {
      cancelled = true
      passwordCallback.current = null
      renderTask.current?.cancel()
      void task.destroy().catch(() => {})
    }
  }, [source, attempt])

  useEffect(() => {
    const element = scroller.current
    if (!element) return
    const update = () => setWidth(Math.max(0, element.clientWidth - 24))
    update()
    const observer = new ResizeObserver(update)
    observer.observe(element)
    return () => observer.disconnect()
  }, [document])

  useEffect(() => {
    if (!document || !width || !canvas.current) return
    let cancelled = false
    const element = canvas.current
    setRendering(true)
    setError('')
    const run = async () => {
      const previous = renderTask.current
      if (previous) {
        previous.cancel()
        try { await previous.promise } catch { /* cancellation is expected */ }
      }
      if (cancelled) return
      const page = await document.getPage(pageNumber)
      if (cancelled) return
      const natural = page.getViewport({ scale: 1 })
      const viewport = page.getViewport({ scale: width / natural.width * zoom })
      const outputScale = Math.min(window.devicePixelRatio || 1, 2, Math.sqrt(12_000_000 / (viewport.width * viewport.height)))
      element.width = Math.max(1, Math.floor(viewport.width * outputScale))
      element.height = Math.max(1, Math.floor(viewport.height * outputScale))
      element.style.width = `${Math.floor(viewport.width)}px`
      element.style.height = `${Math.floor(viewport.height)}px`
      element.removeAttribute('data-rendered-page')
      const task = page.render({ canvas: element, viewport, transform: [outputScale, 0, 0, outputScale, 0, 0] })
      renderTask.current = task
      await task.promise
      if (!cancelled) {
        element.dataset.renderedPage = String(pageNumber)
        setRendering(false)
      }
      if (renderTask.current === task) renderTask.current = null
    }
    void run().catch((cause: unknown) => {
      if (!cancelled && !(cause instanceof Error && cause.name === 'RenderingCancelledException')) {
        setRendering(false)
        setError(de.pdfRenderError)
      }
    })
    return () => { cancelled = true; renderTask.current?.cancel() }
  }, [document, pageNumber, width, zoom])

  const moveTo = (next: number) => {
    if (!document || !Number.isFinite(next)) { setPageInput(String(pageNumber)); return }
    const target = Math.max(1, Math.min(document.numPages, Math.floor(next)))
    setPageNumber(target)
    setPageInput(String(target))
    scroller.current?.scrollTo({ top: 0, left: 0 })
  }

  return <div className="pdf-viewer">
    {document && <div className="pdf-toolbar" aria-label="PDF-Werkzeuge">
      <button type="button" onClick={() => moveTo(pageNumber - 1)} disabled={pageNumber <= 1} aria-label={de.pdfPrevious}><ChevronLeft size={21} /></button>
      <form className="pdf-page-form" onSubmit={(event) => { event.preventDefault(); moveTo(Number(pageInput)) }}>
        <input type="number" inputMode="numeric" min={1} max={document.numPages} aria-label={de.pdfPage} value={pageInput} onChange={(event) => setPageInput(event.target.value)} onBlur={() => moveTo(Number(pageInput))} />
        <span>{de.pdfPageCount(document.numPages)}</span>
      </form>
      <button type="button" onClick={() => moveTo(pageNumber + 1)} disabled={pageNumber >= document.numPages} aria-label={de.pdfNext}><ChevronRight size={21} /></button>
      <span className="pdf-toolbar-spacer" />
      <button type="button" onClick={() => setZoom((value) => Math.max(0.5, value - 0.25))} disabled={zoom <= 0.5} aria-label={de.pdfZoomOut}><Minus size={18} /></button>
      <span className="pdf-zoom-label" aria-live="polite">{Math.round(zoom * 100)}%</span>
      <button type="button" onClick={() => setZoom((value) => Math.min(3, value + 0.25))} disabled={zoom >= 3} aria-label={de.pdfZoomIn}><Plus size={18} /></button>
    </div>}
    {document && <div className="pdf-scroll" ref={scroller} aria-label={`${name}: ${de.pdfPage} ${pageNumber}`}>
      <canvas ref={canvas} className="pdf-page" role="img" aria-label={`${name}, ${de.pdfPage} ${pageNumber} ${de.pdfPageCount(document.numPages)}`} />
      {rendering && <div className="pdf-rendering" role="status"><LoaderCircle size={16} className="spin" /> {de.pdfRendering}</div>}
    </div>}
    {loading && <div className="pdf-message" role="status"><LoaderCircle className="spin" />{de.pdfLoading}</div>}
    {passwordPrompt && <form className="pdf-message pdf-password" onSubmit={(event) => {
      event.preventDefault()
      const callback = passwordCallback.current
      if (!callback) return
      passwordCallback.current = null
      setPasswordPrompt(null)
      setLoading(true)
      callback(password)
      setPassword('')
    }}>
      <strong>{passwordPrompt === 'incorrect' ? de.pdfPasswordWrong : de.pdfPassword}</strong>
      <label htmlFor="pdf-password">{de.pdfPasswordLabel}</label>
      <input id="pdf-password" type="password" autoComplete="off" value={password} onChange={(event) => setPassword(event.target.value)} />
      <button className="primary-button" type="submit">{de.pdfUnlock}</button>
    </form>}
    {error && <div className="pdf-message" role="alert"><span>{error}</span><button className="primary-button" onClick={() => setAttempt((value) => value + 1)}>{de.pdfRetry}</button><a href={source} target="_blank" rel="noopener noreferrer">{de.pdfOpenBrowser}</a></div>}
  </div>
}
