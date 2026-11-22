import { Download, FileImage, FileText, Film, Folder, HardDrive, Heart, LockKeyhole, Music2, PanelTop, Pin } from 'lucide-react'
import type { ReactNode } from 'react'
import { de } from './locales/de'
import type { Drive, FileEntry, KnownFolder, ShareRootSetting } from './types'
import { useModalDialogFocus } from './useModalDialogFocus'

interface NavigationProps {
  currentPath: string | null
  isFilesPage: boolean
  drives: Drive[]
  knownFolders: KnownFolder[]
  quickAccessFolders: FileEntry[]
  favorites: string[]
  sharedRoots: ShareRootSetting[]
  onNavigate: (path: string | null) => void
}

const pathKey = (path: string) => path.replaceAll('/', '\\').replace(/\\+$/, '').toLocaleLowerCase()

function knownFolderIcon(id: string) {
  const Icon = ({ desktop: PanelTop, downloads: Download, documents: FileText, pictures: FileImage, videos: Film, music: Music2 } as Record<string, typeof Folder>)[id] ?? Folder
  return <Icon size={17} />
}

export function FolderNavigation(props: NavigationProps) {
  const { currentPath, isFilesPage, drives, knownFolders, quickAccessFolders, favorites, sharedRoots, onNavigate } = props
  const knownPaths = new Set(knownFolders.map((folder) => pathKey(folder.path)))
  const shortcuts = quickAccessFolders.filter((folder) => !knownPaths.has(pathKey(folder.path)))
  const listedPaths = new Set([...knownPaths, ...shortcuts.map((folder) => pathKey(folder.path)), ...drives.map((drive) => pathKey(drive.name))])
  const extraRoots = sharedRoots.filter((root) => !listedPaths.has(pathKey(root.path)))
  const locationButton = (path: string | null, name: string, icon: ReactNode, available = true, readOnly = false) => (
    <button key={path ?? 'home'} type="button" className="folder-nav-link" title={path ?? name}
      aria-current={isFilesPage && (path === null ? currentPath === null : currentPath !== null && pathKey(path) === pathKey(currentPath)) ? 'location' : undefined}
      disabled={!available} onClick={() => onNavigate(path)}>
      <span className="folder-nav-icon" aria-hidden="true">{icon}</span><span className="folder-nav-name">{name}</span>
      {readOnly && <LockKeyhole size={12} aria-label={de.readOnly} />}
    </button>
  )
  return <nav className="folder-navigation" aria-label={de.uiOrdnernavigation}>
    {locationButton(null, de.thisPc, <HardDrive size={18} />)}
    <section className="folder-nav-section" aria-label={de.uiWichtigeOrdner}>
      <h2>Important folders</h2>
      {knownFolders.map((folder) => locationButton(folder.path, folder.name, knownFolderIcon(folder.id), folder.available))}
    </section>
    {shortcuts.length > 0 && <section className="folder-nav-section" aria-label={de.uiWindowsSchnellzugriff}>
      <h2><Pin size={12} aria-hidden="true" />Quick Access</h2>
      {shortcuts.map((folder) => locationButton(folder.path, folder.name, <Folder size={17} />))}
    </section>}
    {favorites.length > 0 && <section className="folder-nav-section" aria-label={de.uiFavoriten}>
      <h2>{de.uiFavoriten}</h2>
      {favorites.map((path) => locationButton(path, path.split(/[\\/]/).filter(Boolean).at(-1) ?? path, <Heart size={16} />))}
    </section>}
    <section className="folder-nav-section" aria-label={de.drives}>
      <h2>Drives</h2>
      {drives.map((drive) => locationButton(drive.name, `${drive.name.replace(/\\$/, '')} ${drive.displayName === drive.name.replace(/\\$/, '') ? '' : drive.displayName}${drive.format ? ` · ${drive.format}` : ''}`.trim(), <HardDrive size={17} />, drive.ready, drive.readOnly))}
    </section>
    {extraRoots.length > 0 && <section className="folder-nav-section" aria-label={de.uiFreigegebeneOrdner}>
      <h2>{de.uiFreigegebeneOrdner}</h2>
      {extraRoots.map((root) => locationButton(root.path, root.label || root.path, <Folder size={17} />))}
    </section>}
  </nav>
}

export function FolderNavigationDrawer({ onClose, toggleControl, ...props }: NavigationProps & { onClose: () => void; toggleControl: HTMLElement | null }) {
  const dialogRef = useModalDialogFocus<HTMLDivElement>(onClose, toggleControl, toggleControl)
  return <div className="folder-nav-backdrop" onClick={onClose}>
    <div ref={dialogRef} id="folder-navigation-drawer" className="folder-nav-drawer" role="dialog" aria-modal="true" aria-label={de.uiOrdnernavigation} aria-owns="folder-navigation-toggle" tabIndex={-1} onClick={(event) => event.stopPropagation()}>
      <div className="folder-nav-heading"><strong>{de.uiOrdnernavigation}</strong></div>
      <FolderNavigation {...props} onNavigate={(path) => { props.onNavigate(path); onClose() }} />
    </div>
  </div>
}
