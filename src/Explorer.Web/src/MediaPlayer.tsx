import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import type { KeyboardEvent, PointerEvent } from 'react'
import { AudioLines, ChevronDown, Expand, ListMusic, LoaderCircle, Maximize2, Music2, Pause, PictureInPicture2, Play, Repeat, Repeat1, RotateCcw, RotateCw, Shuffle, SkipBack, SkipForward, X } from 'lucide-react'
import { explorerApi } from './api'
import { de } from './locales/de'
import { formatMediaTime, isVideoMedia } from './mediaUtils'
import type { FileEntry } from './types'
import { useModalDialogFocus } from './useModalDialogFocus'
import './MediaPlayer.css'

export interface MediaPlayerProps {
  queue: FileEntry[]
  startPath: string
  sessionId: number
  onClose: () => void
  onTrackChange?: (entry: FileEntry) => void
  onQueueChange?: (queue: FileEntry[]) => void
}

type RepeatMode = 'off' | 'all' | 'one'
type Waveform = { path: string; duration: number; peaks: number[] }
type WaveDrag = { id: number; x: number; y: number; value: number }
type SafariVideo = HTMLVideoElement & { webkitEnterFullscreen?: () => void }

function mediaError(element: HTMLMediaElement) {
  if (element.error?.code === 2) return de.playerNetworkError
  if (element.error?.code === 3 || element.error?.code === 4) return de.playerFormatError
  return de.playerPlaybackError
}

export function MediaPlayer({ queue, startPath, sessionId, onClose, onTrackChange, onQueueChange }: MediaPlayerProps) {
  const [currentPath, setCurrentPath] = useState(startPath)
  const [expanded, setExpanded] = useState(true)
  const [playing, setPlaying] = useState(false)
  const [loading, setLoading] = useState(true)
  const [buffering, setBuffering] = useState(false)
  const [position, setPosition] = useState(0)
  const [mediaDuration, setMediaDuration] = useState(0)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [showQueue, setShowQueue] = useState(false)
  const [shuffle, setShuffle] = useState(false)
  const [repeat, setRepeat] = useState<RepeatMode>('off')
  const [speed, setSpeed] = useState(1)
  const [waveformData, setWaveform] = useState<Waveform | null>(null)
  const [waveformFailedPath, setWaveformFailedPath] = useState('')
  const [fineScrub, setFineScrub] = useState(1)
  const mediaRef = useRef<HTMLMediaElement | null>(null)
  const panelRef = useRef<HTMLDivElement>(null)
  const expandButtonRef = useRef<HTMLButtonElement>(null)
  const minimizeButtonRef = useRef<HTMLButtonElement>(null)
  const scrubbing = useRef(false)
  const waveDrag = useRef<WaveDrag | null>(null)
  const pendingSeek = useRef<number | null>(null)
  const lastSession = useRef(sessionId)
  const onTrackChangeRef = useRef(onTrackChange)
  const reportedTrackPath = useRef('')
  const titleId = useId()
  const queueId = useId()
  const currentIndex = Math.max(0, queue.findIndex((entry) => entry.path === currentPath))
  const current = queue[currentIndex]
  const path = current?.path ?? ''
  const currentName = current?.name ?? ''
  const waveform = waveformData?.path === path ? waveformData : null
  const waveformFailed = waveformFailedPath === path
  const video = current ? isVideoMedia(current) : false
  const duration = mediaDuration || waveform?.duration || 0
  const shownPosition = Math.min(position, duration || position)
  const repeatLabel = repeat === 'off' ? de.playerRepeatOff : repeat === 'all' ? de.playerRepeatAll : de.playerRepeatOne
  useEffect(() => { onTrackChangeRef.current = onTrackChange }, [onTrackChange])

  const minimize = useCallback(() => {
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {})
    setExpanded(false)
  }, [])
  const dialogRef = useModalDialogFocus<HTMLElement>(minimize)

  const play = useCallback(async () => {
    const element = mediaRef.current
    if (!element) return
    setNotice('')
    try {
      await element.play()
    } catch (reason) {
      if (reason instanceof DOMException && reason.name === 'AbortError') return
      if (reason instanceof DOMException && reason.name === 'NotAllowedError') {
        setNotice(de.playerTapToPlay)
        setLoading(false)
      } else if (!element.error) {
        setError(de.playerPlaybackError)
        setLoading(false)
      }
    }
  }, [])

  const seek = useCallback((value: number) => {
    const element = mediaRef.current
    const next = Math.max(0, Math.min(value, duration || value))
    setPosition(next)
    if (!element) return
    if (element.readyState === 0) { pendingSeek.current = next; return }
    try { element.currentTime = next } catch { pendingSeek.current = next }
  }, [duration])

  const chooseTrack = useCallback((index: number) => {
    const entry = queue[index]
    if (!entry) return
    if (entry.path === path) { seek(0); void play() }
    else setCurrentPath(entry.path)
  }, [queue, path, play, seek])

  const nextTrack = useCallback((automatic = false) => {
    if (!queue.length) return
    if (automatic && repeat === 'one') { seek(0); void play(); return }
    if (shuffle && queue.length > 1) {
      const random = Math.floor(Math.random() * (queue.length - 1))
      chooseTrack(random >= currentIndex ? random + 1 : random)
    } else if (currentIndex + 1 < queue.length) chooseTrack(currentIndex + 1)
    else if (repeat === 'all') chooseTrack(0)
    else setPlaying(false)
  }, [queue.length, repeat, shuffle, currentIndex, chooseTrack, seek, play])

  const previousTrack = useCallback(() => {
    if ((mediaRef.current?.currentTime ?? 0) > 3 || currentIndex === 0) { seek(0); return }
    chooseTrack(currentIndex - 1)
  }, [chooseTrack, currentIndex, seek])

  const togglePlayback = useCallback(() => {
    const element = mediaRef.current
    if (!element) return
    if (element.paused) void play()
    else element.pause()
  }, [play])

  const stop = useCallback(() => {
    const element = mediaRef.current
    if (element) {
      element.pause()
      element.removeAttribute('src')
      element.load()
    }
    if ('mediaSession' in navigator) {
      navigator.mediaSession.playbackState = 'none'
      navigator.mediaSession.metadata = null
    }
    onClose()
  }, [onClose])

  useEffect(() => {
    if (lastSession.current === sessionId) return
    lastSession.current = sessionId
    setCurrentPath(startPath)
    setExpanded(true)
  }, [sessionId, startPath])

  useEffect(() => {
    const element = mediaRef.current
    if (!element || !path) return
    scrubbing.current = false
    waveDrag.current = null
    pendingSeek.current = null
    setPosition(0)
    setMediaDuration(0)
    setPlaying(false)
    setLoading(true)
    setBuffering(false)
    setError('')
    setNotice('')
    element.src = explorerApi.streamPathUrl(path)
    element.load()
    void play()
    return () => { element.pause(); element.removeAttribute('src'); element.load() }
  }, [path, play])

  useEffect(() => {
    if (mediaRef.current) {
      mediaRef.current.defaultPlaybackRate = speed
      mediaRef.current.playbackRate = speed
    }
  }, [speed, path])

  useEffect(() => {
    if (current && reportedTrackPath.current !== current.path) {
      reportedTrackPath.current = current.path
      onTrackChangeRef.current?.(current)
    }
  }, [current]) // Queue additions must never restart the current track.

  useEffect(() => {
    const element = mediaRef.current
    return () => {
      if (element) { element.pause(); element.removeAttribute('src'); element.load() }
    }
  }, [])

  useEffect(() => {
    if (expanded) minimizeButtonRef.current?.focus({ preventScroll: true })
    else expandButtonRef.current?.focus({ preventScroll: true })
  }, [expanded])

  useEffect(() => {
    if (!path) return
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined
    async function loadWaveform() {
      try {
        const result = await explorerApi.waveform(path, controller.signal)
        if (controller.signal.aborted) return
        if ('status' in result) { timer = setTimeout(() => { void loadWaveform() }, 2000); return }
        if (!Array.isArray(result.peaks) || result.peaks.length === 0 || !Number.isFinite(result.duration)) throw new Error('waveform')
        setWaveform({ ...result, path })
      } catch {
        if (!controller.signal.aborted) setWaveformFailedPath(path)
      }
    }
    void loadWaveform()
    return () => { controller.abort(); if (timer) clearTimeout(timer) }
  }, [path])

  useEffect(() => {
    if (!('mediaSession' in navigator) || !path) return
    const session = navigator.mediaSession
    if ('MediaMetadata' in window) session.metadata = new MediaMetadata({ title: currentName, artist: de.appName })
    const handlers: [MediaSessionAction, MediaSessionActionHandler][] = [
      ['play', () => { void play() }], ['pause', () => mediaRef.current?.pause()],
      ['previoustrack', previousTrack], ['nexttrack', () => nextTrack()],
      ['seekbackward', (event) => seek((mediaRef.current?.currentTime ?? 0) - (event.seekOffset ?? 10))],
      ['seekforward', (event) => seek((mediaRef.current?.currentTime ?? 0) + (event.seekOffset ?? 10))],
      ['seekto', (event) => { if (event.seekTime !== undefined) seek(event.seekTime) }], ['stop', stop],
    ]
    for (const [action, handler] of handlers) {
      try { session.setActionHandler(action, handler) } catch { /* Action support differs across mobile browsers. */ }
    }
    return () => {
      for (const [action] of handlers) { try { session.setActionHandler(action, null) } catch { /* Unsupported action. */ } }
      session.metadata = null
    }
  }, [path, currentName, play, previousTrack, nextTrack, seek, stop])

  useEffect(() => {
    if (!('mediaSession' in navigator)) return
    navigator.mediaSession.playbackState = playing ? 'playing' : 'paused'
    if (duration > 0 && 'setPositionState' in navigator.mediaSession) {
      try { navigator.mediaSession.setPositionState({ duration, playbackRate: speed, position: Math.min(shownPosition, duration) }) } catch { /* Older browsers can reject position updates. */ }
    }
  }, [playing, duration, shownPosition, speed])

  function updatePosition() {
    if (!scrubbing.current && mediaRef.current) setPosition(mediaRef.current.currentTime)
  }

  function loadedMetadata() {
    const element = mediaRef.current
    if (!element) return
    setMediaDuration(Number.isFinite(element.duration) ? element.duration : 0)
    if (element.readyState >= 1) {
      setLoading(false)
      if (pendingSeek.current !== null) {
        try { element.currentTime = pendingSeek.current; pendingSeek.current = null } catch { /* Retry once metadata is ready. */ }
      }
    }
  }

  function retry() {
    const element = mediaRef.current
    if (!element) return
    pendingSeek.current = position
    setError('')
    setNotice('')
    setLoading(true)
    element.load()
    void play()
  }

  async function fullscreen() {
    try {
      if (document.fullscreenElement) await document.exitFullscreen()
      else if (panelRef.current?.requestFullscreen) await panelRef.current.requestFullscreen()
      else (mediaRef.current as SafariVideo | null)?.webkitEnterFullscreen?.()
    } catch { setNotice(de.playerDisplayError) }
  }

  async function pictureInPicture() {
    const element = mediaRef.current
    if (!(element instanceof HTMLVideoElement)) return
    try {
      if (document.pictureInPictureElement) await document.exitPictureInPicture()
      else await element.requestPictureInPicture()
    } catch { setNotice(de.playerDisplayError) }
  }

  function wavePointerDown(event: PointerEvent<HTMLDivElement>) {
    if (!duration || event.button !== 0) return
    event.preventDefault()
    event.currentTarget.setPointerCapture(event.pointerId)
    const rect = event.currentTarget.getBoundingClientRect()
    const value = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width)) * duration
    waveDrag.current = { id: event.pointerId, x: event.clientX, y: event.clientY, value }
    scrubbing.current = true
    setPosition(value)
  }

  function wavePointerMove(event: PointerEvent<HTMLDivElement>) {
    const drag = waveDrag.current
    if (!drag || drag.id !== event.pointerId) return
    const vertical = Math.abs(event.clientY - drag.y)
    const factor = vertical >= 120 ? 50 : vertical >= 50 ? 10 : 1
    const width = event.currentTarget.getBoundingClientRect().width
    drag.value = Math.max(0, Math.min(duration, drag.value + (event.clientX - drag.x) / width * duration / factor))
    drag.x = event.clientX
    setFineScrub(factor)
    setPosition(drag.value)
  }

  function finishWaveScrub(event: PointerEvent<HTMLDivElement>, cancel = false) {
    const drag = waveDrag.current
    if (!drag || drag.id !== event.pointerId) return
    waveDrag.current = null
    scrubbing.current = false
    setFineScrub(1)
    if (cancel) updatePosition()
    else seek(drag.value)
  }

  function waveKeyboard(event: KeyboardEvent<HTMLDivElement>) {
    const values: Record<string, number> = { ArrowLeft: position - 5, ArrowRight: position + 5, ArrowDown: position - 5, ArrowUp: position + 5, PageDown: position - 30, PageUp: position + 30, Home: 0, End: duration }
    if (!(event.key in values)) return
    event.preventDefault()
    seek(values[event.key])
  }

  const svgPeaks = useMemo(() => waveform?.peaks.map((peak, index, peaks) => {
    const height = Math.max(.4, Math.min(1, peak) * 48)
    const x = (index + .5) * 1200 / peaks.length
    return `M${x.toFixed(1)},${(50 - height).toFixed(1)}v${(height * 2).toFixed(1)}`
  }).join(''), [waveform])

  function removeFromQueue(index: number) {
    if (!onQueueChange) return
    const nextQueue = queue.filter((_, itemIndex) => itemIndex !== index)
    if (!nextQueue.length) { stop(); onQueueChange([]); return }
    if (index === currentIndex) setCurrentPath(nextQueue[Math.min(index, nextQueue.length - 1)].path)
    onQueueChange(nextQueue)
  }

  if (!current) return null
  const progress = duration ? shownPosition / duration * 100 : 0

  const mediaEvents = {
    onLoadedMetadata: loadedMetadata,
    onDurationChange: loadedMetadata,
    onTimeUpdate: updatePosition,
    onSeeking: updatePosition,
    onPlay: () => { setPlaying(true); setNotice(''); setError('') },
    onPause: () => setPlaying(false),
    onWaiting: () => setBuffering(true),
    onPlaying: () => { setBuffering(false); setLoading(false) },
    onCanPlay: () => { setBuffering(false); setLoading(false) },
    onEnded: () => nextTrack(true),
    onError: () => { if (mediaRef.current?.error) { setError(mediaError(mediaRef.current)); setLoading(false); setBuffering(false); setPlaying(false) } },
  }

  return <section ref={dialogRef} className={`player-root ${expanded ? 'player-expanded' : 'player-mini'}`}
    role={expanded ? 'dialog' : 'region'} aria-modal={expanded ? true : undefined}
    aria-label={expanded ? de.playerDialogName(current.name) : de.playerName} tabIndex={-1}>
    <div className="player-backdrop" aria-hidden="true" onClick={minimize} />
    <div className="player-panel" ref={panelRef}>
      <header className="player-header">
        <span className="player-kicker"><span className="player-stream-dot" />{de.playerStreaming}</span>
        <div className="player-header-buttons">
          <button type="button" className="player-button player-expand-button" ref={expandButtonRef} aria-label={de.playerExpand} title={de.playerExpand} onClick={() => setExpanded(true)}><Expand size={20} /></button>
          <button type="button" className="player-button player-minimize-button" ref={minimizeButtonRef} data-dialog-initial-focus aria-label={de.playerMinimize} title={de.playerMinimize} onClick={minimize}><ChevronDown size={22} /></button>
          <button type="button" className="player-button player-stop-button" aria-label={de.playerStop} title={de.playerStop} onClick={stop}><X size={22} /></button>
        </div>
      </header>

      <div className={`player-stage ${video ? 'player-video-stage' : 'player-audio-stage'}`}>
        {!video && <div className="player-artwork" aria-hidden="true"><Music2 size={44} /><AudioLines className={playing ? 'player-artwork-lines player-artwork-playing' : 'player-artwork-lines'} size={66} /></div>}
        {video
          ? <video ref={(element) => { mediaRef.current = element }} controls playsInline preload="metadata" {...mediaEvents} />
          : <audio ref={(element) => { mediaRef.current = element }} controls preload="metadata" {...mediaEvents} />}
      </div>

      <div className="player-track">
        <h2 id={titleId} title={current.name}>{current.name}</h2>
        <span className="player-track-meta">{de.playerQueuePosition(currentIndex + 1, queue.length)}</span>
      </div>

      <div className="player-wave-section player-expanded-only">
        <div className={`player-waveform ${duration ? '' : 'player-waveform-disabled'}`} role="slider" tabIndex={duration ? 0 : -1}
          aria-label={de.playerWaveform} aria-valuemin={0} aria-valuemax={duration} aria-valuenow={shownPosition}
          aria-valuetext={de.playerTime(formatMediaTime(shownPosition), formatMediaTime(duration))} aria-disabled={!duration}
          onPointerDown={wavePointerDown} onPointerMove={wavePointerMove} onPointerUp={(event) => finishWaveScrub(event)}
          onPointerCancel={(event) => finishWaveScrub(event, true)} onKeyDown={waveKeyboard}>
          {svgPeaks ? <>
            <svg className="player-wave-svg" viewBox="0 0 1200 100" preserveAspectRatio="none" aria-hidden="true"><path d={svgPeaks} /></svg>
            <div className="player-wave-progress" style={{ width: `${progress}%` }}><svg className="player-wave-svg" style={{ width: progress > 0 ? `${10000 / progress}%` : '100%' }} viewBox="0 0 1200 100" preserveAspectRatio="none" aria-hidden="true"><path d={svgPeaks} /></svg></div>
          </> : <span className="player-wave-placeholder">{waveformFailed ? de.playerWaveformUnavailable : <><LoaderCircle size={17} className="player-spinner" />{de.playerWaveformLoading}</>}</span>}
          <span className="player-wave-cursor" style={{ left: `${progress}%` }} aria-hidden="true" />
        </div>
        <p className="player-scrub-hint">{fineScrub > 1 ? de.playerFineScrub(fineScrub) : de.playerFineScrubHint}</p>
      </div>

      <div className="player-seek">
        <input type="range" min="0" max={duration || 1} step="0.01" value={Math.min(shownPosition, duration || 1)} disabled={!duration}
          aria-label={de.playerSeek} aria-valuetext={de.playerTime(formatMediaTime(shownPosition), formatMediaTime(duration))}
          onPointerDown={(event) => { scrubbing.current = true; event.currentTarget.setPointerCapture(event.pointerId) }}
          onChange={(event) => { const value = event.currentTarget.valueAsNumber; setPosition(value); if (!scrubbing.current) seek(value) }}
          onPointerUp={(event) => { scrubbing.current = false; seek(event.currentTarget.valueAsNumber) }}
          onPointerCancel={() => { scrubbing.current = false; updatePosition() }} />
        <div className="player-time-row"><span>{formatMediaTime(shownPosition)}</span><span>{formatMediaTime(duration)}</span></div>
      </div>

      <div className="player-transport">
        <button type="button" className="player-button player-previous" aria-label={de.playerPrevious} title={de.playerPrevious} onClick={previousTrack}><SkipBack size={22} /></button>
        <button type="button" className="player-button player-expanded-only player-jump" aria-label={de.playerBack10} title={de.playerBack10} onClick={() => seek((mediaRef.current?.currentTime ?? 0) - 10)} disabled={!duration}><RotateCcw size={22} /><small>10</small></button>
        <button type="button" className="player-button player-play" aria-label={playing ? de.playerPause : de.playerPlay} title={playing ? de.playerPause : de.playerPlay} onClick={togglePlayback}>{playing ? <Pause size={25} fill="currentColor" /> : <Play size={25} fill="currentColor" />}</button>
        <button type="button" className="player-button player-expanded-only player-jump" aria-label={de.playerForward10} title={de.playerForward10} onClick={() => seek((mediaRef.current?.currentTime ?? 0) + 10)} disabled={!duration}><RotateCw size={22} /><small>10</small></button>
        <button type="button" className="player-button player-next" aria-label={de.playerNext} title={de.playerNext} disabled={queue.length < 2 || (!shuffle && repeat !== 'all' && currentIndex === queue.length - 1)} onClick={() => nextTrack()}><SkipForward size={22} /></button>
      </div>

      <div className="player-status" aria-live="polite">
        {error ? <div className="player-error" role="alert"><span>{error}</span><button type="button" className="player-text-button" onClick={retry}>{de.playerRetry}</button></div>
          : notice ? <span>{notice}</span>
            : loading || buffering ? <span className="player-loading"><LoaderCircle size={16} className="player-spinner" />{loading ? de.playerLoading : de.playerBuffering}</span> : null}
      </div>

      <div className="player-options player-expanded-only">
        <button type="button" className="player-button player-jump30" aria-label={de.playerBack30} title={de.playerBack30} disabled={!duration} onClick={() => seek((mediaRef.current?.currentTime ?? 0) - 30)}>−30 s</button>
        <button type="button" className="player-button player-jump30" aria-label={de.playerForward30} title={de.playerForward30} disabled={!duration} onClick={() => seek((mediaRef.current?.currentTime ?? 0) + 30)}>+30 s</button>
        <button type="button" className="player-button" aria-label={de.playerShuffle} title={de.playerShuffle} aria-pressed={shuffle} onClick={() => setShuffle(!shuffle)}><Shuffle size={20} /></button>
        <button type="button" className="player-button" aria-label={repeatLabel} title={repeatLabel} aria-pressed={repeat !== 'off'} onClick={() => setRepeat(repeat === 'off' ? 'all' : repeat === 'all' ? 'one' : 'off')}>{repeat === 'one' ? <Repeat1 size={20} /> : <Repeat size={20} />}</button>
        <label className="player-speed"><span>{de.playerSpeed}</span><select aria-label={de.playerSpeed} value={speed} onChange={(event) => setSpeed(Number(event.target.value))}>{[.5, .75, 1, 1.25, 1.5, 1.75, 2].map((rate) => <option key={rate} value={rate}>{rate.toLocaleString('de-DE')}×</option>)}</select></label>
        {video && <button type="button" className="player-button" aria-label={de.playerFullscreen} title={de.playerFullscreen} onClick={() => { void fullscreen() }}><Maximize2 size={20} /></button>}
        {video && typeof document !== 'undefined' && document.pictureInPictureEnabled && <button type="button" className="player-button" aria-label={de.playerPictureInPicture} title={de.playerPictureInPicture} onClick={() => { void pictureInPicture() }}><PictureInPicture2 size={20} /></button>}
        <button type="button" className="player-button player-queue-toggle" aria-label={de.playerQueue} aria-expanded={showQueue} aria-controls={queueId} title={de.playerQueue} onClick={() => setShowQueue(!showQueue)}><ListMusic size={20} /><span>{queue.length}</span></button>
      </div>

      <div className="player-expanded-only player-queue" id={queueId} hidden={!showQueue}>
        <h3>{de.playerQueueCount(queue.length)}</h3>
        <ol>{queue.map((entry, index) => <li key={entry.path} className="player-queue-row">
          <button type="button" className={`player-queue-item ${index === currentIndex ? 'player-queue-current' : ''}`} aria-current={index === currentIndex ? 'true' : undefined} onClick={() => chooseTrack(index)}>
            <span className="player-queue-number">{index === currentIndex && playing ? <AudioLines size={18} /> : index + 1}</span><span className="player-queue-name">{entry.name}</span>{index === currentIndex && <span className="player-queue-playing"><Music2 size={16} /></span>}
          </button>
          {onQueueChange && <button type="button" className="player-button player-queue-remove" aria-label={de.playerRemoveFromQueue(entry.name)} title={de.playerRemoveFromQueue(entry.name)} onClick={() => removeFromQueue(index)}><X size={18} /></button>}
        </li>)}</ol>
      </div>
      <p className="player-browse-hint player-expanded-only"><ChevronDown size={15} />{de.playerContinueBrowsing}</p>
    </div>
  </section>
}
