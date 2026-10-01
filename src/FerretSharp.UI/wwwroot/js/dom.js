// Small DOM helpers that Blazor cannot do on its own.

export function scrollIntoViewById(id) {
  document.getElementById(id)?.scrollIntoView({ block: 'nearest' });
}
