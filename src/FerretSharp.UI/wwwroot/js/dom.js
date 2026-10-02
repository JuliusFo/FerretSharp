// Small DOM helpers that Blazor cannot do on its own.

/** block: 'nearest' keeps the element just visible (keyboard navigation), 'start' jumps to it (letter index). */
export function scrollIntoViewById(id, block = 'nearest') {
  document.getElementById(id)?.scrollIntoView({ block });
}

export function focusAndSelect(element) {
  element?.focus();
  element?.select();
}

/** Selects the text only if the element still has the focus (it may have moved on, e.g. with Tab). */
export function selectIfFocused(element) {
  if (element && document.activeElement === element) element.select();
}

export function focusById(id) {
  document.getElementById(id)?.focus();
}

/** Returns false if the clipboard refused the write. */
export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
