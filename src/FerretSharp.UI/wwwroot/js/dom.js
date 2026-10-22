// Small DOM helpers that Blazor cannot do on its own.

import { TYPE_PAUSE, TypedText, pickTyped, typedCharOf } from './cellTyping.js';

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
 * A text starting with `keep` (the command prefix of the quick open, WP-38) is cleared back to it, and with only `keep`
 * left Escape goes on as in an empty field.
 */
export function clearOnEscape(input, keep = '') {
  input?.addEventListener('keydown', e => {
    if (e.key !== 'Escape' || e.isComposing || input.value === '' || (keep && input.value.trim() === keep)) return;
    e.stopPropagation();
    e.preventDefault();
    input.value = keep && input.value.trimStart().startsWith(keep) ? keep : '';
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

/**
 * Text pasted into a list value (an input with `data-list` inside `root`) is turned into the list by .NET (`ListFromPaste`:
 * lines or an IN list become `A; B`, null leaves the text as it is) and inserted where the selection was. The input event
 * updates the binding.
 */
export function listPaste(root, dotnet) {
  root?.addEventListener('paste', async e => {
    const input = e.target.closest?.('input[data-list]');
    const text = e.clipboardData?.getData('text/plain');
    if (!input || !text) return;
    e.preventDefault();
    const [start, end] = [input.selectionStart, input.selectionEnd];
    let value = text;
    try {
      value = await dotnet.invokeMethodAsync('ListFromPaste', text) ?? text;
    } catch (err) {
      console.debug('FerretSharp: list paste could not ask .NET', err);
    }
    input.setRangeText(value, start ?? input.value.length, end ?? input.value.length, 'end');
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

export function focusById(id) {
  document.getElementById(id)?.focus();
}

/** The size of the window, for placing a right-click menu Blazor opened (its mouse event does not carry it). */
export function viewportSize() {
  return [window.innerWidth, window.innerHeight];
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

/**
 * Drags something onto a drop target while the pointer is down (a filter row into a group, WP-50): the target under the
 * pointer – the closest element matching the selector – gets hoverClass, root gets 'dragging'. Resolves with the
 * target's data-drop when the pointer is released over one, else null. Pointer events, not HTML5 drag and drop.
 */
export function dragToDrop(root, pointerId, selector, hoverClass) {
  return new Promise(resolve => {
    let over = null;
    const move = e => {
      const target = document.elementFromPoint(e.clientX, e.clientY)?.closest(selector) ?? null;
      if (target !== over) {
        over?.classList.remove(hoverClass);
        target?.classList.add(hoverClass);
        over = target;
      }
    };
    let done = false;
    const onMove = e => ((e.buttons & 1) === 0 ? end(e) : move(e)); // the button came up unseen: no drop
    const end = e => {
      if (done) return;
      done = true;
      root.removeEventListener('pointermove', onMove);
      root.removeEventListener('pointerup', end);
      root.removeEventListener('pointercancel', end);
      root.removeEventListener('lostpointercapture', end);
      root.classList.remove('dragging');
      over?.classList.remove(hoverClass);
      if (root.hasPointerCapture(pointerId)) root.releasePointerCapture(pointerId);
      resolve(e.type === 'pointerup' && over ? over.dataset.drop ?? null : null);
    };
    try {
      root.setPointerCapture(pointerId);
    } catch {
      // released before the capture: nothing to drag
    }
    // A mouse button released while the call came here leaves no capture (and no exception): a later click elsewhere in
    // the bar must not drop the row there (review of WP-50).
    if (!root.hasPointerCapture(pointerId)) {
      resolve(null);
      return;
    }
    root.classList.add('dragging');
    root.addEventListener('pointermove', onMove);
    root.addEventListener('pointerup', end);
    root.addEventListener('pointercancel', end);
    root.addEventListener('lostpointercapture', end); // the bar went away, or the capture ended otherwise
  });
}

/** Focuses the first enabled input, select or text area inside the element (a new row in the form). */
export function focusFirstInput(element) {
  element?.querySelector('input:not([disabled]):not([readonly]), select:not([disabled]), textarea:not([disabled]):not([readonly])')?.focus();
}

const focusable = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/** Tab and Shift+Tab stay inside the dialog box (review 3.40.0, A15): they went on to the buttons behind the backdrop, and
 *  Enter there committed from the status bar while a confirmation was open. Tab inside Monaco or a grid that handles it
 *  stays theirs. The listener is on the backdrop and goes with it. */
export function trapFocus(box) {
  const backdrop = box?.parentElement;
  if (!backdrop || backdrop._focusTrap) {
    return;
  }

  backdrop._focusTrap = true;
  backdrop.addEventListener('keydown', e => {
    if (e.key !== 'Tab' || e.defaultPrevented || e.target.closest?.('.monaco-editor')) {
      return;
    }

    // Buttons with tabindex="-1" (the quick open's rows) are skipped by Tab: counted, they let the focus out past the last stop.
    const items = [...box.querySelectorAll(focusable)].filter(el => el.offsetParent !== null && el.tabIndex >= 0);
    const active = document.activeElement;
    if (items.length === 0) {
      e.preventDefault();
      box.focus();
    } else if (e.shiftKey && (active === items[0] || !items.includes(active))) {
      e.preventDefault();
      items[items.length - 1].focus();
    } else if (!e.shiftKey && (active === items[items.length - 1] || !items.includes(active))) {
      e.preventDefault();
      items[0].focus();
    }
  });
}

/**
 * Typing in the member lists (native selects) below root – the enum fields of the form: as in the grid's cell editor the
 * value first, then the label (cellTyping.js); the list's own search knows only the labels, digits found nothing. The pick
 * goes to Blazor (a change event) when typing pauses, on Enter or when the list loses the focus – not with every key, so
 * `1` `0` is one change, not two. The listeners are on root and go with it; lists rendered anew need nothing.
 */
export function typeInLists(root) {
  if (!root || root._typeInLists) {
    return;
  }

  root._typeInLists = true;
  const states = new WeakMap(); // select → { typed, pending, timer }
  const take = select => {
    const state = states.get(select);
    state?.typed.reset(); // the next key starts anew
    if (!state?.pending) return;
    clearTimeout(state.timer);
    state.pending = false;
    select.dispatchEvent(new Event('change', { bubbles: true }));
  };
  root.addEventListener('keydown', e => {
    const select = e.target;
    if (!(select instanceof HTMLSelectElement)) return;
    if (e.key === 'Enter') {
      take(select);
      return;
    }

    let state = states.get(select);
    if (!state) states.set(select, state = { typed: new TypedText(), pending: false, timer: 0 });
    const char = typedCharOf(e);
    const now = Date.now();
    // A space on its own opens the list as before; while typing it belongs to the text.
    if (char === null || (char === ' ' && !state.typed.active(now))) return;
    e.preventDefault();
    e.stopPropagation();
    const options = [...select.options].map(o => ({ value: o.value, label: o.textContent }));
    const value = pickTyped(options, state.typed.add(char, now), select.value);
    if (value === null || value === select.value) return;
    select.value = value;
    state.pending = true;
    clearTimeout(state.timer);
    state.timer = setTimeout(() => take(select), TYPE_PAUSE);
  }, true);
  root.addEventListener('focusout', e => {
    if (e.target instanceof HTMLSelectElement) take(e.target);
  }, true);
}

/** True if the element contains the focused element (Ctrl+F: the form's search or the grid's column jump). */
export function containsFocus(element) {
  return !!element && element.contains(document.activeElement);
}

/** Sets `className` on the element while its content does not fit its width without that class – the tab bar narrows
 *  pinned tabs then (WP-34). Measured without the class each time (in one layout, nothing is painted in between), so it
 *  does not flip back and forth. Follows the element's size and its content (tabs opened, closed, renamed); the observers
 *  go with the element. */
export function watchOverflow(element, className) {
  if (!element || element._overflowWatch) {
    return;
  }

  const update = () => {
    element.classList.remove(className);
    element.classList.toggle(className, element.scrollWidth > element.clientWidth + 1);
  };
  const resize = new ResizeObserver(update);
  const content = new MutationObserver(update);
  resize.observe(element);
  content.observe(element, { childList: true, subtree: true, characterData: true });
  element._overflowWatch = true;
  update();
}

/** Makes the tab bar easy to scroll, its scrollbar being thin: the mouse wheel scrolls it sideways (a plain wheel only
 *  scrolls vertically, the bar could only be reached with Shift), and a newly activated tab – opened, or picked by
 *  shortcut – is scrolled into view next to the pinned group, which stays at the left (sticky). Only a change of the
 *  active tab scrolls, so a bar scrolled away by hand stays put while the tabs re-render. The class `hover` shows the
 *  thumb: Chromium does not repaint it for `:hover` on the bar. */
export function scrollableTabs(element) {
  if (!element || element._scrollableTabs) {
    return;
  }

  element.addEventListener('mouseenter', () => element.classList.add('hover'));
  element.addEventListener('mouseleave', () => element.classList.remove('hover'));

  element.addEventListener('wheel', e => {
    const delta = Math.abs(e.deltaX) > Math.abs(e.deltaY) ? e.deltaX : e.deltaY;
    if (e.ctrlKey || delta === 0 || element.scrollWidth <= element.clientWidth) {
      return;
    }
    e.preventDefault();
    element.scrollLeft += delta;
  }, { passive: false });

  let shown = null;
  const reveal = () => {
    const tab = element.querySelector('.tab.active');
    if (tab === shown) {
      return;
    }
    shown = tab;
    if (!tab || tab.classList.contains('pinned')) {
      return;
    }
    const bar = element.getBoundingClientRect();
    const rect = tab.getBoundingClientRect();
    const left = bar.left + (element.querySelector('.tabs-pinned')?.offsetWidth ?? 0);
    const right = bar.left + element.clientWidth;
    if (rect.left < left) {
      element.scrollLeft -= left - rect.left;
    } else if (rect.right > right) {
      element.scrollLeft += Math.min(rect.right - right, rect.left - left);
    }
  };
  new MutationObserver(reveal).observe(element, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
  element._scrollableTabs = true;
  reveal();
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

/** The language of the page (WP-29): screen readers, spell checking and hyphenation follow it. The texts of Blazor's error
 *  bar come along: the bar shows when Blazor itself has failed, so it cannot render them. */
export function setDocumentLanguage(lang, errorText, reloadText) {
  document.documentElement.lang = lang;
  const bar = document.getElementById('blazor-error-ui');
  const message = bar?.querySelector('.message');
  const reload = bar?.querySelector('.reload');
  if (message) message.textContent = errorText;
  if (reload) reload.textContent = reloadText;
}
