import './styles/globals.css';
import './app/i18n';
import { bootstrap } from './app/bootstrap';

// A page's chunk is gone once the server is updated (hashed names): load the new build, once per build so a broken deploy cannot loop.
window.addEventListener('vite:preloadError', (event) => {
  const reloaded = 'nid-reloaded-for';
  const build = import.meta.env.VITE_NID_VERSION ?? 'dev';
  try {
    if (sessionStorage.getItem(reloaded) === build) return;
    sessionStorage.setItem(reloaded, build);
  } catch {
    return; // no storage, no guard against a loop: let the error show
  }
  event.preventDefault();
  window.location.reload();
});

void bootstrap({ root: document.getElementById('root')!, location: window.location });
