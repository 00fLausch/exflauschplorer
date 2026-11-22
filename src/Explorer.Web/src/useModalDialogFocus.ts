import { useEffect, useRef } from 'react'

const FOCUSABLE_SELECTOR = 'button:not(:disabled), a[href], input:not(:disabled), select:not(:disabled), textarea:not(:disabled), audio[controls], video[controls], iframe, [tabindex]:not([tabindex="-1"])'
const MODAL_SELECTOR = '[role="dialog"][aria-modal="true"]'

export function useModalDialogFocus<T extends HTMLElement>(onClose: () => void, returnFocusTarget?: HTMLElement | null, externalControl?: HTMLElement | null) {
  const dialogRef = useRef<T>(null)
  const onCloseRef = useRef(onClose)

  useEffect(() => { onCloseRef.current = onClose }, [onClose])

  useEffect(() => {
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const topmostDialog = () => {
      const visibleDialogs = Array.from(document.querySelectorAll<HTMLElement>(MODAL_SELECTOR))
        .filter((dialog) => dialog.getClientRects().length > 0 && getComputedStyle(dialog).visibility !== 'hidden')
      return visibleDialogs.at(-1)
    }
    const focusableControls = () => [...(externalControl ? [externalControl] : []), ...Array.from(dialogRef.current?.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR) ?? [])]
      .filter((control) => !control.closest('[aria-hidden="true"]') && control.getClientRects().length > 0 && getComputedStyle(control).visibility !== 'hidden')
    const handleKeyDown = (event: KeyboardEvent) => {
      const dialog = dialogRef.current
      if (!dialog || topmostDialog() !== dialog) return

      if (event.key === 'Escape') {
        event.preventDefault()
        event.stopPropagation()
        onCloseRef.current()
        return
      }
      if (event.key !== 'Tab') return

      const controls = focusableControls()
      if (controls.length === 0) {
        event.preventDefault()
        dialog.focus()
        return
      }
      const first = controls[0]
      const last = controls[controls.length - 1]
      if (!dialog.contains(document.activeElement) && document.activeElement !== externalControl) {
        event.preventDefault()
        ;(event.shiftKey ? last : first).focus()
      } else if (event.shiftKey && document.activeElement === first) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus()
      }
    }

    document.addEventListener('keydown', handleKeyDown)
    const controls = focusableControls()
    const initialFocus = dialogRef.current?.querySelector<HTMLElement>('[data-dialog-initial-focus], [autofocus]')
      ?? controls.find((control) => Boolean((control as HTMLElement & { autofocus?: boolean }).autofocus))
      ?? controls[0]
      ?? dialogRef.current
    initialFocus?.focus({ preventScroll: true })

    return () => {
      document.removeEventListener('keydown', handleKeyDown)
      const target = returnFocusTarget?.isConnected ? returnFocusTarget : previousFocus
      const remainingTopmostDialog = topmostDialog()
      if (remainingTopmostDialog && (!target || !remainingTopmostDialog.contains(target))) return
      if (target?.isConnected) target.focus({ preventScroll: true })
    }
  }, [returnFocusTarget, externalControl])

  return dialogRef
}
