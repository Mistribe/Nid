// Applies the remembered theme and mode before the first render, so the page never flashes the default
// (frontend/design-system.md §7.6). It is an external file because the CSP refuses inline scripts.
(function () {
  var theme = 'neutre';
  var mode = 'system';
  try {
    theme = localStorage.getItem('nid.theme') || theme;
    mode = localStorage.getItem('nid.mode') || mode;
  } catch {
    // Storage refused: keep the defaults.
  }
  if (!/^[a-z][a-z0-9-]{0,31}$/.test(theme)) theme = 'neutre';
  var dark = mode === 'dark' || (mode !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.setAttribute('data-theme', theme);
  document.documentElement.classList.toggle('dark', dark);
})();
