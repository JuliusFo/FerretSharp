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

/**
 * Escape in a search field with text clears it and goes no further: stopped at the field, Blazor's handlers above it
 * (a popover's or dialog's Escape) never see it, so they act only on the next Escape. The input event updates the binding.
 */
export function clearOnEscape(input) {
  input?.addEventListener('keydown', e => {
    if (e.key !== 'Escape' || e.isComposing || input.value === '') return;
    e.stopPropagation();
    e.preventDefault();
    input.value = '';
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

export function focusById(id) {
  document.getElementById(id)?.focus();
}

/** Moves a fixed popup up if it reaches below the window (its height is only known once rendered). */
export function keepInViewport(id, margin = 8) {
  const element = document.getElementById(id);
  if (!element) return;
  const rect = element.getBoundingClientRect();
  if (rect.bottom > window.innerHeight - margin) {
    element.style.top = Math.max(margin, window.innerHeight - rect.height - margin) + 'px';
  }
}

/**
 * Drags the left edge of a side panel (the form beside the grid, WP-21) while the pointer is down: the width follows
 * the pointer, between min and maxFraction of the parent. Resolves with the final width when the pointer is released.
 */
export function dragWidth(handle, panel, pointerId, startX, min, maxFraction) {
  return new Promise(resolve => {
    const startWidth = panel.getBoundingClientRect().width;
    const max = Math.max(min, panel.parentElement.getBoundingClientRect().width * maxFraction);
    const widthAt = x => Math.round(Math.min(max, Math.max(min, startWidth + startX - x)));
    const move = e => { panel.style.width = widthAt(e.clientX) + 'px'; };
    const up = e => {
      handle.removeEventListener('pointermove', move);
      handle.removeEventListener('pointerup', up);
      handle.removeEventListener('pointercancel', up);
      if (handle.hasPointerCapture(pointerId)) handle.releasePointerCapture(pointerId);
      resolve(widthAt(e.clientX));
    };
    try {
      handle.setPointerCapture(pointerId);
    } catch {
      resolve(Math.round(startWidth)); // released before the capture: nothing to drag
      return;
    }
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', up);
    handle.addEventListener('pointercancel', up);
  });
}

/** Focuses the first enabled input, select or text area inside the element (a new row in the form). */
export function focusFirstInput(element) {
  element?.querySelector('input:not([disabled]):not([readonly]), select:not([disabled]), textarea:not([disabled]):not([readonly])')?.focus();
}

/** True if the element contains the focused element (Ctrl+F: the form's search or the grid's column jump). */
export function containsFocus(element) {
  return !!element && element.contains(document.activeElement);
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
