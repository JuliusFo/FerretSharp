// Small DOM helpers that Blazor cannot do on its own.

/** block: 'nearest' keeps the element just visible (keyboard navigation), 'start' jumps to it (letter index). */
export function scrollIntoViewById(id, block = 'nearest') {
  document.getElementById(id)?.scrollIntoView({ block });
}
