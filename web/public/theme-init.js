// Applies the remembered theme and mode before the first render, so the page never flashes the default
// (frontend/design-system.md §7.6). It is an external file because the CSP refuses inline scripts.
(function () {
  var theme = 'ardoise';
  var mode = 'system';
  var reduced = false;
  try {
    theme = localStorage.getItem('nid.theme') || theme;
    mode = localStorage.getItem('nid.mode') || mode;
    reduced = localStorage.getItem('nid.transparency') === 'reduce';
  } catch {
    // Storage refused: keep the defaults.
  }
  if (!/^[a-z][a-z0-9-]{0,31}$/.test(theme)) theme = 'ardoise';
  var dark = mode === 'dark' || (mode !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.setAttribute('data-theme', theme);
  document.documentElement.classList.toggle('dark', dark);
  if (reduced) document.documentElement.setAttribute('data-reduce-transparency', '');
})();
