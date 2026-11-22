const TEXT_SIZE_KEY = 'windows-mobile-explorer:text-size'
const TEXT_SIZE_PERCENTAGES = [100, 125, 150, 175, 200]

function isAppleMobileDevice(): boolean {
  return /(?:iPhone|iPad|iPod)/i.test(navigator.userAgent) ||
    (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)
}

export function configureIOSDynamicType(): boolean {
  if (!isAppleMobileDevice() || !CSS.supports('font', '-apple-system-body')) return false

  document.documentElement.dataset.dynamicType = 'enabled'
  return true
}

function getIOSDynamicTypeBaseSize(): number {
  const probe = document.createElement('span')
  probe.setAttribute('aria-hidden', 'true')
  probe.dataset.dynamicTypeProbe = 'true'
  probe.style.cssText = 'position:fixed;left:-10000px;top:0;width:0;height:0;overflow:hidden;visibility:hidden;pointer-events:none;white-space:nowrap;font:-apple-system-body'
  probe.style.fontFamily = getComputedStyle(document.documentElement).fontFamily
  document.body.append(probe)
  const size = Number.parseFloat(getComputedStyle(probe).fontSize)
  probe.remove()
  return size
}

export function setAppTextScale(percentage: number): void {
  const root = document.documentElement
  const nativeBaseSize = root.dataset.dynamicType === 'enabled'
    ? getIOSDynamicTypeBaseSize()
    : 16
  const baseSize = Number.isFinite(nativeBaseSize) && nativeBaseSize > 0 ? nativeBaseSize : 16
  root.style.setProperty('--app-font-size', `${baseSize * percentage / 100}px`)
}

export function initializeAppTextScale(): void {
  configureIOSDynamicType()

  let percentage = 100
  try {
    const stored = Number(localStorage.getItem(TEXT_SIZE_KEY))
    if (TEXT_SIZE_PERCENTAGES.includes(stored)) percentage = stored
  } catch { /* use the accessible default when browser storage is unavailable */ }

  setAppTextScale(percentage)
}
