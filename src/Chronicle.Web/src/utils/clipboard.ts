/**
 * Copies text to the clipboard. `navigator.clipboard` exists only in a secure context (https or localhost), so on the
 * plain-http address Chronicle is normally reached by on a home network it is undefined and a button that calls it does
 * nothing at all. Falls back to selecting a hidden textarea and `document.execCommand('copy')`, which still works there.
 * Resolves true when the text was copied.
 */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText && window.isSecureContext) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // fall through to the textarea route (permission denied, document not focused, ...)
  }

  const area = document.createElement('textarea')
  area.value = text
  area.setAttribute('readonly', '')
  area.style.position = 'fixed'
  area.style.top = '0'
  area.style.left = '-9999px'
  document.body.appendChild(area)
  try {
    area.select()
    area.setSelectionRange(0, text.length)
    return document.execCommand('copy')
  } catch {
    return false
  } finally {
    document.body.removeChild(area)
  }
}
